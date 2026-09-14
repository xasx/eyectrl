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
    public SettingsService(string filePath, SettingsWriter writer, ILogger<SettingsService>? logger = null)
    {
        _filePath = filePath;
        _writer = writer;
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
            var migratedJson = MigrateCursorFilterUnits(MigrateDragActions(json));
            if (!ReferenceEquals(json, migratedJson))
            {
                // Atomic, like Save: this is a write during startup, and a torn one would leave the
                // user's whole settings file unparseable — which Load answers with all-defaults, so
                // every configured value would silently revert.
                try
                {
                    var tmp = _filePath + ".tmp";
                    File.WriteAllText(tmp, migratedJson);
                    File.Move(tmp, _filePath, overwrite: true);
                }
                catch (Exception ex) { _logger?.LogWarning(ex, "Failed to persist migrated settings to {FilePath}", _filePath); }
            }
            var settings = JsonSerializer.Deserialize<EyeCtrlSettings>(migratedJson, JsonOptions) ?? new EyeCtrlSettings();

            // Migration: seed SelectionMode from deprecated ManualSelection.Enabled
            if (settings.SelectionMode == SelectionMode.None && settings.ManualSelection.Enabled)
            {
                settings.SelectionMode = SelectionMode.ManualSelection;
            }

            // The gaze-indicator visuals were unvalidated before they got ranges (2026-08-29); a
            // hand-edited file holding an out-of-range value must clamp, not trip the whole-file
            // default below — the file-wipe path punishes exactly the hand-editors the UI serves.
            ClampGazeIndicator(settings.ManualSelection);
            ClampCursorFilter(settings.Cursor);
            SanitizeLanguagePack(settings.Keyboard);

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

    private void ClampGazeIndicator(ManualSelectionSettings ms)
    {
        var (outer, dot, stroke, opacity, anim) = (
            ms.GazeIndicatorOuterDiameter, ms.GazeIndicatorCenterDotDiameter,
            ms.GazeIndicatorStrokeThickness, ms.GazeIndicatorOpacity,
            ms.GazeIndicatorResetAnimationMs);
        ms.GazeIndicatorOuterDiameter = Math.Clamp(outer, 20, 200);
        ms.GazeIndicatorCenterDotDiameter = Math.Clamp(dot, 2, 40);
        ms.GazeIndicatorStrokeThickness = Math.Clamp(stroke, 0.5, 10);
        ms.GazeIndicatorOpacity = Math.Clamp(opacity, 0.1, 1.0);
        ms.GazeIndicatorResetAnimationMs = Math.Clamp(anim, 0, 1000);
        if (ms.GazeIndicatorOuterDiameter != outer || ms.GazeIndicatorCenterDotDiameter != dot
            || ms.GazeIndicatorStrokeThickness != stroke || ms.GazeIndicatorOpacity != opacity
            || ms.GazeIndicatorResetAnimationMs != anim)
        {
            _logger?.LogInformation("Clamped out-of-range gaze-indicator settings to their valid ranges.");
        }
    }

    // The Stage 3 deadband knobs have no setter guard and no Validate() entry on purpose (see their XML
    // docs), so this is the ONLY thing standing between a hand-edited value and the stabiliser. Clamping
    // rather than rejecting is the whole design: Load answers a validation error by discarding the user's
    // entire settings file, and a brand-new setting is the likeliest one to be typed wrong.
    private void ClampCursorFilter(CursorSettings cursor)
    {
        var (radius, confirm) = (cursor.DeadbandRadiusPx, cursor.DeadbandConfirmSamples);
        cursor.DeadbandRadiusPx = Math.Clamp(radius, 0.0, 15.0);
        cursor.DeadbandConfirmSamples = Math.Clamp(confirm, 1, 5);
        if (cursor.DeadbandRadiusPx != radius || cursor.DeadbandConfirmSamples != confirm)
            _logger?.LogInformation("Clamped out-of-range cursor deadband settings to their valid ranges.");
    }

    // LanguagePack is a path ingredient (dictionary/trie reads, user-dictionary write), so a
    // hand-edited "..\..\evil" would read and write outside the intended trees. Reset to empty
    // (auto-detect) — same clamp-not-reject design as above: a validation error would wipe the
    // whole settings file over one string.
    private void SanitizeLanguagePack(KeyboardSettings keyboard)
    {
        if (KeyboardSettings.IsValidLanguagePack(keyboard.LanguagePack)) return;
        _logger?.LogWarning("Ignoring malformed keyboard language pack '{Lang}'; falling back to auto-detection.",
            keyboard.LanguagePack);
        keyboard.LanguagePack = "";
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
        using var mutex = new Mutex(false, @"Local\EyeCtrl.Settings.Writer");
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

    // ── Stage 3 filter retune migration ──────────────────────────────────────────────────────────
    //
    // WHY a migration is needed at all: Load deserialises straight onto the object graph, so a changed
    // default is invisible to every profile that already has the old one written out — and every profile
    // does, because Save serialises the whole object. Without this entry the retune would ship inert on
    // the one machine it was designed for.
    //
    // THE DETECTION RULE, and its two halves:
    //
    //   beta — the JSON key itself is the marker. The pixel-unit value lived under "oneEuroBeta"; the
    //   rescaled one lives under "oneEuroBetaPerDiagonal". Presence of the old key means "not yet
    //   migrated", which is durable, unambiguous and idempotent — unlike a value test, which would
    //   re-migrate a user who later dials the new beta down into the old key's range.
    //
    //   minCutoff — no key change is possible (the units are still Hz), so the rule is value-based:
    //   exactly the old default 5.0 means "never touched", anything else is the user's and is kept. It
    //   rides the beta key's marker, so it also runs exactly once. The known cost is a user who
    //   deliberately chose 5.0 and gets moved to 1.5; that is the trade the ticket asks for, and 5.0 was
    //   a bad value for everyone on this hardware.
    //
    // A user-changed beta is CONVERTED, not kept: the units changed underneath it, so preserving the
    // number would silently change the behaviour, while preserving the behaviour is what "preserve the
    // value the user chose" has to mean. The conversion is clamped into the new validated range here,
    // because a persisted out-of-range value makes the [ObservableProperty] setter throw during
    // deserialisation — which Load answers by discarding the whole file.
    private const string LegacyBetaKey = "oneEuroBeta";
    private const string BetaKey = "oneEuroBetaPerDiagonal";
    private const string MinCutoffKey = "oneEuroMinCutoff";
    private const string CursorSectionName = "cursor";
    // Both are exactly representable as doubles, so == against a JSON round-trip is safe.
    private const double LegacyDefaultBeta = 0.5;
    private const double LegacyDefaultMinCutoff = 5.0;

    private static string MigrateCursorFilterUnits(string json)
    {
        // Cheap reject. "oneEuroBetaPerDiagonal" does not contain the quoted old key, so an
        // already-migrated file never reaches the parse.
        if (!json.Contains($"\"{LegacyBetaKey}\"", StringComparison.Ordinal))
            return json;

        JsonObject? root;
        try { root = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return json; }   // unparseable: leave it to Load's own error path

        if (root?[CursorSectionName] is not JsonObject cursor)
            return json;
        if (!cursor.Remove(LegacyBetaKey, out var legacy) || legacy is not JsonValue legacyValue)
            return json;

        // A file carrying BOTH keys has already been migrated and then hand-edited (or written by a
        // mixed-version pair of processes). Drop the stale key and keep the new value untouched.
        if (cursor[BetaKey] is null && legacyValue.TryGetValue(out double betaPixelUnits))
        {
            var defaults = new CursorSettings();
            // A legal JSON number can overflow a double: "1e400" parses fine and TryGetValue hands back
            // positive Infinity (verified, not assumed). Math.Clamp would turn that into 60 — the
            // MAXIMUM, i.e. the least smoothing on the dial — which is a strange thing to hand someone
            // whose file was garbage. The default is the better answer, and it is also the only branch
            // that would save a NaN, which Math.Clamp passes through untouched and the setter's range
            // guard cannot reject (every comparison against NaN is false).
            cursor[BetaKey] = betaPixelUnits == LegacyDefaultBeta || !double.IsFinite(betaPixelUnits)
                ? defaults.OneEuroBetaPerDiagonal
                : Math.Clamp(betaPixelUnits * Processing.OneEuroFilter.SpeedNormalizationPixels, 0.0, 60.0);

            if (cursor[MinCutoffKey] is JsonValue cutoffValue
                && cutoffValue.TryGetValue(out double minCutoff)
                && minCutoff == LegacyDefaultMinCutoff)
            {
                cursor[MinCutoffKey] = defaults.OneEuroMinCutoff;
            }
        }

        return root.ToJsonString(JsonOptions);
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
