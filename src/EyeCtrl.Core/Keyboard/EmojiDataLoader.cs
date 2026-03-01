namespace EyeCtrl.Core.Keyboard;

using System.Text.Json;

public static class EmojiDataLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

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
