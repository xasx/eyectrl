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
    /// <summary>
    /// Icon key for <see cref="SelectionMode.None"/> (raw pixel targeting). This project cannot
    /// see <c>ToolbarIconKey</c>, so this describes what the mode means, not what the icon looks
    /// like — see <c>ToolbarIconKey.SelectionNone</c> for the appearance.
    /// </summary>
    public const string None = "SelectionNone";

    /// <summary>
    /// Icon key for <see cref="SelectionMode.ManualSelection"/> (zoom-and-dwell). This project
    /// cannot see <c>ToolbarIconKey</c>, so this describes what the mode means, not what the icon
    /// looks like — see <c>ToolbarIconKey.SelectionManual</c> for the appearance.
    /// </summary>
    public const string ManualSelection = "SelectionManual";

    /// <summary>
    /// Icon key for <see cref="SelectionMode.AutoSelection"/> (snap-to-control). This project
    /// cannot see <c>ToolbarIconKey</c>, so this describes what the mode means, not what the icon
    /// looks like — see <c>ToolbarIconKey.SelectionAuto</c> for the appearance.
    /// </summary>
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
