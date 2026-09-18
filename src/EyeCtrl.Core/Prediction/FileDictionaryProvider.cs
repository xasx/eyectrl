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

    /// <inheritdoc />
    public Dictionary<string, int> Load()
    {
        var words = new Dictionary<string, int>();

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
                        words[kv.Key] = kv.Value;

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

        if (File.Exists(_userDictionaryPath))
        {
            try
            {
                var json = File.ReadAllText(_userDictionaryPath);
                var userWords = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                if (userWords != null)
                    foreach (var kv in userWords)
                        words[kv.Key] = words.GetValueOrDefault(kv.Key) + kv.Value;
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
        var directory = Path.GetDirectoryName(_userDictionaryPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(words, s_writeOptions);
        File.WriteAllText(_userDictionaryPath, json);
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
