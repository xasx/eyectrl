namespace EyeCtrl.App.Services.Coordinators;

using EyeCtrl.App.Overlays;
using EyeCtrl.Core.ActionMenu;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

/// <summary>
/// Ticket 8's chrome-zone composition: the rects the activation offer's engine must never place a
/// button over (dwell has no z-order, so an offer drawn over EyeCtrl's own controls would compete
/// with them). Pure functions over plain data — the production collector reads the live windows and
/// ViewModels, and every arithmetic step is separately testable.
///
/// <para>
/// A zone is deliberately the whole interactive surface, over-covering rather than under: the button
/// simply is not offered near chrome, and the ring still pins there (decision 8). The toolbar is the
/// only surface with a physical-bounds seam (<see cref="IToolbarOverlay.GetPhysicalBounds"/>); the
/// keyboard window exposes its laid-out bounds, and the two menus are composed from the ViewModel
/// data the windows themselves reported — no extra window access.
/// </para>
///
/// <para>
/// The modal surfaces that suspend offers entirely (Settings, the task switcher, the quick-actions
/// grid, the eye-care banner) are deliberately NOT zones: the user's rule is "no offer at all while
/// one is open", which the engine's suspension flag carries more honestly than a screen-sized rect.
/// </para>
/// </summary>
internal static class ActivationChromeZones
{
    /// <summary>
    /// The zones for whichever surfaces are currently up, in physical pixels. Called from the
    /// coordinator after every trigger the plan lists (keyboard show/hide/layout, toolbar
    /// slide/auto-hide, prediction bar, action and modes menus) and deferred one layout pass, so a
    /// <c>SizeToContent</c> window's bounds are its laid-out ones.
    /// </summary>
    public static PhysicalRect[] CollectFromProduction(EventCoordinationContext context)
    {
        var toolbar = context.OverlayManager.Toolbar;
        PhysicalRect toolbarBounds = toolbar.IsOnScreen ? toolbar.GetPhysicalBounds() : default;

        // The whole keyboard window: key grid, prediction bar and category bar together. The
        // prediction bar is inside this rect, and it is why a prediction relayout (SizeToContent)
        // re-publishes.
        PhysicalRect? keyboardBounds =
            context.KeyboardVM.IsVisible
            && context.OverlayManager.Keyboard.TryGetChromeBounds(out var bounds)
                ? bounds
                : null;

        PhysicalRect? actionMenuBounds = context.ActionMenuVM.IsVisible
            ? ActionMenuBounds(context.ActionMenuVM.Center, context.ActionMenuVM.EffectiveRingRadius, DpiHelper.ScaleX)
            : null;

        PhysicalRect? modesMenuBounds = context.ModesMenuVM.IsVisible
            ? ModesMenuBounds(context.ModesMenuVM.RowScreenPositions)
            : null;

        return Compose(
            toolbar.IsOnScreen, toolbarBounds,
            context.KeyboardVM.IsVisible, keyboardBounds,
            actionMenuBounds, modesMenuBounds);
    }

    /// <summary>
    /// The composition itself, split out so the "which surface contributes" rules are unit-testable
    /// without windows: a zone is included only when its surface is up and its bounds were sourced.
    /// </summary>
    public static PhysicalRect[] Compose(
        bool toolbarOnScreen, PhysicalRect toolbarBounds,
        bool keyboardVisible, PhysicalRect? keyboardBounds,
        PhysicalRect? actionMenuBounds,
        PhysicalRect? modesMenuBounds)
    {
        var zones = new List<PhysicalRect>(4);
        if (toolbarOnScreen)
            zones.Add(toolbarBounds);
        if (keyboardVisible && keyboardBounds is { } keyboard)
            zones.Add(keyboard);
        if (actionMenuBounds is { } actionMenu)
            zones.Add(actionMenu);
        if (modesMenuBounds is { } modesMenu)
            zones.Add(modesMenu);
        return zones.ToArray();
    }

    /// <summary>
    /// The action menu window's bounds: the window is a square of
    /// <see cref="RingPlan.WindowSizeFor"/> DIP centred on the clamped menu centre, so the
    /// physical rect follows the live scale.
    /// </summary>
    public static PhysicalRect ActionMenuBounds(PhysicalPoint center, double ringRadiusDip, double scale)
    {
        double sizePx = RingPlan.WindowSizeFor(ringRadiusDip) * scale;
        return new PhysicalRect(center.X - (sizePx / 2), center.Y - (sizePx / 2), sizePx, sizePx);
    }

    /// <summary>
    /// The union of the modes-menu rows the window last reported — the panel's interactive strip,
    /// not its outer frame. Null when no placement has been reported yet (nothing to suppress).
    /// </summary>
    public static PhysicalRect? ModesMenuBounds(
        IReadOnlyList<(string Id, PhysicalPoint Center, double Radius)> rows)
    {
        if (rows.Count == 0)
            return null;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (_, center, radius) in rows)
        {
            minX = Math.Min(minX, center.X - radius);
            minY = Math.Min(minY, center.Y - radius);
            maxX = Math.Max(maxX, center.X + radius);
            maxY = Math.Max(maxY, center.Y + radius);
        }
        return new PhysicalRect(minX, minY, maxX - minX, maxY - minY);
    }
}
