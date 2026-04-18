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

public class FileDictionaryProvider : IDictionaryProvider
{
    private readonly string _baseDictionaryPath;
    private readonly string _userDictionaryPath;
    private readonly ILogger<FileDictionaryProvider>? _logger;
    private HashSet<string>? _abbreviations;

    public string UserDictionaryPath => _userDictionaryPath;

    /// <summary>
    /// Gets the abbreviations loaded from the base dictionary file.
    /// </summary>
    public IReadOnlySet<string> Abbreviations => _abbreviations ?? [];

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

    public Dictionary<string, int> Load()
    {
        var words = new Dictionary<string, int>();

        if (File.Exists(_baseDictionaryPath))
        {
            var json = File.ReadAllText(_baseDictionaryPath);

            // Try to deserialize as new format (with words and abbreviations)
            var data = JsonSerializer.Deserialize<DictionaryData>(json);

            // If Words is null, it's the old format (just a dictionary)
            if (data?.Words == null)
            {
                _logger?.LogInformation("Detected legacy dictionary format from {Path}, falling back to old format", _baseDictionaryPath);
                // Fall back to old format (just a dictionary)
                var baseWords = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                if (baseWords != null)
                    foreach (var kv in baseWords)
                        words[kv.Key] = kv.Value;
            }
            else
            {
                // New format - load words
                foreach (var kv in data.Words)
                    words[kv.Key] = kv.Value;

                // Load abbreviations if present
                if (data.Abbreviations != null && data.Abbreviations.Count > 0)
                {
                    _abbreviations = new HashSet<string>(data.Abbreviations, StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        if (File.Exists(_userDictionaryPath))
        {
            var json = File.ReadAllText(_userDictionaryPath);
            var userWords = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            if (userWords != null)
                foreach (var kv in userWords)
                    words[kv.Key] = words.GetValueOrDefault(kv.Key) + kv.Value;
        }

        return words;
    }

    public void Save(Dictionary<string, int> words)
    {
        var directory = Path.GetDirectoryName(_userDictionaryPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(words, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_userDictionaryPath, json);
    }

    /// <summary>
    /// Loads bigram data from the user dictionary file.
    /// </summary>
    /// <returns>Dictionary mapping first words to their followers with frequencies.</returns>
    public Dictionary<string, Dictionary<string, int>> LoadBigrams()
    {
        var path = GetBigramPath();
        if (!File.Exists(path))
            return [];

        try
        {
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(json);
            return data ?? [];
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "Failed to deserialize bigrams from {Path}", path);
            // Return empty if JSON is corrupted
            return [];
        }
    }

    /// <summary>
    /// Saves bigram data to the user dictionary file.
    /// </summary>
    /// <param name="bigrams">The bigram data to save.</param>
    public void SaveBigrams(Dictionary<string, Dictionary<string, int>> bigrams)
    {
        var path = GetBigramPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(bigrams, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Loads phrase data from the user dictionary file.
    /// </summary>
    /// <returns>Dictionary mapping phrase prefixes to complete phrases with frequencies.</returns>
    public Dictionary<string, Dictionary<string, int>> LoadPhrases()
    {
        var path = GetPhrasePath();
        if (!File.Exists(path))
            return [];

        try
        {
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(json);
            return data ?? [];
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "Failed to deserialize phrases from {Path}", path);
            // Return empty if JSON is corrupted
            return [];
        }
    }

    /// <summary>
    /// Saves phrase data to the user dictionary file.
    /// </summary>
    /// <param name="phrases">The phrase data to save.</param>
    public void SavePhrases(Dictionary<string, Dictionary<string, int>> phrases)
    {
        var path = GetPhrasePath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(phrases, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private string GetBigramPath() => _userDictionaryPath.Replace(".json", "_bigrams.json");

    private string GetPhrasePath() => _userDictionaryPath.Replace(".json", "_phrases.json");

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
