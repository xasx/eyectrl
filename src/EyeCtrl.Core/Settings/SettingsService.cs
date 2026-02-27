namespace EyeCtrl.Core.Settings;
using System.Text.Json;
public class SettingsService
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SettingsService(string filePath) => _filePath = filePath;

    public EyeCtrlSettings Load()
    {
        if (!File.Exists(_filePath)) return new EyeCtrlSettings();
        var json = File.ReadAllText(_filePath);
        return JsonSerializer.Deserialize<EyeCtrlSettings>(json, JsonOptions) ?? new EyeCtrlSettings();
    }

    public void Save(EyeCtrlSettings settings)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_filePath, json);
    }
}
