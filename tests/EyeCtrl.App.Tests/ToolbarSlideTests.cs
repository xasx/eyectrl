namespace EyeCtrl.App.Tests;

using System.Windows;
using EyeCtrl.App.Tests.Fixtures;
using Xunit;

/// <summary>
/// The toolbar's slide transition: the two state flags, the sequencing, and cancellation.
///
/// <para>
/// A test STA thread has no message pump, so an animation clock never advances and never raises
/// Completed here. That is the same constraint that produced the EndFlash seam
/// (ToolbarWindow.xaml.cs, ToolbarBand.EndFlash), and the completion path is exercised the same
/// way: driven directly through <c>CompleteSlideForTest</c> rather than waited on. Driving it by
/// hand is also what makes the generation guard observable at all — see
/// <see cref="ForceShow_MidSlideOut_DefusesThePendingHide"/>.
/// </para>
///
/// <para>
/// Category=UI because these construct and Show real windows; CI runs Category!=UI
/// (.github/workflows/ci.yml:84), so these are verified locally.
/// </para>
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[Collection("WpfApplication")]
public class ToolbarSlideTests
{
    /// <summary>A shown, laid-out, top-docked toolbar — the state every slide starts from.</summary>
    private static EyeCtrl.App.Overlays.ToolbarWindow NewDockedToolbar()
    {
        var (window, _) = ToolbarTestHelpers.NewBoundToolbar();
        window.Show();
        window.SetMode(isDocked: true, edge: "Top");
        return window;
    }

