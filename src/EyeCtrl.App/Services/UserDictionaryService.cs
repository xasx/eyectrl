namespace EyeCtrl.App.Services;

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using Serilog;

/// <summary>
/// The dictionary-management half of prediction controls (#22, OptiKey's dictionary console
/// analog): lists the words EyeCtrl has LEARNED (present in the per-language user dictionary
/// file but not in the shipped base dictionary) and removes them on request — from the file AND
/// from the live prediction stack via <see cref="PredictionStackFactory.ForgetWords"/>, which
/// also guards the in-flight-rebuild resurrection window.
/// </summary>
/// <remarks>
/// <para>
/// Path arithmetic mirrors <see cref="PredictionStackFactory"/>'s Build exactly (same language
/// sanitize, same file names) so the page always lists the dictionary the live stack actually
/// loaded — including the empty-language-code degradation.
/// </para>
/// <para>
/// The factory arrives LATE (SetFactory, the SetModelServices pattern): it is constructed per
/// keyboard initialization inside KeyboardCoordinator, not DI. Before then the settings window
/// cannot be open, so the late bind is safe; ForgetWord's live half no-ops until set.
/// </para>
/// <para>
/// All IO is small (words-only JSON) and happens on the settings UI thread, like every other
/// settings-page action.
/// </para>
/// </remarks>
public sealed class UserDictionaryService
{
    private readonly EyeCtrlSettings _settings;
    private readonly string _appDataDir;
    private readonly string _baseDir;
    private PredictionStackFactory? _factory;

    public UserDictionaryService(EyeCtrlSettings settings, string appDataDir, string? baseDir = null)
    {
        _settings = settings;
        _appDataDir = appDataDir;
        _baseDir = baseDir ?? AppDomain.CurrentDomain.BaseDirectory;
    }

    /// <summary>Hands the live prediction-stack factory over. Called at keyboard initialization.</summary>
    public void SetFactory(PredictionStackFactory factory) => _factory = factory;

    /// <summary>
    /// Learned WORDS: multi-letter, letters-only entries that are not base-dictionary vocabulary,
    /// most-used first. The user file stores the MERGED dictionary (SaveUserDictionary writes
    /// predictor state wholesale), so "learned" is the set difference; single characters and
    /// punctuation land in that file as typing noise (every keystroke the predictor sees is
    /// learned incidentally) and are not dictionary words. When a live stack is attached, the
    /// LIVE dictionary is the source — it includes Add Word learns that have not been persisted
    /// yet, so the page can undo them in the same session.
    /// </summary>
    public IReadOnlyList<string> GetLearnedWords()
    {
        var (basePath, userPath) = Paths();
        var baseWords = ReadBaseWords(basePath);

        IEnumerable<KeyValuePair<string, int>> source =
            _factory?.CurrentWordPredictor is { } live ? live.WordFrequencies : ReadUserFile(userPath);

        return source
            .Where(kv => IsLearnedWord(kv.Key) && !baseWords.Contains(kv.Key))
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <summary>Two or more letters — a word a user can recognize and decide about.</summary>
    private static bool IsLearnedWord(string key) =>
        key.Length > 1 && key.All(char.IsLetter);

    /// <summary>
    /// Removes one learned word from the user file and the live stack. Returns whether the FILE
    /// rewrite happened (false: file missing, empty, or unreadable). The live forget runs even
    /// then — a corrupt file must not strand the word in predictor memory (the inverse of the
    /// resurrection rule).
    /// </summary>
    public bool ForgetWord(string word)
    {
        var removed = RewriteUserFileWithout([word]);
        _factory?.ForgetWords([word]);
        return removed;
    }

    /// <summary>Removes every learned word (file + live stack) in one pass; same return contract
    /// as <see cref="ForgetWord"/>.</summary>
    public bool ClearLearned()
    {
        var learned = GetLearnedWords();
        if (learned.Count == 0) return true;
        var removed = RewriteUserFileWithout(learned);
        _factory?.ForgetWords(learned);
        if (removed)
            Log.Information("Cleared {Count} learned dictionary words", learned.Count);
        return removed;
    }

    /// <summary>Same arithmetic as PredictionStackFactory.Build: sanitize, then both file paths.</summary>
    private (string BasePath, string UserPath) Paths()
    {
        var langCode = KeyboardSettings.IsValidLanguagePack(_settings.Keyboard.LanguagePack)
            ? _settings.Keyboard.LanguagePack
            : "";
        var basePath = Path.Combine(_baseDir, "Resources", "Dictionaries", $"{langCode}.json");
        var provider = new FileDictionaryProvider(basePath, _appDataDir, langCode);
        return (basePath, provider.UserDictionaryPath);
    }

    private static Dictionary<string, int> ReadUserFile(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Failed to read the user dictionary at {Path}; treating as empty", path);
            return [];
        }
    }

    /// <summary>Rewrites the user file minus the given keys. False when nothing changed.
    /// A plain write, matching what FileDictionaryProvider.Save itself does for the words file.
    /// </summary>
    private bool RewriteUserFileWithout(IReadOnlyList<string> words)
    {
        var (_, userPath) = Paths();
        var userWords = ReadUserFile(userPath);
        if (userWords.Count == 0) return false;

        var removed = false;
        foreach (var word in words)
            removed |= userWords.Remove(word.ToLowerInvariant());
        if (!removed) return false;

        File.WriteAllText(userPath, JsonSerializer.Serialize(userWords, s_writeOptions));
        return true;
    }

    private static readonly JsonSerializerOptions s_writeOptions = new() { WriteIndented = true };

    private static HashSet<string> ReadBaseWords(string basePath)
    {
        if (!File.Exists(basePath)) return [];
        try
        {
            var data = JsonSerializer.Deserialize<BaseDictionaryData>(File.ReadAllText(basePath));
            return data?.Words is null ? [] : new HashSet<string>(data.Words.Keys);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Failed to read the base dictionary at {Path}; listing all user-file words", basePath);
            return [];
        }
    }

    private sealed class BaseDictionaryData
    {
        [JsonPropertyName("words")]
        public Dictionary<string, int>? Words { get; set; }
    }
}
