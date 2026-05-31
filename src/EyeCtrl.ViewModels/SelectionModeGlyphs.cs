namespace EyeCtrl.ViewModels;

using EyeCtrl.Core.Models;

/// <summary>
/// Single source of truth for the toolbar glyphs that represent each <see cref="SelectionMode"/>.
/// Shared by <see cref="ToolbarViewModel"/> (mode → glyph) and the toolbar window (glyph → mode)
/// so the forward and reverse mappings cannot drift apart.
/// </summary>
public static class SelectionModeGlyphs
{
    /// <summary>None (raw pixel targeting) — ○.</summary>
    public const string None = "○";

    /// <summary>Manual Selection (zoom-and-dwell) — ⊕.</summary>
    public const string ManualSelection = "⊕";

    /// <summary>Auto Selection (snap-to-control) — ◎.</summary>
    public const string AutoSelection = "◎";

    /// <summary>Returns the glyph for the given selection mode.</summary>
    public static string ForMode(SelectionMode mode) => mode switch
    {
        SelectionMode.ManualSelection => ManualSelection,
        SelectionMode.AutoSelection => AutoSelection,
        _ => None
    };

    /// <summary>Maps a glyph back to its selection mode (defaults to <see cref="SelectionMode.None"/>).</summary>
    public static SelectionMode ToMode(string glyph) => glyph switch
    {
        ManualSelection => SelectionMode.ManualSelection,
        AutoSelection => SelectionMode.AutoSelection,
        _ => SelectionMode.None
    };
}
