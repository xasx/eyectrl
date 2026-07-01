namespace EyeCtrl.Core.Settings;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EyeCtrl.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Service for persisting and loading application settings as JSON.
/// Performs validation before saving and returns safe defaults on load failure.
/// </summary>
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
    private readonly object _debounceLock = new();
    private const int DebounceMs = 500;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsService"/> class.
    /// </summary>
    /// <param name="filePath">Full path to the settings JSON file.</param>
    /// <param name="logger">Optional logger for diagnostic messages.</param>
    public SettingsService(string filePath, ILogger<SettingsService>? logger = null)
    {
        _filePath = filePath;
        _logger = logger;
    }

    /// <summary>
    /// Saves settings to disk after a debounce delay. Repeated calls within <see cref="DebounceMs"/>
    /// cancel previous pending saves, ensuring only the latest state is written.
    /// </summary>
    /// <param name="settings">The settings object to persist.</param>
    public void SaveDebounced(EyeCtrlSettings settings)
    {
        CancellationToken token;
        lock (_debounceLock)
        {
            var oldCts = _debounceCts;
            _debounceCts = new CancellationTokenSource();
            token = _debounceCts.Token;

            oldCts?.Cancel();
            oldCts?.Dispose();
        }

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

    /// <summary>
    /// Loads settings from disk. Returns defaults if the file does not exist, is malformed,
    /// or contains validation errors.
    /// </summary>
    /// <returns>The loaded and validated <see cref="EyeCtrlSettings"/>, or a new default instance on failure.</returns>
    public EyeCtrlSettings Load()
    {
        if (!File.Exists(_filePath)) return new EyeCtrlSettings();

        try
        {
            var json = File.ReadAllText(_filePath);
            var migratedJson = MigrateDragActions(json);
            if (!ReferenceEquals(json, migratedJson))
            {
                try { File.WriteAllText(_filePath, migratedJson); }
                catch (Exception ex) { _logger?.LogWarning(ex, "Failed to persist migrated settings to {FilePath}", _filePath); }
            }
            var settings = JsonSerializer.Deserialize<EyeCtrlSettings>(migratedJson, JsonOptions) ?? new EyeCtrlSettings();

            // Migration: seed SelectionMode from deprecated ManualSelection.Enabled
            if (settings.SelectionMode == SelectionMode.None && settings.ManualSelection.Enabled)
            {
                settings.SelectionMode = SelectionMode.ManualSelection;
            }

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

    /// <summary>
    /// Validates and immediately saves settings to disk. Throws if validation fails.
    /// </summary>
    /// <param name="settings">The settings object to persist.</param>
    /// <exception cref="InvalidOperationException">Thrown when settings fail validation.</exception>
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

    private static string MigrateDragActions(string json)
    {
        if (!json.Contains("\"dragStart\"", StringComparison.OrdinalIgnoreCase) &&
            !json.Contains("\"dragEnd\"", StringComparison.OrdinalIgnoreCase))
            return json;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("actionMenu", out var actionMenu) ||
            !actionMenu.TryGetProperty("visibleActions", out var visibleActions))
            return json;

        var actions = new List<string>();
        bool modified = false;

        foreach (var item in visibleActions.EnumerateArray())
        {
            var value = item.GetString()!;
            if (string.Equals(value, "dragStart", StringComparison.OrdinalIgnoreCase))
            {
                actions.Add("drag");
                modified = true;
            }
            else if (string.Equals(value, "dragEnd", StringComparison.OrdinalIgnoreCase))
            {
                modified = true;
            }
            else
            {
                actions.Add(value);
            }
        }

        if (!modified)
            return json;

        if (!actions.Contains("drag"))
            actions.Add("drag");

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name == "actionMenu")
                {
                    writer.WritePropertyName("actionMenu");
                    writer.WriteStartObject();
                    foreach (var amProp in prop.Value.EnumerateObject())
                    {
                        if (amProp.Name == "visibleActions")
                        {
                            writer.WritePropertyName("visibleActions");
                            writer.WriteStartArray();
                            foreach (var a in actions)
                                writer.WriteStringValue(a);
                            writer.WriteEndArray();
                        }
                        else
                        {
                            amProp.WriteTo(writer);
                        }
                    }
                    writer.WriteEndObject();
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
