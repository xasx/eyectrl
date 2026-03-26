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

        try
        {
            var json = File.ReadAllText(_filePath);
            var settings = JsonSerializer.Deserialize<EyeCtrlSettings>(json, JsonOptions) ?? new EyeCtrlSettings();

            // Validate loaded settings
            var validationErrors = settings.Validate();
            if (validationErrors.Count > 0)
            {
                // Log validation errors but use defaults for invalid values
                Console.WriteLine($"Settings validation errors found in {_filePath}:");
                foreach (var error in validationErrors)
                {
                    Console.WriteLine($"  - {error}");
                }
                Console.WriteLine("Using default settings for invalid values.");

                // Return defaults for safety
                return new EyeCtrlSettings();
            }

            return settings;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading settings from {_filePath}: {ex.Message}");
            return new EyeCtrlSettings();
        }
    }

    public void Save(EyeCtrlSettings settings)
    {
        // Validate before saving
        var validationErrors = settings.Validate();
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Cannot save invalid settings. Errors:\n{string.Join("\n", validationErrors)}");
        }

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_filePath, json);
    }
}
