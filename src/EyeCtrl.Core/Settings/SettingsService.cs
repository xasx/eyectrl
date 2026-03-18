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

    private CancellationTokenSource? _debounceCts;
    private const int DebounceMs = 500;

    public SettingsService(string filePath) => _filePath = filePath;

    public void SaveDebounced(EyeCtrlSettings settings)
    {
        // Cancel and dispose old CancellationTokenSource to prevent resource leak
        var oldCts = _debounceCts;
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        // Dispose old CTS after extracting the new token to avoid race conditions
        oldCts?.Cancel();
        oldCts?.Dispose();

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceMs, token);
                if (!token.IsCancellationRequested)
                    Save(settings);
            }
            catch (TaskCanceledException)
            {
                // Expected when debounce is superseded by a new save request
            }
        });
    }

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
