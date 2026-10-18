namespace EyeCtrl.Core.Prediction;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

/// <summary>
/// DTO for dictionary JSON file format.
/// </summary>
internal class DictionaryData
{
    [JsonPropertyName("words")]
    public Dictionary<string, int>? Words { get; set; }

    [JsonPropertyName("abbreviations")]
    public List<string>? Abbreviations { get; set; }
}

/// <summary>
/// File-based dictionary provider that loads a base dictionary plus a per-user learned dictionary.
/// Also supports loading/saving bigrams, phrases, and casing preferences as extension data.
/// </summary>
public class FileDictionaryProvider : IDictionaryProvider
{
    private readonly string _baseDictionaryPath;
    private readonly string _userDictionaryPath;
    private readonly ILogger<FileDictionaryProvider>? _logger;
    private HashSet<string>? _abbreviations;
    // The base counts this provider loaded: Save subtracts them so the user file only ever
    // holds the LEARNED delta. This copy is never handed to the predictor (which mutates the
    // merged dictionary as it learns).
    private Dictionary<string, int> _baseWords = new(StringComparer.OrdinalIgnoreCase);
    // Distinguishes "never loaded" from "loaded, and the base is empty": Save may be the first
    // caller (the background rebuild writes through a fresh provider), and it needs the base
    // counts to write the delta.
    private bool _baseLoaded;
    private bool _loadedInflated;
    private int _inflatedWordCount;

    /// <summary>Full path to the per-language user dictionary JSON file.</summary>
    public string UserDictionaryPath => _userDictionaryPath;

    /// <summary>
    /// Gets the abbreviations loaded from the base dictionary file.
    /// </summary>
    public IReadOnlySet<string> Abbreviations => _abbreviations ?? [];

    /// <summary>
    /// Initializes a new instance of the <see cref="FileDictionaryProvider"/> class.
    /// </summary>
    /// <param name="baseDictionaryPath">Path to the base (built-in) dictionary JSON file.</param>
    /// <param name="appDataDir">Application data directory for user dictionary storage.</param>
    /// <param name="languageCode">Two-letter ISO language code.</param>
    /// <param name="logger">Optional logger for diagnostic messages.</param>
    public FileDictionaryProvider(string baseDictionaryPath, string appDataDir, string languageCode, ILogger<FileDictionaryProvider>? logger = null)
    {
        _baseDictionaryPath = baseDictionaryPath;
        _logger = logger;
        var preferredSeparator = DetectSeparator(appDataDir);
        var normalizedBasePath = NormalizeSeparators(appDataDir, Path.DirectorySeparatorChar);
        var combinedPath = Path.Combine(normalizedBasePath, $"user_dictionary_{languageCode}.json");

        _userDictionaryPath = preferredSeparator == Path.DirectorySeparatorChar
            ? combinedPath
            : combinedPath.Replace(Path.DirectorySeparatorChar, preferredSeparator);
    }

