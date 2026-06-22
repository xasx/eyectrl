namespace EyeCtrl.Core.Prediction;

/// <summary>Contract for loading and saving word-frequency dictionaries used by word prediction.</summary>
public interface IDictionaryProvider
{
    /// <summary>Loads the word-frequency dictionary from the provider's data source.</summary>
    /// <returns>Dictionary mapping words (lowercase) to their frequency counts.</returns>
    Dictionary<string, int> Load();
    /// <summary>Persists the word-frequency dictionary to the provider's data store.</summary>
    /// <param name="words">Dictionary mapping words to frequency counts.</param>
    void Save(Dictionary<string, int> words);
}
