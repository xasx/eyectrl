namespace EyeCtrl.Core.Models;

/// <summary>The kind of action a quick action performs when triggered.</summary>
public enum QuickActionType
{
    /// <summary>A keyboard shortcut (e.g. Ctrl+C), sent as a key combination.</summary>
    KeyboardShortcut,
    /// <summary>Pre-written text pasted/typed into the focused field.</summary>
    CannedText,
    /// <summary>Launch an external executable.</summary>
    LaunchExe,
    /// <summary>Launch a UWP/Store Windows application.</summary>
    LaunchWindowsApp,
    /// <summary>Open a URL in the default browser.</summary>
    LaunchUrl,
    /// <summary>Play/pause, volume, or other media transport control.</summary>
    MediaControl
}

/// <summary>A user-configurable action shown on the Quick Actions toolbar.</summary>
public class QuickAction
{
    /// <summary>Unique identifier for this action, generated at creation.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString();
    /// <summary>The kind of action to perform when this quick action is triggered.</summary>
    public QuickActionType Type { get; set; }
    /// <summary>The display label shown on the toolbar button.</summary>
    public string Label { get; set; } = string.Empty;
    /// <summary>Action-specific data (key sequence, text, path, URL, etc.).</summary>
    public string Payload { get; set; } = string.Empty;
    /// <summary>Whether this action is active and selectable on the toolbar.</summary>
    public bool Enabled { get; set; } = true;
}
