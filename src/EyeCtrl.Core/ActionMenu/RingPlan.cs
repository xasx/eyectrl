namespace EyeCtrl.Core.ActionMenu;

using System.Collections.Generic;
using System.Linq;
using EyeCtrl.Core.Models;

/// <summary>
/// Pure, WPF-free description of the radial action menu: the primary/secondary partition, the two
/// layers' ordered slots and angles, the ring's radius and window geometry, and the scalar the
/// settings preview scales itself with.
///
/// <para>
/// One plan serves both consumers — the runtime ring and the settings preview — so the two can
/// never disagree about what sits where. The partition keeps the VISIBLE list's order filtered by
/// primary membership, deliberately not <c>PrimaryActions</c> order: that is how the runtime
/// draws the ring, and a preview ordered by the primary list would teach angles the runtime never
/// shows. (The old settings-object partition property that used primary-list order was deleted
/// rather than reused; this plan is its replacement.)
/// </para>
///
/// <para>
/// Doubles only: no <c>System.Windows.Point</c>, no DPI conversion, no window. A host turns slot
/// angles into positions (runtime: physical pixels via its DPI helper; preview: DIPs).
/// </para>
/// </summary>
public sealed class RingPlan
{
    /// <summary>
    /// Extra margin (DIP) around the button ring when sizing the square window that holds it; the
    /// clearance that keeps the outermost stroke from clipping at the window edge.
    /// </summary>
    public const double WindowSizeMargin = 20.0;

    /// <summary>
    /// Legibility floor: the smallest drawn button RADIUS (DIP) a shrunk preview may reach before
    /// it is omitted entirely. ~36 DIP keeps a scaled button's icon readable; below it the
    /// preview would teach a ring too small to have faith in — absent beats approximate. This is
    /// the legibility bound (<see cref="MinimumLegibleScale"/>); the fit bound is
    /// <see cref="ScaleCap"/>.
    /// </summary>
    public const double LegibilityFloorButtonRadiusDip = 36.0;

    /// <summary>
    /// The smallest fit scalar still above <see cref="LegibilityFloorButtonRadiusDip"/> — the
    /// floor expressed as a fraction of the true button radius. A preview whose computed scale
    /// falls below this must be omitted, never drawn smaller.
    /// </summary>
    public const double MinimumLegibleScale = LegibilityFloorButtonRadiusDip / LayoutConstants.ActionMenuButtonRadius;

    /// <summary>
    /// The largest scale a preview is ever drawn at (the fit cap; 1.0 = true size). Kept separate
    /// from the hard never-magnify bound so a product decision can cap large monitors without
    /// touching the fit math or the legibility bound (<see cref="MinimumLegibleScale"/>).
    /// </summary>
    public const double ScaleCap = 1.0;

    private RingPlan(
        IReadOnlyList<InteractionAction> visibleActions,
        IReadOnlyList<InteractionAction> primaryActions,
        double circleSize)
    {
        CircleSize = circleSize;

        // Visible order on both sides. PrimaryActions order is deliberately NOT used: a slot's
        // position follows the order the menu was opened with, and both consumers read these
        // lists, so ordering by the primary list would move every angle.
        PrimaryActions = visibleActions.Where(primaryActions.Contains).ToArray();
        SecondaryActions = visibleActions.Where(a => !primaryActions.Contains(a)).ToArray();

        EffectiveRingRadius = EffectiveRingRadiusFor(circleSize);
        WindowSize = WindowSizeFor(EffectiveRingRadius);
    }

    /// <summary>
    /// Builds a plan for one menu composition. Null lists are tolerated and treated as empty,
    /// mirroring the runtime's fallback for a settings file whose lists deserialized as null.
    /// </summary>
    /// <param name="visibleActions">The visible actions, in the order the menu was opened with.</param>
    /// <param name="primaryActions">The actions promoted to the ring's first layer.</param>
    /// <param name="circleSize">The cursor circle diameter in DIP (the Cursor tab's CircleSize).</param>
    public static RingPlan Create(
        IReadOnlyList<InteractionAction>? visibleActions,
        IReadOnlyList<InteractionAction>? primaryActions,
        double circleSize)
        => new(
            visibleActions ?? Array.Empty<InteractionAction>(),
            primaryActions ?? Array.Empty<InteractionAction>(),
            circleSize);

    /// <summary>The cursor circle diameter in DIP the ring is built around.</summary>
    public double CircleSize { get; }

    /// <summary>The visible actions on the first layer, in visible-list order.</summary>
    public IReadOnlyList<InteractionAction> PrimaryActions { get; }

    /// <summary>The visible actions behind More, in visible-list order.</summary>
    public IReadOnlyList<InteractionAction> SecondaryActions { get; }

