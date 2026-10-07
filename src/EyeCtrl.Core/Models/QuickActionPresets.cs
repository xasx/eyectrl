namespace EyeCtrl.Core.Models;

/// <summary>
/// One fixed preset a quick action can be created from: a stable <see cref="Id"/> that travels
/// with the saved action, the English display label that is stored beside it as a readable
/// fallback, and the type-specific payload (a keyboard chord, a media command, or a
/// <see cref="WindowAction"/> member name).
/// </summary>
/// <param name="Id">Stable identifier, for example <c>kbd.copy</c>. Never translated, never shown.</param>
/// <param name="Label">English display label, for example "Copy".</param>
/// <param name="Payload">The action-specific value the editor stores in <see cref="QuickAction.Payload"/>.</param>
public sealed record QuickActionPreset(string Id, string Label, string Payload);

/// <summary>
/// Fixed preset tables for the Quick Actions settings editor. Data only — the editor turns a
/// chosen entry into a <see cref="QuickAction"/>. Keyboard chords must parse via the same path
/// <c>EventCoordinator.ExecuteKeyboardShortcut</c> uses; media commands must match the
/// <c>ExecuteMediaControl</c> switch (both covered by tests).
///
/// <para>
/// This class also owns the preset identity seam: <see cref="TryAssignPresetId"/> matches an
/// existing tile back to its preset once, and <see cref="DisplayLabel(QuickAction, System.Func{string,string})"/>
/// is the one route every display site takes for a tile's label. A preset tile follows the UI
/// language through the host's label function while the stored <see cref="QuickAction.Label"/>
/// stays English.
/// </para>
/// </summary>
public static class QuickActionPresets
{
    /// <summary>Common keyboard shortcuts: stable id + display label + chord string.</summary>
    public static IReadOnlyList<QuickActionPreset> KeyboardShortcuts { get; } = new[]
    {
        new QuickActionPreset("kbd.copy", "Copy", "Ctrl+C"),
        new QuickActionPreset("kbd.paste", "Paste", "Ctrl+V"),
        new QuickActionPreset("kbd.cut", "Cut", "Ctrl+X"),
        new QuickActionPreset("kbd.undo", "Undo", "Ctrl+Z"),
        new QuickActionPreset("kbd.redo", "Redo", "Ctrl+Y"),
        new QuickActionPreset("kbd.select_all", "Select All", "Ctrl+A"),
        new QuickActionPreset("kbd.save", "Save", "Ctrl+S"),
        new QuickActionPreset("kbd.find", "Find", "Ctrl+F"),
        new QuickActionPreset("kbd.new", "New", "Ctrl+N"),
        new QuickActionPreset("kbd.print", "Print", "Ctrl+P"),
        new QuickActionPreset("kbd.switch_app", "Switch App", "Alt+Tab"),
        new QuickActionPreset("kbd.show_desktop", "Show Desktop", "Win+D"),
        new QuickActionPreset("kbd.lock", "Lock", "Win+L"),
        new QuickActionPreset("kbd.file_explorer", "File Explorer", "Win+E"),
    };

    /// <summary>Media transport commands: stable id + display label + payload (matches ExecuteMediaControl).</summary>
    public static IReadOnlyList<QuickActionPreset> MediaControls { get; } = new[]
    {
        new QuickActionPreset("media.play_pause", "Play / Pause", "PlayPause"),
        new QuickActionPreset("media.stop", "Stop", "Stop"),
        new QuickActionPreset("media.next", "Next", "NextTrack"),
        new QuickActionPreset("media.previous", "Previous", "PreviousTrack"),
        new QuickActionPreset("media.volume_up", "Volume Up", "VolumeUp"),
        new QuickActionPreset("media.volume_down", "Volume Down", "VolumeDown"),
        new QuickActionPreset("media.mute", "Mute", "VolumeMute"),
    };

