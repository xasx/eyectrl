namespace EyeCtrl.ViewModels;

using EyeCtrl.Core.Models;

/// <summary>
/// Single source of truth for which toolbar icon represents each <see cref="SelectionMode"/>.
/// Shared by <see cref="ToolbarViewModel"/> (mode → key) and the toolbar window (key → mode) so
/// the forward and reverse mappings cannot drift apart.
///
/// Values are <c>EyeCtrl.Controls.Icons.ToolbarIconKey</c> member NAMES carried as strings,
/// because this project must not reference the WPF Controls assembly. Pinned against the real
/// enum by <c>ToolbarIconKeyBridgeTests</c> in EyeCtrl.App.Tests, which sees both sides.
/// </summary>
public static class SelectionModeIcons
{
    /// <summary>None (raw pixel targeting) — a slashed ring.</summary>
    public const string None = "SelectionNone";

    /// <summary>Manual Selection (zoom-and-dwell) — a crosshair in a ring.</summary>
    public const string ManualSelection = "SelectionManual";

    /// <summary>Auto Selection (snap-to-control) — a ring around a control.</summary>
    public const string AutoSelection = "SelectionAuto";

    /// <summary>Returns the icon key for the given selection mode.</summary>
    /// <param name="mode">The mode.</param>
    /// <returns>The icon key name.</returns>
    public static string ForMode(SelectionMode mode) => mode switch
    {
        SelectionMode.ManualSelection => ManualSelection,
        SelectionMode.AutoSelection => AutoSelection,
        _ => None
    };

    /// <summary>Maps an icon key back to its mode (defaults to <see cref="SelectionMode.None"/>).</summary>
    /// <param name="iconKey">The icon key name.</param>
    /// <returns>The mode it represents.</returns>
    public static SelectionMode ToMode(string iconKey) => iconKey switch
    {
        ManualSelection => SelectionMode.ManualSelection,
        AutoSelection => SelectionMode.AutoSelection,
        _ => SelectionMode.None
    };
}
