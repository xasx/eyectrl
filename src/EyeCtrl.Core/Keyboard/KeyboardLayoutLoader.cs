namespace EyeCtrl.Core.Keyboard;

using System.Text.Json;

public static class KeyboardLayoutLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static KeyboardLayout Load(string filePath)
    {
        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<KeyboardLayout>(json, Options)
               ?? throw new InvalidOperationException($"Failed to load keyboard layout from {filePath}");
    }

    public static void Save(KeyboardLayout layout, string filePath)
    {
        var json = JsonSerializer.Serialize(layout, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        File.WriteAllText(filePath, json);
    }
}