    /// <summary>
    /// Foreground-window management actions: stable id + display label + payload. The payload is
    /// the <see cref="WindowAction"/> enum member name, parsed by
    /// <c>EventCoordinator.ExecuteWindowAction</c> (covered by tests).
    /// </summary>
    public static IReadOnlyList<QuickActionPreset> WindowActions { get; } = new[]
    {
        new QuickActionPreset("window.minimize", "Minimize Window", "Minimize"),
        new QuickActionPreset("window.maximize_restore", "Maximize / Restore", "MaximizeRestore"),
        new QuickActionPreset("window.close", "Close Window", "Close"),
    };

    /// <summary>Every preset with the action type its table creates — the match's search space.</summary>
    private static readonly IReadOnlyList<(QuickActionType Type, QuickActionPreset Preset)> AllPresets =
        KeyboardShortcuts.Select(p => (QuickActionType.KeyboardShortcut, p))
            .Concat(MediaControls.Select(p => (QuickActionType.MediaControl, p)))
            .Concat(WindowActions.Select(p => (QuickActionType.WindowAction, p)))
            .ToList();

    private static readonly IReadOnlyDictionary<string, QuickActionPreset> PresetById =
        AllPresets.ToDictionary(p => p.Preset.Id, p => p.Preset, StringComparer.Ordinal);

    /// <summary>Looks a preset up by its stable id.</summary>
    /// <param name="presetId">The id stored on a <see cref="QuickAction.PresetId"/>.</param>
    /// <returns>The preset, or null when no preset carries the id.</returns>
    public static QuickActionPreset? ById(string presetId) =>
        PresetById.TryGetValue(presetId, out var preset) ? preset : null;

    /// <summary>
    /// The English label for a preset id, or null when the id is unknown. This is the default
    /// host label function a surface passes until its own keys arrive.
    /// </summary>
    /// <param name="presetId">The preset id to name.</param>
    /// <returns>The preset's English label, or null for an unknown id.</returns>
    public static string? EnglishLabelFor(string presetId) => ById(presetId)?.Label;

    /// <summary>
    /// The label a surface shows for a tile: the host's label for the preset the tile was
    /// created from, or the stored label for a user-named tile. An id the host has no label for
    /// falls back to the stored label, so a stale or unknown id can never blank a tile.
    /// </summary>
    /// <param name="action">The stored action whose label is shown.</param>
    /// <param name="presetLabelFor">The surface's preset-id → label function.</param>
    /// <returns>The label to display.</returns>
    public static string DisplayLabel(QuickAction action, Func<string, string?> presetLabelFor)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(presetLabelFor);
        return string.IsNullOrEmpty(action.PresetId)
            ? action.Label
            : presetLabelFor(action.PresetId) ?? action.Label;
    }

    /// <summary>
    /// The same resolution for a picker row, which has no stored action yet: the host's label
    /// for the preset, or its English table label.
    /// </summary>
    /// <param name="preset">The preset being offered.</param>
    /// <param name="presetLabelFor">The surface's preset-id → label function.</param>
    /// <returns>The label to display.</returns>
    public static string DisplayLabel(QuickActionPreset preset, Func<string, string?> presetLabelFor)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(presetLabelFor);
        return presetLabelFor(preset.Id) ?? preset.Label;
    }

    /// <summary>
    /// The one-time match: gives an action that has no preset id the id of the preset whose
    /// type, payload AND stored English label all match. An action the user named — or one whose
    /// label has drifted from its preset — is deliberately left alone, and an action that
    /// already has an id is never re-matched.
    /// </summary>
    /// <param name="action">The action to match, mutated in place when a preset matches.</param>
    /// <returns>True when an id was assigned; the caller saves only when something changed.</returns>
    public static bool TryAssignPresetId(QuickAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!string.IsNullOrEmpty(action.PresetId)) return false;

        foreach (var (type, preset) in AllPresets)
        {
            if (action.Type == type && action.Payload == preset.Payload && action.Label == preset.Label)
            {
                action.PresetId = preset.Id;
                return true;
            }
        }
        return false;
    }
}