    /// <summary>Reads only the base dictionary counts (and abbreviations).</summary>
    private void LoadBaseCounts()
    {
        _baseWords = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(_baseDictionaryPath))
        {
            try
            {
                var json = File.ReadAllText(_baseDictionaryPath);
                var data = JsonSerializer.Deserialize<DictionaryData>(json);

                if (data?.Words != null)
                {
                    // Load words
                    foreach (var kv in data.Words)
                        _baseWords[kv.Key] = kv.Value;

                    // Load abbreviations if present
                    if (data.Abbreviations != null && data.Abbreviations.Count > 0)
                    {
                        _abbreviations = new HashSet<string>(data.Abbreviations, StringComparer.OrdinalIgnoreCase);
                    }
                }
                else
                {
                    _logger?.LogWarning("Base dictionary at {Path} has null Words property, returning empty dictionary", _baseDictionaryPath);
                }
            }
            catch (JsonException ex)
            {
                _logger?.LogError(ex, "Failed to deserialize base dictionary from {Path}, returning empty dictionary", _baseDictionaryPath);
            }
            catch (IOException ex)
            {
                _logger?.LogError(ex, "Failed to read base dictionary from {Path}, returning empty dictionary", _baseDictionaryPath);
            }
        }
    }

    /// <summary>
    /// Save can be the first caller (the background rebuild writes through a fresh provider), so
    /// the base counts are loaded on demand for the delta. This path reads the base only: no
    /// user file, no inflation detection, no repair or backup.
    /// </summary>
    private void EnsureBaseLoaded()
    {
        if (_baseLoaded) return;
        LoadBaseCounts();
        _baseLoaded = true;
    }

    /// <inheritdoc />
    public Dictionary<string, int> Load()
    {
        LoadBaseCounts();
        _baseLoaded = true;
        _loadedInflated = false;
        _inflatedWordCount = 0;

        var baseCounts = _baseWords;
        var words = new Dictionary<string, int>(baseCounts, StringComparer.OrdinalIgnoreCase);

        if (File.Exists(_userDictionaryPath))
        {
            try
            {
                var json = File.ReadAllText(_userDictionaryPath);
                var userWords = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                if (userWords != null)
                {
                    if (IsInflated(userWords))
                    {
                        // The old save wrote the MERGED counts back, so whole copies of the
                        // base accumulated (the owner's file was base x 57-59). Remove the
                        // whole copies and keep the residual, which is what the user learned.
                        _loadedInflated = true;
                        foreach (var kv in userWords)
                        {
                            if (baseCounts.TryGetValue(kv.Key, out var baseCount) && baseCount > 0)
                            {
                                _inflatedWordCount++;
                                var learned = kv.Value % baseCount;
                                if (learned > 0) words[kv.Key] = baseCount + learned;
                            }
                            else
                            {
                                words[kv.Key] = words.GetValueOrDefault(kv.Key) + kv.Value;
                            }
                        }
                    }
                    else
                    {
                        foreach (var kv in userWords)
                            words[kv.Key] = words.GetValueOrDefault(kv.Key) + kv.Value;
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger?.LogError(ex, "Failed to deserialize user dictionary from {Path}", _userDictionaryPath);
            }
            catch (IOException ex)
            {
                _logger?.LogError(ex, "Failed to read user dictionary from {Path}", _userDictionaryPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger?.LogError(ex, "Access denied reading user dictionary from {Path}", _userDictionaryPath);
            }
        }

        return words;
    }

    /// <inheritdoc />
    public void Save(Dictionary<string, int> words)
    {
        // A fresh provider (the background rebuild's) has not loaded the base yet; without it
        // the delta would be the whole merged dictionary and the file would re-inflate.
        EnsureBaseLoaded();

        var directory = Path.GetDirectoryName(_userDictionaryPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // The first save after an inflated load repairs the file; keep the original once. If the
        // backup cannot be made, leave the file and the flag untouched so a later save retries:
        // every load repairs in memory, and not writing cannot inflate the file further.
        if (_loadedInflated)
        {
            if (!BackupInflatedFile()) return;
            _loadedInflated = false;
        }

        // Only what the user added on top of the base: whole base copies are never written back.
        var delta = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in words)
        {
            var learned = kv.Value - _baseWords.GetValueOrDefault(kv.Key);
            if (learned > 0) delta[kv.Key] = learned;
        }

        var json = JsonSerializer.Serialize(delta, s_writeOptions);
        File.WriteAllText(_userDictionaryPath, json);
    }

    /// <summary>
    /// True when at least 90% of the base words appear in the user file with at least twice
    /// their base count. A genuine delta file only holds words the user typed, so it can never
    /// trip this; a rare base word with a large delta is far below the threshold.
    /// </summary>
    private bool IsInflated(Dictionary<string, int> userWords)
    {
        if (_baseWords.Count == 0 || userWords.Count == 0) return false;

        var multiplied = 0;
        foreach (var (word, baseCount) in _baseWords)
        {
            if (baseCount > 0 && userWords.TryGetValue(word, out var userCount)
                && userCount >= 2L * baseCount)
            {
                multiplied++;
            }
        }

        return multiplied * 10L >= _baseWords.Count * 9L;
    }

    private bool BackupInflatedFile()
    {
        var backupPath = _userDictionaryPath + ".bak-inflated";

        if (File.Exists(backupPath))
        {
            _logger?.LogInformation(
                "Repaired the inflated user dictionary ({Words} base words carried whole copies); an existing backup was kept at {Backup}",
                _inflatedWordCount, backupPath);
            return true;
        }

        try
        {
            if (!File.Exists(_userDictionaryPath))
            {
                _logger?.LogInformation(
                    "Repaired the inflated user dictionary ({Words} base words carried whole copies); the user file no longer exists, so there was nothing to back up",
                    _inflatedWordCount);
                return true;
            }

            File.Copy(_userDictionaryPath, backupPath);

            _logger?.LogInformation(
                "Repaired the inflated user dictionary ({Words} base words carried whole copies); the original is at {Backup}",
                _inflatedWordCount, backupPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex,
                "Could not back up the inflated user dictionary to {Backup}; keeping the file unchanged",
                backupPath);
            return false;
        }
    }

    private static readonly JsonSerializerOptions s_writeOptions = new() { WriteIndented = true };

    private string GetExtensionPath(string fileSuffix)
    {
        var directory = Path.GetDirectoryName(_userDictionaryPath);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(_userDictionaryPath);
        var fileName = fileNameWithoutExtension + fileSuffix;
        return string.IsNullOrEmpty(directory) ? fileName : Path.Combine(directory, fileName);
    }

    private T LoadJson<T>(string fileSuffix) where T : class, new()
    {
        var path = GetExtensionPath(fileSuffix);
        if (!File.Exists(path))
            return new T();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json) ?? new T();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger?.LogError(ex, "Failed to load {Path}, returning empty data", path);
            return new T();
        }
    }

    private void SaveJson<T>(string fileSuffix, T data)
    {
        var path = GetExtensionPath(fileSuffix);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Atomic write (review finding 9): a plain WriteText overwrite can be observed
        // half-written by a concurrent reader (the background prediction-stack build loads
        // these files on the pool thread while another rebuild's head-of-task write runs).
        // Serialize to a temp file in the SAME directory, then File.Replace swaps it in
        // atomically. File.Replace requires the target to exist; first-write uses Move.
        var json = JsonSerializer.Serialize(data, s_writeOptions);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        try
        {
            if (File.Exists(path))
                File.Replace(temp, path, destinationBackupFileName: null);
            else
                File.Move(temp, path);
        }
        catch (IOException) when (File.Exists(temp))
        {
            // Replace can fail transiently (antivirus locks, a reader holding the target on
            // platforms without share-delete). Fall back to the direct overwrite rather than
            // losing the save; the reader's LoadJson already handles IO errors.
            File.Delete(temp);
            File.WriteAllText(path, json);
        }
    }

    /// <summary>
    /// Loads bigram data from the user dictionary file.
    /// </summary>
    /// <returns>Dictionary mapping first words to their followers with frequencies.</returns>
    public Dictionary<string, Dictionary<string, int>> LoadBigrams()
        => LoadJson<Dictionary<string, Dictionary<string, int>>>("_bigrams.json");

    /// <summary>
    /// Saves bigram data to the user dictionary file.
    /// </summary>
    /// <param name="bigrams">The bigram data to save.</param>
    public void SaveBigrams(Dictionary<string, Dictionary<string, int>> bigrams)
        => SaveJson("_bigrams.json", bigrams);

    /// <summary>
    /// Phase 8: Loads bigram timestamps from the user dictionary file.
    /// </summary>
    /// <returns>Dictionary mapping first words to their followers' timestamps.</returns>
    public Dictionary<string, Dictionary<string, DateTimeOffset>> LoadBigramTimestamps()
        => LoadJson<Dictionary<string, Dictionary<string, DateTimeOffset>>>("_bigrams_timestamps.json");

    /// <summary>
    /// Phase 8: Saves bigram timestamps to the user dictionary file.
    /// </summary>
    /// <param name="timestamps">The timestamp data to save.</param>
    public void SaveBigramTimestamps(Dictionary<string, Dictionary<string, DateTimeOffset>> timestamps)
        => SaveJson("_bigrams_timestamps.json", timestamps);

    /// <summary>
    /// Loads phrase data from the user dictionary file.
    /// </summary>
    /// <returns>Dictionary mapping phrase prefixes to complete phrases with frequencies.</returns>
    public Dictionary<string, Dictionary<string, int>> LoadPhrases()
        => LoadJson<Dictionary<string, Dictionary<string, int>>>("_phrases.json");

    /// <summary>
    /// Saves phrase data to the user dictionary file.
    /// </summary>
    /// <param name="phrases">The phrase data to save.</param>
    public void SavePhrases(Dictionary<string, Dictionary<string, int>> phrases)
        => SaveJson("_phrases.json", phrases);

    /// <summary>
    /// Phase 8: Loads phrase timestamps from the user dictionary file.
    /// </summary>
    /// <returns>Dictionary mapping phrase prefixes to their continuations' timestamps.</returns>
    public Dictionary<string, Dictionary<string, DateTimeOffset>> LoadPhraseTimestamps()
        => LoadJson<Dictionary<string, Dictionary<string, DateTimeOffset>>>("_phrases_timestamps.json");

    /// <summary>
    /// Phase 8: Saves phrase timestamps to the user dictionary file.
    /// </summary>
    /// <param name="timestamps">The timestamp data to save.</param>
    public void SavePhraseTimestamps(Dictionary<string, Dictionary<string, DateTimeOffset>> timestamps)
        => SaveJson("_phrases_timestamps.json", timestamps);

    /// <summary>
    /// Loads casing preferences from the user dictionary file.
    /// </summary>
    /// <returns>Dictionary mapping lowercase words to casing variants with frequencies.</returns>
    public Dictionary<string, Dictionary<string, int>> LoadCasingPreferences()
        => LoadJson<Dictionary<string, Dictionary<string, int>>>("_casing.json");

    /// <summary>
    /// Saves casing preferences to the user dictionary file.
    /// </summary>
    /// <param name="casingPreferences">The casing preference data to save.</param>
    public void SaveCasingPreferences(Dictionary<string, Dictionary<string, int>> casingPreferences)
        => SaveJson("_casing.json", casingPreferences);

    private static string NormalizeSeparators(string path, char separator)
    {
        return path.Replace('\\', separator).Replace('/', separator);
    }

    private static char DetectSeparator(string path)
    {
        var lastBackslashIndex = path.LastIndexOf('\\');
        var lastSlashIndex = path.LastIndexOf('/');

        if (lastBackslashIndex > lastSlashIndex)
            return '\\';
        if (lastSlashIndex > lastBackslashIndex)
            return '/';

        return Path.DirectorySeparatorChar;
    }
}
