namespace EyeCtrl.Controls.Theme;

/// <summary>
/// A glyph or label's font size as a fraction of the container that bounds it.
///
/// Roughly a sixth of the app's font sizes are not steps on the type ramp: they are icons
/// and short labels sized to fit a box, and several of those boxes resize at runtime
/// (<c>KeyboardWindow</c>'s keys scale with a setting). Snapping those onto a text ramp
/// would resize icons relative to their containers. <c>ToolbarWindow</c> already modelled
/// the right approach with a local <c>ButtonSymbolSizeRatio = 0.4</c>; this generalises it.
///
/// **Named for containers, not glyphs**, because it also covers keyboard letters and
/// prediction words, for which "glyph" would be wrong.
///
/// Deliberately NOT theme tokens: a ratio describes a geometric relationship, and no palette
/// changes it. If one ever needs to vary per theme it can move to <see cref="ITheme"/> the
/// same way <see cref="ITheme.BorderThicknessStandard"/> did.
///
/// Every value below was observed at a site that already used it — see the design spec
/// §8.5 table. They are ratios, not free parameters.
/// </summary>
public static class ContainerRatio
{
    /// <summary>
    /// Glyph in a small circular badge, under about 32px, where a smaller ratio would be
    /// illegible. Observed: <c>DragTrailOverlay</c>'s grab/release icons at 16/26 = 0.615.
    /// </summary>
    public const double Badge = 0.60;

    /// <summary>
    /// The glyph *is* the control. Observed: <c>QuickActionsGridWindow</c>'s 44px close
    /// button at 20/44 = 0.455.
    /// </summary>
    public const double Dominant = 0.45;

    /// <summary>
    /// A prominent icon filling most of its cell. Observed: <c>ToolbarWindow</c>'s existing
    /// 0.4 verbatim, and the emoji cell at 28/70 = 0.400.
    /// </summary>
    public const double Large = 0.40;

    /// <summary>
    /// A mid-weight icon. Observed: emoji category 22/70 = 0.314, skin-tone 36/105 = 0.343,
    /// the banner's dwell-ring glyphs 22/64 = 0.344, the settings tab icon 30/100 = 0.300.
    /// </summary>
    public const double Medium = 0.32;

    /// <summary>
    /// An icon or short label above or beside other content. The most-observed ratio, at six
    /// sites: tile 28/120, keyboard key 16/70, prediction 16/70, layout-switch 16/70, note
    /// button 14/60, arc button 22/100 — all 0.220 to 0.233.
    /// </summary>
    public const double Small = 0.23;
}
