namespace EyeCtrl.Controls.Theme;

/// <summary>
/// A glyph or label's font size as a fraction of the container that bounds it.
///
/// Roughly a quarter of the app's font-size sites are not steps on the type ramp: they are
/// icons and short labels sized to fit a box, and several of those boxes resize at runtime
/// (<c>KeyboardWindow</c>'s keys scale with a setting). Snapping those onto a text ramp
/// would resize icons relative to their containers. <c>ToolbarWindow</c> already modelled
/// the right approach with a local <c>ButtonSymbolSizeRatio = 0.4</c>; this generalises it.
///
/// **Named for containers, not glyphs**, because it also covers keyboard letters and
/// prediction words, for which "glyph" would be wrong.
///
/// **Three members are named for a role, three for a magnitude.** `Badge`, `Solo` and
/// `Dominant` name situations, not sizes — check them first. `Large`/`Medium`/`Small` name
/// relative weight inside an ordinary container. That mixed axis is deliberate: `Badge` is the
/// *largest* ratio but serves the app's *smallest* container (26px), so a magnitude name would
/// invert the reader's mental model. Size intuition alone will pick wrong.
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
    /// A drawn icon that is the WHOLE content of its disc on one of the two gaze-target surfaces
    /// (the modes menu and the icon-only edge buttons), with no label beside it to share the face.
    /// Larger than <see cref="Dominant"/> because those discs hold nothing else — and the user's
    /// toolbar can be as small as 40px, where an icon at Dominant reads as a stamp in a wide ring.
    ///
    /// <para>
    /// 0.58 is a measured ceiling, not a taste: it is the largest ratio at which every icon in the
    /// redesigned set keeps the required +2 DIP clearance to the progress arc's inner edge at the
    /// smallest supported size (40px). The tightest, Double click's two mice at +2.34 DIP, sets it
    /// — see the Phase-1 clearance table and the icon-fit test that sweeps 40–200.
    /// </para>
    ///
    /// <para>
    /// Scoped by construction: <see cref="Dominant"/> still sizes the toolbar, the ring and the
    /// quick-actions grid, which must not move. A caller takes this ratio only by asking
    /// <see cref="EyeCtrl.Controls.DwellArcButton.Create"/>'s <c>IconSize.Solo</c>, which refuses a
    /// label — the ratio names a label-less face.
    /// </para>
    /// </summary>
    public const double Solo = 0.58;

    /// <summary>
    /// A 40–50px chrome affordance whose entire content is one glyph, with no label beside
    /// it. Observed: <c>QuickActionsGridWindow</c>'s 44px close button at 20/44 = 0.455.
    /// </summary>
    public const double Dominant = 0.45;

    /// <summary>
    /// A prominent icon filling most of its cell. Observed: <c>ToolbarWindow</c>'s existing
    /// 0.4 verbatim, the emoji cell at 28/70 = 0.400, and <c>SettingsWindow</c>'s pagination
    /// digit at 30/80 = 0.375 — this bucket's largest delta (+2.0px).
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
    /// button 14/60, arc button 22/100 (at the 50px-radius callers) — all 0.220 to 0.233.
    ///
    /// Not covered: <c>DwellArcButton</c>'s 40px-diameter callers (the edge bar's drag/back
    /// buttons, which default to a 20px radius) observe 22/40 = 0.55 — no bucket fits. Those
    /// sites are resolved, but NOT by adding a bucket for them: they take
    /// <see cref="Badge"/> instead, selected by <c>DwellArcButton.BadgeDiameterThreshold</c>,
    /// which routes any diameter under 64px to <see cref="Badge"/> and everything at or above it
    /// to this ratio. See that const's doc comment for why a bucket would have been wrong —
    /// 40 × 0.23 = 9.2px is below the accessibility floor on the affordance that cancels a drag.
    /// </summary>
    public const double Small = 0.23;
}
