namespace EyeCtrl.Core.Prediction;

using System.Text.Json;

/// <summary>
/// Extension methods for FileDictionaryProvider to support bigram and phrase storage.
/// </summary>
public static class FileDictionaryProviderExtensions
{
    /// <summary>
    /// Loads bigram data from the user dictionary file.
    /// </summary>
    /// <param name="provider">The dictionary provider.</param>
    /// <returns>Dictionary mapping first words to their followers with frequencies.</returns>
    public static Dictionary<string, Dictionary<string, int>> LoadBigrams(this FileDictionaryProvider provider)
    {
        var path = GetBigramPath(provider);
        if (!File.Exists(path))
            return new Dictionary<string, Dictionary<string, int>>();

        var json = File.ReadAllText(path);
        var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(json);
        return data ?? new Dictionary<string, Dictionary<string, int>>();
    }

    /// <summary>
    /// Saves bigram data to the user dictionary file.
    /// </summary>
    /// <param name="provider">The dictionary provider.</param>
    /// <param name="bigrams">The bigram data to save.</param>
    public static void SaveBigrams(this FileDictionaryProvider provider, Dictionary<string, Dictionary<string, int>> bigrams)
    {
        var path = GetBigramPath(provider);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(bigrams, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Loads phrase data from the user dictionary file.
    /// </summary>
    /// <param name="provider">The dictionary provider.</param>
    /// <returns>Dictionary mapping phrase prefixes to complete phrases with frequencies.</returns>
    public static Dictionary<string, Dictionary<string, int>> LoadPhrases(this FileDictionaryProvider provider)
    {
        var path = GetPhrasePath(provider);
        if (!File.Exists(path))
            return new Dictionary<string, Dictionary<string, int>>();

        var json = File.ReadAllText(path);
        var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(json);
        return data ?? new Dictionary<string, Dictionary<string, int>>();
    }

    /// <summary>
    /// Saves phrase data to the user dictionary file.
    /// </summary>
    /// <param name="provider">The dictionary provider.</param>
    /// <param name="phrases">The phrase data to save.</param>
    public static void SavePhrases(this FileDictionaryProvider provider, Dictionary<string, Dictionary<string, int>> phrases)
    {
        var path = GetPhrasePath(provider);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(phrases, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private static string GetBigramPath(FileDictionaryProvider provider)
    {
        var basePath = provider.UserDictionaryPath;
        return basePath.Replace(".json", "_bigrams.json");
    }

    private static string GetPhrasePath(FileDictionaryProvider provider)
    {
        var basePath = provider.UserDictionaryPath;
        return basePath.Replace(".json", "_phrases.json");
    }
}
