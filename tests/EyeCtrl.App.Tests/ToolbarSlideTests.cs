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
}
