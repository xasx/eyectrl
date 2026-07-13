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

    /// <summary>The quick action this tile represents.</summary>
    public QuickAction Action { get; }

    /// <summary>
    /// Display glyph for the tile, computed from the action type (and payload for media controls).
    /// Uses Unicode symbols; WPF default font may not render some emoji, hence the ASCII fallbacks.
    /// </summary>
    public string Icon { get; }

    public QuickActionTileViewModel(QuickAction action)
    {
        Action = action;
        Icon = action.Type switch
        {
            QuickActionType.KeyboardShortcut => "\u2328",   // ⌨
            QuickActionType.CannedText => "T",              // fallback (no emoji in WPF default font)
            QuickActionType.LaunchExe => "\u229E",          // ⊞
            QuickActionType.LaunchWindowsApp => "\uD83E\uDE9F", // 🪟 (surrogate pair)
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
            _ => "\u26A1"                      // ⚡ fallback
        };
    }
}
