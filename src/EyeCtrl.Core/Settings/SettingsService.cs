namespace EyeCtrl.Core.Settings;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

public class SettingsService
{
    private readonly string _filePath;
    private readonly ILogger<SettingsService>? _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private CancellationTokenSource? _debounceCts;
    private const int DebounceMs = 500;

    public SettingsService(string filePath, ILogger<SettingsService>? logger = null)
    {
        _filePath = filePath;
        _logger = logger;
    }

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
            catch (InvalidOperationException ex)
            {
                // Settings validation failed - log but don't crash
                _logger?.LogError(ex, "Failed to save settings due to validation errors");
            }
            catch (Exception ex)
            {
                // IO or serialization errors - log but don't crash
                _logger?.LogError(ex, "Failed to save settings to {FilePath}", _filePath);
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
                _logger?.LogWarning("Settings validation errors found in {FilePath}: {Errors}",
                    _filePath, string.Join("; ", validationErrors));

                // Return defaults for safety
                return new EyeCtrlSettings();
            }

            return settings;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error loading settings from {FilePath}", _filePath);
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