    /// <summary>
    /// The ring radius in DIP: the cursor circle's edge, plus the button radius, plus the outer
    /// gap — the single definition the runtime ring, the screen-edge clamp and the preview read.
    /// </summary>
    public double EffectiveRingRadius { get; }

    /// <summary>Side length (DIP) of the square the true-size ring fits, window margin included.</summary>
    public double WindowSize { get; }

    /// <summary>Ring radius in DIP for a cursor circle diameter, without building a plan.</summary>
    public static double EffectiveRingRadiusFor(double circleSize)
        => circleSize / 2 + LayoutConstants.ActionMenuButtonRadius + LayoutConstants.ActionMenuButtonOuterGap;

    /// <summary>Window side length (DIP) for a ring radius, without building a plan.</summary>
    public static double WindowSizeFor(double ringRadiusDip)
        => (ringRadiusDip + LayoutConstants.ActionMenuButtonRadius) * 2 + WindowSizeMargin;

    /// <summary>
    /// The uniform scale a preview draws the true-size painting at:
    /// <c>min(1, availableWidth / WindowSize, availableHeight / WindowSize, ScaleCap)</c> — the
    /// largest value ≤ <see cref="ScaleCap"/> that fits the square painting in the space the host
    /// can offer. Callers pass their available rectangle ONCE and share the result: the width
    /// decision and the height gate must never restate this arithmetic.
    /// </summary>
    /// <param name="availableWidthDip">Width the host can give the preview, in DIP.</param>
    /// <param name="availableHeightDip">Height the host can give the preview, in DIP.</param>
    public double ScaleFor(double availableWidthDip, double availableHeightDip)
    {
        var fit = Math.Min(availableWidthDip / WindowSize, availableHeightDip / WindowSize);
        return Math.Min(1.0, Math.Min(fit, ScaleCap));
    }

    /// <summary>
    /// Whether a fit scalar keeps the preview above the legibility floor. False means omit the
    /// preview entirely — a ring scaled below this teaches a shape the eye cannot use.
    /// </summary>
    public static bool IsLegibleAt(double scale) => scale >= MinimumLegibleScale;

    /// <summary>
    /// Builds one layer's ordered slots, with the layer's angular step and each slot's angle.
    ///
    /// <para>
    /// Layer one: the primary actions, then a More target when the second layer holds anything
    /// (a More that opens an empty layer is a dead end), then — only when
    /// <paramref name="showBack"/> — the Back that exits the menu. Layer two: the remaining
    /// actions, then the Back to layer one, always. This mirrors the runtime's rule that one nav
    /// slot cannot mean two things, so the zoom Back lives on layer one alone.
    /// </para>
    /// </summary>
    /// <param name="layer">Which layer to build.</param>
    /// <param name="showBack">
    /// Whether the zoom/manual-selection Back exits the menu on layer one. Ignored on layer two,
    /// whose Back is always the return to layer one.
    /// </param>
    public RingLayerPlan BuildLayer(RingLayer layer, bool showBack)
    {
        var items = new List<(string Id, InteractionAction? Action, ActionIcon Icon, string Label, ActionMenuItemKind Kind)>();

        if (layer == RingLayer.Secondary)
        {
            foreach (var action in SecondaryActions)
                items.Add((action.ToString(), action, ActionIconMap.For(action), RingLabelMap.For(action), ActionMenuItemKind.Action));

            items.Add(("BackToPrimary", null, ActionIcon.Back, "Back", ActionMenuItemKind.BackToPrimary));
        }
        else
        {
            foreach (var action in PrimaryActions)
                items.Add((action.ToString(), action, ActionIconMap.For(action), RingLabelMap.For(action), ActionMenuItemKind.Action));

            // No More target when it would open an empty layer.
            if (SecondaryActions.Count > 0)
                items.Add(("More", null, ActionIcon.More, "More", ActionMenuItemKind.OpenMore));

            // The zoom Back only exists on layer one. From layer two it takes two dwells — back
            // to primary, then Back — because one nav slot cannot mean two things.
            if (showBack)
                items.Add(("Back", null, ActionIcon.Back, "Back", ActionMenuItemKind.ExitMenu));
        }

        var angleStepDegrees = items.Count == 0 ? 0.0 : MathConstants.FullCircleDegrees / items.Count;

        var slots = new RingSlot[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var (id, action, icon, label, kind) = items[i];
            slots[i] = new RingSlot(
                id, action, icon, label, kind, i,
                angleStepDegrees * i - MathConstants.RightAngleDegrees);
        }

        return new RingLayerPlan(layer, slots, angleStepDegrees);
    }
}
