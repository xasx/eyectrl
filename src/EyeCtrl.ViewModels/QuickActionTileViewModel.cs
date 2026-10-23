namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

/// <summary>
/// One tile in the Quick Actions grid, wrapping a single <see cref="QuickAction"/> for display.
/// Exposes a computed <see cref="Icon"/> glyph (chosen from the action type/payload) and a
/// <see cref="DwellProgress"/> observable for the dwell-progress animation.
/// </summary>
public partial class QuickActionTileViewModel : ObservableObject
{
    /// <summary>Dwell progress 0.0–1.0, driven by the grid VM during gaze dwell.</summary>
    [ObservableProperty] private double _dwellProgress;
    /// <summary>
    /// True while this item's dwell is HELD: the gaze stepped off mid-dwell and <see cref="DwellProgress"/>
    /// is kept for the pause grace. The view dims the progress indicator; it never changes the value.
    /// Display-only — nothing reads it to decide whether a dwell holds.
    /// </summary>
    [ObservableProperty] private bool _isDwellPaused;

    /// <summary>The quick action this tile represents.</summary>
    public QuickAction Action { get; }

    /// <summary>
    /// The label shown on the tile: the host's label for the preset this action was created
    /// from, or the action's stored label for a user-named tile. Resolved once at construction
    /// through <see cref="QuickActionPresets.DisplayLabel(QuickAction, System.Func{string,string})"/>,
    /// so a host can key preset labels without touching stored data.
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// Display glyph for the tile, computed from the action type (and payload for media controls).
    /// Uses Unicode symbols; WPF default font may not render some emoji, hence the ASCII fallbacks.
    /// </summary>
    public string Icon { get; }

    /// <param name="action">The stored action this tile shows.</param>
    /// <param name="presetLabelFor">
    /// The host's preset-id → label function; null means the English preset labels
    /// (<see cref="QuickActionPresets.EnglishLabelFor"/>).
    /// </param>
    public QuickActionTileViewModel(QuickAction action, Func<string, string?>? presetLabelFor = null)
    {
        Action = action;
        Label = QuickActionPresets.DisplayLabel(action, presetLabelFor ?? QuickActionPresets.EnglishLabelFor);
        Icon = action.Type switch
        {
            QuickActionType.KeyboardShortcut => "\u2328",   // ⌨
            QuickActionType.CannedText => "T",              // fallback (no emoji in WPF default font)
            QuickActionType.LaunchExe => "\u229E",          // ⊞
            QuickActionType.LaunchWindowsApp => "\u21C4", // 🪟 (BMP; the old window-emoji glyph rendered as tofu)
            QuickActionType.LaunchUrl => "@",                // fallback
            QuickActionType.MediaControl => action.Payload switch
            {
                "PlayPause" => "\u23EF",     // ⏯
                "Stop" => "\u23F9",          // ⏹
                "NextTrack" => "\u23ED",     // ⏭
                "PreviousTrack" => "\u23EE", // ⏮
                "VolumeUp" => "\uD83D\uDD0A",   // 🔊
                "VolumeDown" => "\uD83D\uDD09", // 🔉
                "VolumeMute" => "\uD83D\uDD07", // 🔇
                _ => "\u266B"                // ♫ fallback
            },
            QuickActionType.WindowAction => action.Payload switch
            {
                "Minimize" => "\u2581",        // ▁
                "MaximizeRestore" => "\u25A1", // □
                "Close" => "\u2715",           // ✕
                _ => "\u25A3"                  // ▣ fallback
            },
            _ => "\u26A1"                      // ⚡ fallback
        };
    }
}
