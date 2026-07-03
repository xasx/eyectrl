namespace EyeCtrl.Core.Keyboard;

using System.Text.Json;

/// <summary>Loads and saves keyboard layout definitions from JSON files.</summary>
public static class KeyboardLayoutLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions SaveOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>Loads a keyboard layout from a JSON file.</summary>
    /// <param name="filePath">Path to the layout JSON file.</param>
    /// <returns>The deserialized <see cref="KeyboardLayout"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the file is missing or deserialization returns null.</exception>
    public static KeyboardLayout Load(string filePath)
    {
        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<KeyboardLayout>(json, Options)
               ?? throw new InvalidOperationException($"Failed to load keyboard layout from {filePath}");
    }

    /// <summary>Saves a keyboard layout to a JSON file.</summary>
    /// <param name="layout">The layout to persist.</param>
    /// <param name="filePath">Output file path.</param>
    public static void Save(KeyboardLayout layout, string filePath)
    {
        var json = JsonSerializer.Serialize(layout, SaveOptions);
        File.WriteAllText(filePath, json);
    }
}
