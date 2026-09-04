namespace EyeCtrl.Core.Adaptation;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// Load/save for <see cref="AdaptationState"/> at <c>%APPDATA%/EyeCtrl/adaptation-state.json</c> (§7.4):
/// atomic temp-file + rename on write (the <c>SettingsService.Save</c> pattern, minus its cross-process
/// mutex — the app is the single writer; the supervisor does not write this file, §14), validated on
/// load (the <c>CalibrationService.Load</c> pattern): corrupt JSON, a version mismatch, or a missing
/// file yield <see cref="AdaptationState.Empty"/>; individually out-of-band entries are dropped while
/// valid entries survive. Never throws — a rejected file means "no learned offsets", not a broken app.
/// </summary>
public static class AdaptationStateFile
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Case-insensitive read (review round 1, finding 2): Save writes camelCase property names and
    // System.Text.Json matches case-SENSITIVELY by default, so without this a file this build just
    // wrote would deserialize Version to 0 and be rejected as a version mismatch — persistence dead
    // behind green-enough tests. Same fix as HeartbeatPipeServer / CalibrationService's s_readOptions.
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class StateDto
    {
        public int Version { get; set; }
        public DateTimeOffset SavedAtUtc { get; set; }
        public Dictionary<string, double> Learned { get; set; } = new();
    }

    /// <summary>Loads and validates the state file. Any failure degrades to <see cref="AdaptationState.Empty"/>.</summary>
    /// <param name="path">Full path to the state file.</param>
    /// <param name="logger">Optional logger for rejected-file diagnostics.</param>
    public static AdaptationState Load(string path, ILogger? logger = null)
    {
        try
        {
            if (!File.Exists(path))
                return AdaptationState.Empty;

            var dto = JsonSerializer.Deserialize<StateDto>(File.ReadAllText(path), ReadOptions);
            if (dto is null || dto.Version != AdaptationState.CurrentVersion)
            {
                logger?.LogWarning(
                    "Adaptation state from {FilePath} is absent, unreadable, or a different schema version; ignoring (learned offsets start at zero)",
                    path);
                return AdaptationState.Empty;
            }

            var learned = new Dictionary<AdaptationKnob, double>();
            foreach (var (name, value) in dto.Learned)
            {
                if (!Enum.TryParse<AdaptationKnob>(name, ignoreCase: true, out var knob))
                    continue; // a knob this build no longer knows (or never did) — ignore, keep the rest
                if (!AdaptationState.IsValueValid(value))
                    continue; // out-of-band entry dropped, survivors kept (§7.4 per-entry recovery)
                learned[knob] = value;
            }
            return new AdaptationState(dto.Version, dto.SavedAtUtc, learned);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            logger?.LogWarning(ex, "Failed to load adaptation state from {FilePath}; ignoring (learned offsets start at zero)", path);
            return AdaptationState.Empty;
        }
    }

    /// <summary>Persists the state atomically (temp file + rename). Throws only on I/O failure; the caller owns the best-effort catch.</summary>
    public static void Save(string path, AdaptationState state)
    {
        var dto = new StateDto { Version = state.Version, SavedAtUtc = state.SavedAtUtc };
        foreach (var (knob, fill) in state.Learned)
            dto.Learned[JsonNamingPolicy.CamelCase.ConvertName(knob.ToString())] = fill;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(dto, WriteOptions);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }
}
