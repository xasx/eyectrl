namespace EyeCtrl.Core.Keyboard;

using System.Text.Json;

/// <summary>Loads emoji data (categories and skin tone support) from a JSON file.</summary>
public static class EmojiDataLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Loads emoji data from a JSON file.</summary>
    /// <param name="filePath">Path to the emoji data JSON file.</param>
    /// <returns>The populated <see cref="EmojiData"/> instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the file is missing or deserialization fails.</exception>
    public static EmojiData Load(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var raw = JsonSerializer.Deserialize<EmojiDataRaw>(json, Options)
                  ?? throw new InvalidOperationException($"Failed to load emoji data from {filePath}");

        return new EmojiData
        {
            Categories = raw.Categories,
            SkinToneBase = new HashSet<string>(raw.SkinToneBase ?? new List<string>())
        };
    }

    private class EmojiDataRaw
    {
        public List<EmojiCategory> Categories { get; set; } = new();
        public List<string> SkinToneBase { get; set; } = new();
    }
}