    [StaFact]
    [Trait("Category", "UI")]
    public void AtRest_IsOnScreen_AndNotSliding()
    {
        var window = NewDockedToolbar();
        try
        {
            Check.That(window.IsSliding).IsFalse();
            Check.That(window.IsSlidingOut).IsFalse();
            Check.That(window.IsOnScreen).IsTrue();
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The directional half of the gate. During a slide-OUT the band is leaving and the window is
    /// about to hide, so arming dwell against the published coordinates is unsafe.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void DuringSlideOut_IsNotOnScreen()
    {
        var window = NewDockedToolbar();
        try
        {
            window.SlideOutThenHide(() => { });

            Check.That(window.IsSliding).IsTrue();
            Check.That(window.IsSlidingOut).IsTrue();
            Check.That(window.IsOnScreen).IsFalse();
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The other half, and the one a single-flag implementation gets wrong. During a slide-IN the
    /// band is visible and staying, dwell is armed by design, and IsOnScreen must say so — because
    /// the slide-in's completion no longer re-arms anything, so a gate that reported false here
    /// would let a task-switcher open/close cycle leave the toolbar visible, at rest, and armed by
    /// nothing.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void DuringSlideIn_IsOnScreen_IsTrue()
    {
        var window = NewDockedToolbar();
        try
        {
            window.SlideOutThenHide(() => { });
            window.CompleteSlideForTest();          // now hidden, transform parked at the offset

            window.ShowThenSlideIn();

            Check.That(window.IsSliding).IsTrue();
            Check.That(window.IsSlidingOut).IsFalse();
            Check.That(window.IsOnScreen).IsTrue();
        }
        finally { window.Close(); }
    }

    [StaFact]
    [Trait("Category", "UI")]
    public void WhenHidden_IsNotOnScreen()
    {
        var window = NewDockedToolbar();
        try
        {
            window.SlideOutThenHide(() => { });
            window.CompleteSlideForTest();

            Check.That(window.IsVisible).IsFalse();
            Check.That(window.IsOnScreen).IsFalse();
            Check.That(window.IsSliding).IsFalse();
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The clock is attached before Show(), so no frame can be painted with the band at its rest
    /// position. Asserted through IsSliding at the moment IsVisibleChanged fires — that event is
    /// raised synchronously inside Show(), and it needs no clock tick, which a pumpless STA thread
    /// could not deliver anyway.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void SlideIn_AttachesTheClock_BeforeShow()
    {
        var window = NewDockedToolbar();
        try
        {
            window.SlideOutThenHide(() => { });
            window.CompleteSlideForTest();

            bool? slidingWhenShown = null;
            void OnVisibleChanged(object s, DependencyPropertyChangedEventArgs e)
            {
                if (window.IsVisible) slidingWhenShown = window.IsSliding;
            }
            window.IsVisibleChanged += OnVisibleChanged;

            window.ShowThenSlideIn();
            window.IsVisibleChanged -= OnVisibleChanged;

            Check.That(slidingWhenShown.HasValue).IsTrue();   // the event fired at all
            Check.That(slidingWhenShown!.Value).IsTrue();     // and the clock was already attached
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The docked edge selects the animation AXIS, not just the sign. A left-docked toolbar must
    /// retract horizontally; nothing else in this class would notice if both edges animated Y,
    /// because the default fixture is top-docked.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void SideDock_SlidesAlongX_NotY()
    {
        var (window, _) = ToolbarTestHelpers.NewBoundToolbar();
        try
        {
            window.Show();
            window.SetMode(isDocked: true, edge: "Left");
            window.UpdateLayout();

            window.SlideOutThenHide(() => { });
            window.CompleteSlideForTest();

            Check.That(window.SlideTransform.Y).IsEqualTo(0d);
            Check.That(window.SlideTransform.X).IsStrictlyLessThan(0d);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Already on screen and at rest: arm dwell, do not animate. Unreachable from the controller
    /// (it calls ShowToolbar only from Hidden) but reached by ToolbarVisibilityAdapterTests, and a
    /// gratuitous jump-to-offset-then-slide-back would be a real glitch if a future caller did it.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void SlideIn_OnAnAlreadyVisibleToolbar_ShortCircuits()
    {
        var window = NewDockedToolbar();
        try
        {
            window.UpdateLayout();
            Check.That(window.IsOnScreen).IsTrue();

            window.ShowThenSlideIn();

            Check.That(window.IsSliding).IsFalse();
            Check.That(window.SlideTransform.X).IsEqualTo(0d);
            Check.That(window.SlideTransform.Y).IsEqualTo(0d);
        }
        finally { window.Close(); }
    }

    [StaFact]
    [Trait("Category", "UI")]
    public void SlideOutCompletion_HidesTheWindow_AndRunsItsCallback()
    {
        var window = NewDockedToolbar();
        try
        {
            var ran = false;
            window.SlideOutThenHide(() => ran = true);

            Check.That(window.IsVisible).IsTrue();   // still visible mid-slide
            Check.That(ran).IsFalse();

            window.CompleteSlideForTest();

            Check.That(window.IsVisible).IsFalse();
            Check.That(ran).IsTrue();
            Check.That(window.IsSliding).IsFalse();
            Check.That(window.IsSlidingOut).IsFalse();
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Already hidden: no animation, and the callback runs synchronously. SetPaused and
    /// ReassertVisibility both re-assert a hide on an already-hidden toolbar, and animating an
    /// invisible window would delay the pause indicator by the slide duration — latency on the one
    /// path whose whole job is telling a paused user that the app is paused.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void SlideOut_OnAnAlreadyHiddenToolbar_ShortCircuits()
    {
        var window = NewDockedToolbar();
        try
        {
            window.SlideOutThenHide(() => { });
            window.CompleteSlideForTest();

            var ran = false;
            window.SlideOutThenHide(() => ran = true);

            Check.That(ran).IsTrue();                 // synchronous, no clock
            Check.That(window.IsSliding).IsFalse();
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The flagship interleaving: a modal opens mid-slide-out, so ForceShow must both stop the
    /// animation and defuse its pending Hide(). The flags assertion is the one that catches a
    /// CancelSlide that forgets to clear them — show/transform/no-hide all pass without it, and
    /// the stuck-true flags would leave IsOnScreen false forever.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void ForceShow_MidSlideOut_DefusesThePendingHide()
    {
        var window = NewDockedToolbar();
        try
        {
            var ran = false;
            window.SlideOutThenHide(() => ran = true);

            window.ShowAtRest();

            // The superseded slide's completion must be a no-op.
            window.CompleteSlideForTest();

            Check.That(window.IsVisible).IsTrue();
            Check.That(ran).IsFalse();
            Check.That(window.SlideTransform.X).IsEqualTo(0d);
            Check.That(window.SlideTransform.Y).IsEqualTo(0d);
            Check.That(window.IsSliding).IsFalse();
            Check.That(window.IsSlidingOut).IsFalse();
            Check.That(window.IsOnScreen).IsTrue();
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// ForceShow after a slide-out has fully COMPLETED. The transform's base value is parked at
    /// the hidden offset at this point, so ForceShow's own zeroing write is the only thing that
    /// brings the band back — delete it and the toolbar shows permanently off-screen with no
    /// visible cause.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void ForceShow_AfterACompletedSlideOut_ParksTheBandAtRest()
    {
        var window = NewDockedToolbar();
        try
        {
            window.SlideOutThenHide(() => { });
            window.CompleteSlideForTest();
            Check.That(window.SlideTransform.Y).IsNotEqualTo(0d);   // parked off-edge

            window.ShowAtRest();

            Check.That(window.IsVisible).IsTrue();
            Check.That(window.SlideTransform.X).IsEqualTo(0d);
            Check.That(window.SlideTransform.Y).IsEqualTo(0d);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// A floating toolbar has no edge to retract into, so both transitions degrade to an instant
    /// show/hide rather than animating.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void Floating_DoesNotSlide()
    {
        var (window, _) = ToolbarTestHelpers.NewBoundToolbar();
        try
        {
            window.Show();
            window.SetMode(isDocked: false, edge: "Top");

            var ran = false;
            window.SlideOutThenHide(() => ran = true);

            Check.That(ran).IsTrue();
            Check.That(window.IsSliding).IsFalse();
            Check.That(window.IsVisible).IsFalse();
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Placement publishes each button's centre through PointToScreen, which INCLUDES the render
    /// transform — and UpdateButtonScreenPosition creates a target on its first call, so one bad
    /// publish is permanent. This is the failure that shipped once and left no toolbar button
    /// dwellable at all, arrived at from a new direction.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void MidSlide_PublishesNoTargets()
    {
        var window = NewDockedToolbar();
        try
        {
            window.UpdateLayout();
            window.SlideOutThenHide(() => { });
            Check.That(window.IsSliding).IsTrue();

            window.UpdateGaze(new EyeCtrl.Core.Models.PhysicalPoint(100, 100), 0);

            Check.That(window.TargetsInitialized).IsFalse();
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The transform stays parked at the hidden offset for the WHOLE hidden period, not merely
    /// while a clock is live — so a placement while hidden would publish rest-plus-offset for every
    /// button and latch it. Note this state is safe in the pre-slide code (no transform exists);
    /// the transform is what makes it hazardous.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void WhileHiddenWithTheBandParkedOffEdge_PublishesNoTargets()
    {
        var window = NewDockedToolbar();
        try
        {
            window.UpdateLayout();
            window.SlideOutThenHide(() => { });
            window.CompleteSlideForTest();

            Check.That(window.IsSliding).IsFalse();                  // not the guard under test
            Check.That(window.SlideTransform.Y).IsNotEqualTo(0d);    // the guard under test

            window.UpdateGaze(new EyeCtrl.Core.Models.PhysicalPoint(100, 100), 0);

            Check.That(window.TargetsInitialized).IsFalse();
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The slide-IN half of the guard, and the only state where the `_sliding` flag is the sole
    /// thing suppressing placement — which makes this the test that gives mutation row 2 its teeth.
    ///
    /// <para>
    /// Why this state and not the slide-out: on a pumpless STA thread an animation clock never
    /// ticks, so a property read after BeginAnimation returns the BASE value, and the base value is
    /// always written to the slide's destination. For a slide-out that destination is the off-edge
    /// offset, so the transform already reads non-zero and the at-rest check alone suppresses
    /// placement. For a slide-IN the destination is 0, so the transform reads at-rest while the
    /// band is in fact mid-flight, and only the flag is left. Measured, not reasoned: an earlier
    /// draft of this plan had this backwards and mutation row 2 could not fail.
    /// </para>
    ///
    /// <para>
    /// The spec requires placement suppression "in both directions"; without this test only the
    /// slide-out direction was covered.
    /// </para>
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void MidSlideIn_PublishesNoTargets()
    {
        var window = NewDockedToolbar();
        try
        {
            window.UpdateLayout();
            window.SlideOutThenHide(() => { });
            window.CompleteSlideForTest();

            window.ShowThenSlideIn();

            Check.That(window.IsSliding).IsTrue();
            Check.That(window.SlideTransform.Y).IsEqualTo(0d);   // base value == destination == rest

            window.UpdateGaze(new EyeCtrl.Core.Models.PhysicalPoint(100, 100), 0);

            Check.That(window.TargetsInitialized).IsFalse();
        }
        finally { window.Close(); }
    }

    /// <summary>Once the band is back at rest, placement proceeds as before.</summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void AtRest_PublishesTargets()
    {
        var window = NewDockedToolbar();
        try
        {
            window.UpdateLayout();
            Check.That(window.FacesAreArranged()).IsTrue();

            window.UpdateGaze(new EyeCtrl.Core.Models.PhysicalPoint(100, 100), 0);

            Check.That(window.TargetsInitialized).IsTrue();
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The X-axis half of the placement guard's second disjunct
    /// (<c>SlideTransform.X != 0</c>). Every other test in this class docks Top, so
    /// <c>SlideTransform.X</c> never moves and that disjunct is exercised by nothing — a mutation
    /// deleting it from the guard would not be caught. Left-docked clone of
    /// <see cref="WhileHiddenWithTheBandParkedOffEdge_PublishesNoTargets"/>, docked the way
    /// <see cref="SideDock_SlidesAlongX_NotY"/> docks Left.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void WhileHiddenWithTheBandParkedOffEdge_Left_PublishesNoTargets()
    {
        var (window, _) = ToolbarTestHelpers.NewBoundToolbar();
        try
        {
            window.Show();
            window.SetMode(isDocked: true, edge: "Left");
            window.UpdateLayout();

            window.SlideOutThenHide(() => { });
            window.CompleteSlideForTest();

            Check.That(window.IsSliding).IsFalse();                  // not the guard under test
            Check.That(window.SlideTransform.X).IsNotEqualTo(0d);    // the guard under test
            Check.That(window.SlideTransform.Y).IsEqualTo(0d);

            window.UpdateGaze(new EyeCtrl.Core.Models.PhysicalPoint(100, 100), 0);

            Check.That(window.TargetsInitialized).IsFalse();
        }
        finally { window.Close(); }
    }
}
