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

    /// <summary>
    /// Hands the live prediction-stack factory over. Called at keyboard initialization, where
    /// the factory is BRAND NEW with no current stack: that emptiness is load-bearing — a
    /// factory whose stack was built BEFORE a file rewrite made here (no factory attached at
    /// the time) would re-list the rewritten words from stale memory and write them back. Any
    /// future call site must preserve it (round-2 review N5).
    /// </summary>
    public void SetFactory(PredictionStackFactory factory) => _factory = factory;

    /// <summary>
    /// Learned WORDS: word-shaped entries that are not base-dictionary vocabulary, most-used
    /// first. The user file stores only the LEARNED DELTA over the base dictionary
    /// (SaveUserDictionary subtracts the base counts), so "learned" is the set difference; a
    /// not-yet-repaired file may still carry base rows, which this filter hides. Bare digits and
    /// single characters land in that file as typing noise (every keystroke the predictor sees is learned
    /// incidentally) and are not words a user can decide about — but hyphens, apostrophes and
    /// digits occur inside REAL words (Add Word learns CurrentInput verbatim: "don't",
    /// "covid-19", "mvp2"), so the shape must admit them or those learns become unremovable.
    /// When a live stack is attached, the LIVE dictionary is the source — it includes Add Word
    /// learns that have not been persisted yet, so the page can undo them in the same session.
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

    /// <summary>
    /// Word-shaped: longer than one character, at least two letters (kills bare digits and
    /// punctuation), and every character is a letter, digit, hyphen, or apostrophe — measured
    /// against all five shipped dictionaries and the keystroke-junk set, this keeps zero junk
    /// rows while recovering the digit/hyphen/apostrophe words (round-2 review, N1).
    /// </summary>
    private static bool IsLearnedWord(string key) =>
        key.Length > 1
        && key.Count(char.IsLetter) > 1
        && key.All(c => char.IsLetterOrDigit(c) || c is '-' or '\'' or '\u2019');

    /// <summary>
    /// Removes one learned word from the user file and the live stack. The live forget runs on
    /// every outcome — a file problem must not strand the word in predictor memory (the inverse
    /// of the resurrection rule). <see cref="DictionaryWriteResult.WriteFailed"/> is the only
    /// outcome the UI should raise an error for: the live view legitimately lists words that
    /// were never persisted (Add Word learns), so their file rewrite has nothing to remove.
    /// </summary>
    public DictionaryWriteResult ForgetWord(string word)
    {
        var result = RewriteUserFileWithout([word]);
        _factory?.ForgetWords([word]);
        return result;
    }

    /// <summary>Removes every learned word (file + live stack) in one pass; same return contract
    /// as <see cref="ForgetWord"/>.</summary>
    public DictionaryWriteResult ClearLearned()
    {
        var learned = GetLearnedWords();
        if (learned.Count == 0) return DictionaryWriteResult.NothingToRemove;
        var result = RewriteUserFileWithout(learned);
        _factory?.ForgetWords(learned);
        if (result == DictionaryWriteResult.Removed)
            Log.Information("Cleared {Count} learned dictionary words", learned.Count);
        return result;
    }

    /// <summary>
    /// What a user-file rewrite did. <see cref="Removed"/>: the file changed. NothingToRemove:
    /// the words were not in the file (missing/empty file, or a live-only Add Word learn) —
    /// normal, nothing to report. WriteFailed: the file was READABLE but the write threw
    /// (read-only, locked); the durable half did not happen and the user must hear about it.
    /// </summary>
    public enum DictionaryWriteResult
    {
        Removed,
        NothingToRemove,
        WriteFailed
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

    /// <summary>Rewrites the user file minus the given keys. NothingToRemove when nothing
    /// changed (missing/empty file, or none of the words were present); WriteFailed when the
    /// write itself threw — the read succeeded, so the file exists and is populated, but the
    /// durable change did not land. A plain write, matching what FileDictionaryProvider.Save
    /// itself does for the words file.
    /// </summary>
    private DictionaryWriteResult RewriteUserFileWithout(IReadOnlyList<string> words)
    {
        var (_, userPath) = Paths();
        var userWords = ReadUserFile(userPath);
        if (userWords.Count == 0) return DictionaryWriteResult.NothingToRemove;

        var removed = false;
        foreach (var word in words)
            removed |= userWords.Remove(word.ToLowerInvariant());
        if (!removed) return DictionaryWriteResult.NothingToRemove;

        try
        {
            File.WriteAllText(userPath, JsonSerializer.Serialize(userWords, s_writeOptions));
            return DictionaryWriteResult.Removed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The background build writes this same file from a pool thread; a transient lock
            // or a read-only file must not throw out of a dwell-activated modal callback.
            Log.Warning(ex, "Failed to rewrite the user dictionary at {Path}", userPath);
            return DictionaryWriteResult.WriteFailed;
        }
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
