namespace EyeCtrl.Core.Models;

/// <summary>
/// Fixed preset tables for the Quick Actions settings editor. Data only — the editor turns a
/// chosen entry into a <see cref="QuickAction"/>. Keyboard chords must parse via the same path
/// <c>EventCoordinator.ExecuteKeyboardShortcut</c> uses; media commands must match the
/// <c>ExecuteMediaControl</c> switch (both covered by tests).
/// </summary>
public static class QuickActionPresets
{
    /// <summary>Common keyboard shortcuts: display label + chord string.</summary>
    public static IReadOnlyList<(string Label, string Chord)> KeyboardShortcuts { get; } = new[]
    {
        ("Copy", "Ctrl+C"),
        ("Paste", "Ctrl+V"),
        ("Cut", "Ctrl+X"),
        ("Undo", "Ctrl+Z"),
        ("Redo", "Ctrl+Y"),
        ("Select All", "Ctrl+A"),
        ("Save", "Ctrl+S"),
        ("Find", "Ctrl+F"),
        ("New", "Ctrl+N"),
        ("Print", "Ctrl+P"),
        ("Switch App", "Alt+Tab"),
        ("Show Desktop", "Win+D"),
        ("Lock", "Win+L"),
        ("File Explorer", "Win+E"),
    };

    /// <summary>Media transport commands: display label + payload command (matches ExecuteMediaControl).</summary>
    public static IReadOnlyList<(string Label, string Command)> MediaControls { get; } = new[]
    {
        ("Play / Pause", "PlayPause"),
        ("Stop", "Stop"),
        ("Next", "NextTrack"),
        ("Previous", "PreviousTrack"),
        ("Volume Up", "VolumeUp"),
        ("Volume Down", "VolumeDown"),
        ("Mute", "VolumeMute"),
    };

    /// <summary>
    /// Foreground-window management actions: display label + payload. The payload is the
    /// <see cref="WindowAction"/> enum member name, parsed by
    /// <c>EventCoordinator.ExecuteWindowAction</c> (covered by tests).
    /// </summary>
    public static IReadOnlyList<(string Label, string Payload)> WindowActions { get; } = new[]
    {
        ("Minimize Window", "Minimize"),
        ("Maximize / Restore", "MaximizeRestore"),
        ("Close Window", "Close"),
    };
}
