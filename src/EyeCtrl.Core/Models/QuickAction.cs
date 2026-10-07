namespace EyeCtrl.Core.Models;

using System.Text.Json.Serialization;

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
    MediaControl,
    /// <summary>Minimize, maximize/restore, or close the foreground window.</summary>
    WindowAction
}

/// <summary>A user-configurable action shown on the Quick Actions toolbar.</summary>
public class QuickAction
{
    /// <summary>Unique identifier for this action, generated at creation.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString();
    /// <summary>The kind of action to perform when this quick action is triggered.</summary>
    public QuickActionType Type { get; set; }
    /// <summary>
    /// The stable id of the preset this action was created from (for example <c>kbd.copy</c>),
    /// or null for a user-named action. The displayed label resolves through
    /// <see cref="QuickActionPresets.DisplayLabel(QuickAction, System.Func{string,string})"/>;
    /// the stored <see cref="Label"/> stays as the readable English fallback.
    ///
    /// <para>
    /// A null id is omitted from <c>actions.json</c>, so an existing install's file does not
    /// grow a <c>"PresetId": null</c> line. The condition is scoped to this property alone —
    /// unlike a serializer-wide null omission, it leaves every other legacy field, including an
    /// explicit null <see cref="Payload"/>, exactly as it was found.
    /// </para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PresetId { get; set; }
    /// <summary>The display label shown on the toolbar button.</summary>
    public string Label { get; set; } = string.Empty;
    /// <summary>Action-specific data (key sequence, text, path, URL, etc.).</summary>
    public string Payload { get; set; } = string.Empty;
    /// <summary>Whether this action is active and selectable on the toolbar.</summary>
    public bool Enabled { get; set; } = true;
}
