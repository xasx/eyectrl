namespace EyeCtrl.App.Tests.Services.Coordinators;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.ActionMenu;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// Ticket 8's chrome-zone composition, pure: which surface contributes a zone, the action menu's
/// window square, the modes menu's row union, and the production collector's toolbar read. Review A
/// A6/A7: the collector's keyboard and toolbar lines were unpinned — a fake overlay manager proves the
/// toolbar path here, and the keyboard's visibility/bounds rule is pinned through
/// <see cref="ActivationChromeZones.Compose"/> (the concrete <c>KeyboardWindow</c> cannot be faked on
/// the manager's Keyboard property).
/// </summary>
public class ActivationChromeZonesTests
{
    [Fact]
    public void Compose_IncludesTheToolbarOnlyWhenOnScreen()
    {
        // Review A A7's rule. Mutation: ignore the flag → an auto-hidden toolbar still blocks offers.
        var bounds = new PhysicalRect(695, 0, 2450, 288);
        Check.That(ActivationChromeZones.Compose(true, bounds, false, null, null, null))
            .ContainsExactly(bounds);
        Check.That(ActivationChromeZones.Compose(false, bounds, false, null, null, null)).IsEmpty();
    }

    [Fact]
    public void Compose_IncludesTheKeyboardOnlyWhenVisibleAndSourced()
    {
        // Review A A6's rule: visible but unsourced (a not-yet-measured window) contributes nothing.
        // Mutation: include it on visibility alone → a zero rect becomes a zone at the origin.
        var bounds = new PhysicalRect(600, 1800, 2600, 600);
        Check.That(ActivationChromeZones.Compose(false, default, true, bounds, null, null))
            .ContainsExactly(bounds);
        Check.That(ActivationChromeZones.Compose(false, default, true, null, null, null)).IsEmpty();
        Check.That(ActivationChromeZones.Compose(false, default, false, bounds, null, null)).IsEmpty();
    }

    [Fact]
    public void Compose_IncludesTheMenuZonesWhenTheyAreUp()
    {
        var actionMenu = new PhysicalRect(340, 140, 1320, 1320);
        var modesMenu = new PhysicalRect(1730, 230, 340, 140);

        var zones = ActivationChromeZones.Compose(false, default, false, null, actionMenu, modesMenu);

        Check.That(zones).ContainsExactly(actionMenu, modesMenu);
    }

    [Fact]
    public void ActionMenuBounds_IsTheWindowSquareCentredOnTheMenuCentre()
    {
        // The window is a square of RingPlan.WindowSizeFor DIP centred on the clamped centre;
        // at ScaleX 2.0 its physical extent doubles with it. Mutation: divide by the scale instead of
        // multiplying (physical = DIP / scale) → the rect halves and this goes red.
        var center = new PhysicalPoint(1000, 800);
        double ringDip = 250;
        double expectedSizePx = RingPlan.WindowSizeFor(ringDip) * 2.0;

        var rect = ActivationChromeZones.ActionMenuBounds(center, ringDip, scale: 2.0);

        Check.That(rect.Width).IsCloseTo(expectedSizePx, 1e-9);
        Check.That(rect.Height).IsCloseTo(expectedSizePx, 1e-9);
        Check.That(rect.X).IsCloseTo(center.X - (expectedSizePx / 2), 1e-9);
        Check.That(rect.Y).IsCloseTo(center.Y - (expectedSizePx / 2), 1e-9);
    }

    [Fact]
    public void ModesMenuBounds_IsTheUnionOfTheReportedRows()
    {
        // The panel's interactive strip, not its outer frame: the union of the hit circles the window
        // reported. Mutation: use only the first row → the second row's overhang is outside the zone.
        var rows = new (string Id, PhysicalPoint Center, double Radius)[]
        {
            ("modes_left", new PhysicalPoint(1800, 300), 70),
            ("modes_right", new PhysicalPoint(2000, 300), 70),
        };

        var rect = ActivationChromeZones.ModesMenuBounds(rows);

        Check.That(rect).IsNotNull();
        Check.That(rect!.Value.X).IsCloseTo(1730, 1e-9);
        Check.That(rect.Value.Y).IsCloseTo(230, 1e-9);
        Check.That(rect.Value.Width).IsCloseTo(340, 1e-9);
        Check.That(rect.Value.Height).IsCloseTo(140, 1e-9);
    }

    [Fact]
    public void ModesMenuBounds_IsNullBeforeAnyPlacement()
    {
        Check.That(ActivationChromeZones.ModesMenuBounds(
            Array.Empty<(string, PhysicalPoint, double)>())).IsNull();
    }

    [Fact]
    public void CollectFromProduction_ReadsTheToolbarsPhysicalBounds_WhenOnScreen()
    {
        // Review A A7 end to end: the production collector must include the toolbar's real bounds.
        // Mutation: drop the toolbar line in CollectFromProduction → this goes red.
        var toolbarRect = new PhysicalRect(695, 0, 2450, 288);
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var context = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            Settings = settings,
            Messenger = messenger,
            KeyboardVM = new KeyboardViewModel(settings, messenger),
            ActionMenuVM = new ActionMenuViewModel(settings, messenger),
            ModesMenuVM = new ModesMenuViewModel(settings, messenger),
            OverlayManager = TestFakes.MakeOverlayManager(toolbar: new FakeToolbar(toolbarRect)),
        };

        var zones = ActivationChromeZones.CollectFromProduction(context);

        Check.That(zones).ContainsExactly(toolbarRect);
    }

    [Fact]
    public void ActionMenuGeometry_KeepsItsOwnButtonRadius_NotTheSharedOne()
    {
        // The zone depends on the menu geometry's button radius (its own constant since 2026-08-26);
        // a drift to LayoutConstants.SharedButtonRadius would shrink every action-menu zone.
        Check.That(ActionMenuGeometry.ButtonRadius).IsEqualTo(EyeCtrl.Core.LayoutConstants.ActionMenuButtonRadius);
        Check.That(ActionMenuGeometry.ButtonRadius).IsNotEqualTo(EyeCtrl.Core.LayoutConstants.SharedButtonRadius);
    }

    /// <summary>A toolbar that is on screen at a fixed physical rect. The collector reads only
    /// <see cref="IsOnScreen"/> and <see cref="GetPhysicalBounds"/>.</summary>
    private sealed class FakeToolbar : IToolbarOverlay
    {
        private readonly PhysicalRect _bounds;
        public FakeToolbar(PhysicalRect bounds) => _bounds = bounds;

        public bool IsVisible => true;
        public bool TargetsInitialized => true;
        public bool IsSliding => false;
        public bool IsOnScreen => true;
        public double ExtentDip => _bounds.Height / 2.0;

        public PhysicalRect GetPhysicalBounds() => _bounds;
        public void Show() { }
        public void Hide() { }
        public void Initialize(double buttonSize, int dwellTimeMs, bool includeReset) { }
        public void SetMode(bool isDocked, string edge) { }
        public void SetSelectionModeState(SelectionMode mode) { }
        public void SetPausedState(bool isPaused) { }
        public void UpdateGaze(PhysicalPoint screenPoint, long timestampMicroseconds) { }
        public void BindToViewModel() { }
        public void ShowAtRest() { }
        public void ShowThenSlideIn() { }
        public void SlideOutThenHide(Action onHidden) => onHidden();
    }
}
