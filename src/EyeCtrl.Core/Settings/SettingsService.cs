namespace EyeCtrl.Core.Settings;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using EyeCtrl.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Service for persisting and loading application settings as JSON.
/// Performs validation before saving and returns safe defaults on load failure.
/// </summary>
/// <remarks>
/// Single-writer ownership (spec, "Lost updates, not just corruption"): each instance is constructed
/// with a <see cref="SettingsWriter"/> identifying the owning process. <see cref="Save"/> re-reads
/// the file on disk under a cross-process mutex and preserves any section the caller doesn't own,
/// then atomic-writes via temp+rename. This prevents the App process from clobbering the Supervisor's
/// <see cref="SupervisionSettings"/> (and vice versa) when both share one settings file.
/// </remarks>
public class SettingsService
{
    private readonly string _filePath;
    private readonly ILogger<SettingsService>? _logger;
    private readonly SettingsWriter _writer;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>JSON property name (camelCase) of the Supervision section — the only section
    /// whose ownership is split across writers today.</summary>
    private const string SupervisionSectionName = "supervision";

    private CancellationTokenSource? _debounceCts;
    private readonly object _debounceLock = new();
    private const int DebounceMs = 500;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsService"/> class.
    /// </summary>
    /// <param name="filePath">Full path to the settings JSON file.</param>
    /// <param name="logger">Optional logger for diagnostic messages.</param>
    /// <remarks>Equivalent to passing <see cref="SettingsWriter.App"/>: the app is the only
    /// existing writer today, so the historical default preserves backward compatibility.</remarks>
    public SettingsService(string filePath, ILogger<SettingsService>? logger = null)
        : this(filePath, SettingsWriter.App, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance with an explicit writer identity for single-writer ownership.
    /// </summary>
    /// <param name="filePath">Full path to the settings JSON file.</param>
    /// <param name="writer">Which process owns this instance — controls which sections <see cref="Save"/> may persist.</param>
    /// <param name="logger">Optional logger for diagnostic messages.</param>
    /// <param name="writerMutexName">
    /// Cross-process writer mutex name. Defaults to <see cref="DefaultWriterMutexName"/>; a scoped
    /// instance passes a suffixed name so two instances with separate profiles do not serialise
    /// against each other, and neither blocks the default instance's writer.
    /// </param>
    public SettingsService(
        string filePath,
        SettingsWriter writer,
        ILogger<SettingsService>? logger = null,
        string? writerMutexName = null)
    {
        _filePath = filePath;
        _writer = writer;
        _logger = logger;
        _writerMutexName = writerMutexName ?? DefaultWriterMutexName;
    }

    /// <summary>
    /// Production name of the cross-process mutex serialising the read-merge-write in
    /// <see cref="Save"/>. Was an inline literal; hoisted so it can be scoped per instance and
    /// reported to the app's phase-2 instance-scope check.
    /// </summary>
    public const string DefaultWriterMutexName = @"Local\EyeCtrl.Settings.Writer";

    private readonly string _writerMutexName;

    /// <summary>The writer mutex name this instance binds. Read back by the phase-2 check.</summary>
    public string EffectiveWriterMutexName => _writerMutexName;

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
    /// <remarks>
    /// Single-writer ownership is enforced here: the file is re-read on disk under a cross-process
    /// mutex and sections the caller doesn't own are preserved, so a stale in-memory snapshot can
    /// never overwrite another process's section (spec, "Lost updates"). The write is atomic
    /// (temp file + rename) so a reader never sees a partially-written file.
    /// </remarks>
    public void Save(EyeCtrlSettings settings)
    {
        // Validate before saving (preserved from pre-ownership behavior).
        var validationErrors = settings.Validate();
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Cannot save invalid settings. Errors:\n{string.Join("\n", validationErrors)}");
        }

        // Cross-process mutual exclusion so the read-merge-write sequence is atomic across writers.
        using var mutex = new Mutex(false, _writerMutexName);
        bool owned = false;
        try
        {
            mutex.WaitOne();
            owned = true;

            // Read the on-disk state RAW (not via Load()) for section preservation. Load() returns
            // all-defaults when the file fails validation, which would let this writer silently reset
            // a section it doesn't own but must preserve (spec, "Lost updates"). See MergeForWriter.
            string? onDiskRaw = null;
            if (File.Exists(_filePath))
            {
                try { onDiskRaw = File.ReadAllText(_filePath); }
                catch { onDiskRaw = null; } // unreadable -> treat as absent (can't preserve)
            }

            var mergedJson = MergeForWriter(settings, onDiskRaw);

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, mergedJson);
            File.Move(tmp, _filePath, overwrite: true);
        }
        finally
        {
            if (owned) mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// Produces the JSON to persist after applying single-writer ownership. The caller-owned
    /// section comes from <paramref name="settings"/>; every other section is preserved from the
    /// raw on-disk JSON in <paramref name="onDiskRaw"/>. Implemented at the JSON-node level so it generalizes to any
    /// future owned section without hand-copying <see cref="EyeCtrlSettings"/>'s many properties
    /// (<see cref="EyeCtrlSettings"/> is a class, not a record, so a <c>with</c> expression is
    /// unavailable).
    /// </summary>
    private string MergeForWriter(EyeCtrlSettings settings, string? onDiskRaw)
    {
        var incoming = JsonSerializer.SerializeToNode(settings, JsonOptions)!.AsObject();

        // Preserve the not-owned section(s) from the RAW on-disk JSON — NOT from a validating Load(),
        // which substitutes all-defaults when any section fails validation and would silently reset a
        // section this writer must preserve. Parse defensively: a missing/corrupt file means we can't
        // preserve, and fall back to the incoming snapshot (or defaults).
        JsonObject? baseline = null;
        if (!string.IsNullOrWhiteSpace(onDiskRaw))
        {
            try { baseline = JsonNode.Parse(onDiskRaw) as JsonObject; }
            catch { baseline = null; }
        }

        if (_writer == SettingsWriter.Supervisor)
        {
            // Supervisor owns ONLY Supervision: start from the raw on-disk object so every App-owned
            // section is preserved verbatim (even values this schema no longer understands), then
            // overlay the incoming Supervision.
            var merged = baseline ?? JsonSerializer.SerializeToNode(new EyeCtrlSettings(), JsonOptions)!.AsObject();
            merged[SupervisionSectionName] = incoming[SupervisionSectionName]?.DeepClone();
            return merged.ToJsonString(JsonOptions);
        }

        // App owns everything EXCEPT Supervision: start from the incoming (valid) snapshot, then
        // overlay the raw on-disk Supervision so the supervisor's section survives even when the file
        // as a whole failed validation. If disk had no Supervision section, keep the incoming default.
        var onDiskSupervision = baseline?[SupervisionSectionName];
        if (onDiskSupervision != null)
            incoming[SupervisionSectionName] = onDiskSupervision.DeepClone();
        return incoming.ToJsonString(JsonOptions);
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
