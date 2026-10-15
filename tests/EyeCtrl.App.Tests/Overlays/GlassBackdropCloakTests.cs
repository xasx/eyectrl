namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using Xunit;

/// <summary>
/// The flicker fix's second half, headless: DWM's window transitions are disabled at handle
/// creation, and <c>ShowBackdrop</c> CLOAKS the window before it is shown (DWM would otherwise
/// compose its own first frame of a half-prepared window), uncloaking only after WPF has rendered a
/// frame — a <see cref="DispatcherPriority.Background"/> callback after the render commit, then one
/// <c>DwmFlush</c> so the finished frame is presented before the cloak lifts — with a 250 ms
/// fallback. The lifecycle is pinned too (review-10's conditions): <c>Hide()</c> cancels the pending
/// uncloak AND uncloaks, the plain <c>Show()</c> uncloaks, <c>Close()</c> cancels the fallback, and
/// the magnifier → ring hand-off (a hide and a re-show with UNCHANGED settings within a frame)
/// reuses the prepared content instead of clearing it — no re-apply, no cloak, and therefore no bare
/// or undimmed frame.
///
/// <para>
/// All DWM writes go through the <c>SetDwmAttribute</c> seam and all accent writes through the
/// <c>SetAccentPolicy</c> seam, so the gates record attribute, value and whether the window was
/// visible at the call. The window runs OFF-SCREEN (the <c>PositionForShow</c> seam), so nothing is
/// painted on the owner's screen.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class GlassBackdropCloakTests
{
    [StaFact]
    public void TheTransitionsAreDisabled_AtHandleCreation()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            var recorded = new List<(int Attribute, bool Value)>();
            var real = window.SetDwmAttribute;
            window.SetDwmAttribute = (hwnd, attribute, value) =>
            {
                recorded.Add((attribute, value));
                return real(hwnd, attribute, value);
            };

            _ = new WindowInteropHelper(window).EnsureHandle(); // raises SourceInitialized

            Check.WithCustomMessage(
                    $"expected a (DWMWA_TRANSITIONS_FORCEDISABLED, true) write at handle creation, saw: "
                    + string.Join(" | ", recorded))
                .That(recorded).Contains((3, true));
        }
        finally
        {
            try { window.Close(); } catch { /* never shown; nothing to clean beyond the handle */ }
        }
    }

    [StaFact]
    public void ShowBackdrop_CloaksBeforeTheShow_AndUncloaksAfterTheFirstFrame_BothShows()
    {
        var run = Start(0.3);
        try
        {
            run.Window.ShowBackdrop(run.Settings, Center);

            // Directly pinned: the show itself writes ONLY cloak(true) — no uncloak may land before
            // the first rendered frame (or the fallback). An immediate uncloak would defeat the
            // whole cloak; without this assert that timing is held only indirectly.
            Check.WithCustomMessage($"the show uncloaked without a frame: {string.Join(" | ", run.Cloaks)}")
                .That(run.Cloaks).HasSize(1);
            Check.That(run.Cloaks[0]).IsEqualTo((true, false));

            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(400));
            // A real re-show, later than the hand-off window, so the cloak path is the one pinned.
            run.Window.Hide();
            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(200));
            run.Window.ShowBackdrop(run.Settings, Center);

            // The same on the re-show: cloaked, and nothing uncloaked it yet.
            Check.WithCustomMessage($"the re-show uncloaked without a frame: {string.Join(" | ", run.Cloaks)}")
                .That(run.Cloaks).HasSize(3);
            Check.That(run.Cloaks[2]).IsEqualTo((true, false));

            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(400));

            // Each show: cloak(true) while hidden, then cloak(false) after a frame.
            Check.WithCustomMessage($"unexpected cloak sequence: {string.Join(" | ", run.Cloaks)}")
                .That(run.Cloaks).HasSize(4);
            Check.That(run.Cloaks[0]).IsEqualTo((true, false));   // (cloaked, visibleAtCall)
            Check.That(run.Cloaks[1]).IsEqualTo((false, true));
            Check.That(run.Cloaks[2]).IsEqualTo((true, false));
            Check.That(run.Cloaks[3]).IsEqualTo((false, true));
            Check.That(run.Window.IsVisible).IsTrue();
        }
        finally
        {
            Cleanup(run.Window);
        }
    }

    [StaFact]
    public void TheUncloakFlushesTheFinishedFrame_BeforeLiftingTheCloak()
    {
        var run = Start(0.3);
        try
        {
            // ONE sequence across both seams plus a Rendering marker: the flush must come AFTER a
            // frame has rendered and BEFORE the uncloak write. Uncloaking first would let DWM compose
            // the pre-render surface (the glass-only frame the cloak exists to hide).
            var sequence = new List<string>();
            var realFlush = run.Window.FlushDwm;
            run.Window.FlushDwm = () =>
            {
                sequence.Add("flush");
                realFlush();
            };
            var realDwm = run.Window.SetDwmAttribute;
            run.Window.SetDwmAttribute = (hwnd, attribute, value) =>
            {
                if (attribute == 13) sequence.Add(value ? "cloak:true" : "cloak:false");
                return realDwm(hwnd, attribute, value);
            };
            var marker = (object? _, EventArgs _) => sequence.Add("rendering");
            EventHandler onRendering = (s, e) => marker(s, e);
            CompositionTarget.Rendering += onRendering; // subscribed first: its marker precedes the window's handler
            try
            {
                run.Window.ShowBackdrop(run.Settings, Center);
                LiveCheck.PumpFor(TimeSpan.FromMilliseconds(400));
            }
            finally
            {
                CompositionTarget.Rendering -= onRendering;
            }

            // The marker that matters is the FIRST rendering AFTER the cloak went up (the show's own
            // frame): ambient frames from earlier tests may precede the show, and an immediate
            // uncloak right after base.Show() would flush with no frame in between.
            var cloakTrue = sequence.IndexOf("cloak:true");
            var flush = sequence.IndexOf("flush");
            var uncloak = sequence.IndexOf("cloak:false");
            var frameAfterCloak = cloakTrue >= 0
                ? sequence.FindIndex(cloakTrue + 1, item => item == "rendering")
                : -1;
            Check.WithCustomMessage($"unexpected sequence: {string.Join(" | ", sequence)}")
                .That(cloakTrue).IsStrictlyGreaterThan(-1);
            Check.WithCustomMessage($"no frame rendered between the cloak and the flush: {string.Join(" | ", sequence)}")
                .That(frameAfterCloak).IsStrictlyGreaterThan(-1);
            Check.WithCustomMessage($"the flush came before a frame rendered: {string.Join(" | ", sequence)}")
                .That(flush).IsStrictlyGreaterThan(frameAfterCloak);
            Check.WithCustomMessage($"the cloak was lifted BEFORE the flush presented the finished frame: {string.Join(" | ", sequence)}")
                .That(uncloak).IsStrictlyGreaterThan(flush);
        }
        finally
        {
            Cleanup(run.Window);
        }
    }

    [StaFact]
    public void ShowBackdrop_OnAVisibleWindow_DoesNotRecloakOrReapply()
    {
        var run = Start(0.3);
        try
        {
            run.Window.ShowBackdrop(run.Settings, Center);
            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(400));
            var policies = run.Policies.Count;
            var cloaks = run.Cloaks.Count;
            var children = run.CanvasChildren;

            // A show on an already-visible window with UNCHANGED settings is a no-op: no accent
            // re-send, no cloak (which would blank a window that is on screen), no clear.
            run.Window.ShowBackdrop(run.Settings, Center);
            Check.WithCustomMessage("an unchanged show on a visible window re-sent the accent.")
                .That(run.Policies.Count).IsEqualTo(policies);
            Check.WithCustomMessage("an unchanged show on a visible window re-cloaked it.")
                .That(run.Cloaks.Count).IsEqualTo(cloaks);
            Check.WithCustomMessage("an unchanged show on a visible window cleared its content.")
                .That(run.CanvasChildren).IsEqualTo(children);
            Check.That(run.Window.IsVisible).IsTrue();

            // Changed settings are re-applied in place — one synchronous prepare — and never cloaked.
            var changed = new BackdropSettings { Type = BackdropType.Mica, Enabled = true, Opacity = 0.5 };
            run.Window.ShowBackdrop(changed, Center);
            Check.WithCustomMessage("changed settings on a visible window must re-apply the accent.")
                .That(run.Policies.Count).IsStrictlyGreaterThan(policies);
            Check.WithCustomMessage("changed settings on a visible window must not cloak it.")
                .That(run.Cloaks.Count).IsEqualTo(cloaks);
            Check.That(run.Window.IsVisible).IsTrue();

            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(200));
            Check.WithCustomMessage("the in-place re-apply must not leave a cloak behind.")
                .That(run.Cloaks.Count).IsEqualTo(cloaks);
            Check.That(run.Window.IsVisible).IsTrue();
        }
        finally
        {
            Cleanup(run.Window);
        }
    }

    [StaFact]
    public void TheHandOff_ResetsTheGlyphsDwellProgress()
    {
        var run = Start(0.3);
        try
        {
            run.Window.ShowBackdrop(run.Settings, Center);
            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(400));
            Check.WithCustomMessage("the gate's own fixture: the ✕ carries dwell arc paths.")
                .That(run.VisibleArcs).IsStrictlyGreaterThan(0);

            // The interrupted dwell left progress on the ✕ (0.8 shows the progress and halo arcs);
            // the hand-off is a new show, so the glyph must come back at 0 (arcs collapsed) instead
            // of resuming a half-filled dwell.
            run.Window.UpdateHideButtonProgress(0.8);
            run.Window.Hide();
            run.Window.ShowBackdrop(run.Settings, Center);

            Check.WithCustomMessage("the hand-off kept the interrupted dwell's progress on the ✕.")
                .That(run.VisibleArcs).IsEqualTo(0);
        }
        finally
        {
            Cleanup(run.Window);
        }
    }

    [StaFact]
    public void Hide_DefersTheWindowHide_ButTheHintGoesAtOnce()
    {
        var run = Start(0.3);
        try
        {
            run.Window.ShowBackdrop(run.Settings, Center); // no pump: the uncloak is still pending
            run.Window.ShowEscapeHint();
            Check.That(run.Cloaks).HasSize(1);

            run.Window.Hide();

            // The window hide is DEFERRED (one Background post) so the hand-off's show can cancel
            // it — nothing on screen may blink; the caregiver-facing hint, however, must not linger.
            Check.WithCustomMessage("Hide hid the window synchronously.")
                .That(run.Window.IsVisible).IsTrue();
            Check.WithCustomMessage("the hint must go at once.")
                .That(run.HintVisibility).IsEqualTo(Visibility.Collapsed);
            Check.WithCustomMessage($"the deferred hide must not have uncloaked yet: {string.Join(" | ", run.Cloaks)}")
                .That(run.Cloaks).HasSize(1);

            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(400));

            Check.That(run.Window.IsVisible).IsFalse();
            Check.WithCustomMessage($"the hide must release the cloak exactly once: {string.Join(" | ", run.Cloaks)}")
                .That(run.Cloaks).HasSize(2);
            Check.That(run.Cloaks[1]).IsEqualTo((false, true));

            // A stale uncloak (the show's pending Rendering callback) must not fire a third write.
            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(200));
            Check.WithCustomMessage($"a late uncloak fired: {string.Join(" | ", run.Cloaks)}")
                .That(run.Cloaks).HasSize(2);
        }
        finally
        {
            Cleanup(run.Window);
        }
    }

    [StaFact]
    public void Hide_ThenClose_WithAPendingHide_IsSafe()
    {
        var run = Start(0.3);
        try
        {
            run.Window.ShowBackdrop(run.Settings, Center);
            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(400));
            var cloaksBefore = run.Cloaks.Count;

            run.Window.Hide();  // posts the deferred hide
            run.Window.Close(); // OnClosed must abort it — a leaked base.Hide() on a closed window throws

            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(400)); // drain: a leaked post would surface here

            Check.WithCustomMessage("the window survived Close as visible.")
                .That(run.Window.IsVisible).IsFalse();
            Check.WithCustomMessage($"no cloak write belongs to a closed window: {string.Join(" | ", run.Cloaks)}")
                .That(run.Cloaks).HasSize(cloaksBefore);
        }
        finally
        {
            Cleanup(run.Window);
        }
    }

    [StaFact]
    public void Close_CancelsTheFallbackTimer()
    {
        var run = Start(0.3);
        try
        {
            run.Window.ShowBackdrop(run.Settings, Center); // cloaked, 250 ms fallback armed
            run.Window.Close();

            // OnClosed cancels the pending uncloak; the fallback timer must not fire a write on the
            // closed window. (The cloak bit is irrelevant by then — the handle is gone — so the only
            // write is the show's cloak(true).)
            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(400));

            Check.WithCustomMessage($"the fallback fired after Close: {string.Join(" | ", run.Cloaks)}")
                .That(run.Cloaks).HasSize(1);
            Check.That(run.Cloaks[0]).IsEqualTo((true, false));
        }
        finally
        {
            Cleanup(run.Window);
        }
    }

    [StaFact]
    public void TheFallbackUncloaks_WhenNoFrameIsRendered()
    {
        var run = Start(0.3);
        try
        {
            run.Window.ShowBackdrop(run.Settings, Center); // cloaked; the uncloak is pending
            Check.That(run.Cloaks).HasSize(1);

            // Prove the FALLBACK, not the frame path: thread through Normal-and-above only, so
            // CompositionTarget.Rendering (Render priority) cannot run — the 250 ms fallback timer
            // must still uncloak. A Send-priority timer would never be dispatched here and the window
            // would stay cloaked forever.
            var until = Environment.TickCount64 + 600;
            while (Environment.TickCount64 < until)
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Normal);

            Check.WithCustomMessage($"the fallback did not uncloak: {string.Join(" | ", run.Cloaks)}")
                .That(run.Cloaks).HasSize(2);
            Check.That(run.Cloaks[1]).IsEqualTo((false, true));
        }
        finally
        {
            Cleanup(run.Window);
        }
    }

    [StaFact]
    public void TheHandOff_WithinAFrameOfAHide_NeverLeavesTheScreen_NoCloakNoReapply()
    {
        var run = Start(0.3);
        try
        {
            run.Window.ShowBackdrop(run.Settings, Center);
            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(400));
            var policiesBefore = run.Policies.Count;
            var cloaksBefore = run.Cloaks.Count;
            var childrenBefore = run.CanvasChildren;
            Check.WithCustomMessage("the prepared show must leave the dim layer on the canvas.")
                .That(run.Rectangles).IsEqualTo(1);
            Check.WithCustomMessage("the prepared show must leave content (the ✕ is several elements).")
                .That(childrenBefore).IsStrictlyGreaterThan(1);

            // Production's magnifier → ring hand-off: Hide and ShowBackdrop in two back-to-back
            // dispatcher posts. The hide is deferred, so the show cancels it and the window NEVER
            // leaves the screen — no re-show, no cloak, no re-apply, no blink.
            var wentInvisible = 0;
            void OnVisibleChanged(object? sender, DependencyPropertyChangedEventArgs e)
            {
                if (!run.Window.IsVisible) wentInvisible++;
            }

            run.Window.IsVisibleChanged += OnVisibleChanged;
            try
            {
                run.Window.Hide();
                run.Window.ShowBackdrop(run.Settings, Center);
                LiveCheck.PumpFor(TimeSpan.FromMilliseconds(400)); // drain well past Background
            }
            finally
            {
                run.Window.IsVisibleChanged -= OnVisibleChanged;
            }

            Check.WithCustomMessage("the hand-off window left the screen — a frame of desktop or undimmed glass.")
                .That(wentInvisible).IsEqualTo(0);
            Check.That(run.Window.IsVisible).IsTrue();
            Check.WithCustomMessage("the hand-off re-sent the accent instead of keeping the prepared content.")
                .That(run.Policies.Count).IsEqualTo(policiesBefore);
            Check.WithCustomMessage("the hand-off re-cloaked, which is the bare-desktop gap it exists to avoid.")
                .That(run.Cloaks.Count).IsEqualTo(cloaksBefore);
            Check.WithCustomMessage("the hand-off cleared the prepared content.")
                .That(run.CanvasChildren).IsEqualTo(childrenBefore);
        }
        finally
        {
            Cleanup(run.Window);
        }
    }

    [StaFact]
    public void ChangedSettings_DuringAPendingHide_ReApplyInPlace_AndTheHideIsCancelled()
    {
        var run = Start(0.3);
        try
        {
            run.Window.ShowBackdrop(run.Settings, Center);
            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(400));
            var policiesBefore = run.Policies.Count;
            var cloaksBefore = run.Cloaks.Count;

            run.Window.Hide();
            var changed = new BackdropSettings { Type = BackdropType.Mica, Enabled = true, Opacity = 0.5 };
            run.Window.ShowBackdrop(changed, Center);

            // The window is still visible, so the change is F5's in-place re-apply (no cloak), and
            // the pending hide is cancelled — the window stays on screen.
            Check.WithCustomMessage("changed settings during a pending hide must re-apply the accent.")
                .That(run.Policies.Count).IsStrictlyGreaterThan(policiesBefore);
            Check.WithCustomMessage("changed settings during a pending hide must not cloak the window.")
                .That(run.Cloaks.Count).IsEqualTo(cloaksBefore);

            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(400));
            Check.WithCustomMessage("the cancelled hide ran anyway.")
                .That(run.Window.IsVisible).IsTrue();
            Check.WithCustomMessage("the in-place re-apply must not cloak.")
                .That(run.Cloaks.Count).IsEqualTo(cloaksBefore);
        }
        finally
        {
            Cleanup(run.Window);
        }
    }

    [StaFact]
    public void TheCloakKeepsTheNeverActivateStyles_AndTheCaptureExclusion()
    {
        var run = Start(0.3);
        try
        {
            run.Window.ShowBackdrop(run.Settings, Center); // no pump: still cloaked

            var hwnd = new WindowInteropHelper(run.Window).Handle;
            const int GwlExStyle = -20;
            const int WsExTransparent = 0x00000020;
            const int WsExNoActivate = 0x08000000;
            var exStyle = GetWindowLong(hwnd, GwlExStyle);
            Check.WithCustomMessage($"cloaked window lost WS_EX_NOACTIVATE: exstyle 0x{exStyle:X8}")
                .That((exStyle & WsExNoActivate) != 0).IsTrue();
            Check.WithCustomMessage($"cloaked window lost click-through: exstyle 0x{exStyle:X8}")
                .That((exStyle & WsExTransparent) != 0).IsTrue();
            Check.That(GetWindowDisplayAffinity(hwnd, out var affinity)).IsTrue();
            Check.WithCustomMessage($"cloaked window lost its capture exclusion: affinity 0x{affinity:X}")
                .That(affinity).IsEqualTo(0x00000011u);
        }
        finally
        {
            Cleanup(run.Window);
        }
    }

    private static readonly PhysicalPoint Center = new(100, 100);

    private sealed class Run
    {
        public GlassBackdropWindow Window { get; init; } = null!;
        public BackdropSettings Settings { get; init; } = null!;
        public List<(int State, int Flags, int Gradient)> Policies { get; } = new();
        public List<(bool Cloaked, bool VisibleAtCall)> Cloaks { get; } = new();

        public int CanvasChildren =>
            ((Canvas)Window.FindName("BackdropCanvas")!).Children.Count;

        public int Rectangles =>
            ((Canvas)Window.FindName("BackdropCanvas")!).Children.OfType<Rectangle>().Count();

        /// <summary>The Esc hint's visibility (Collapsed the moment Hide runs, even while deferred).</summary>
        public Visibility HintVisibility => ((TextBlock)Window.FindName("EscapeHint")!).Visibility;

        /// <summary>
        /// The ✕'s dwell arcs (progress + halo) that are currently VISIBLE. <c>UpdateProgress(0)</c>
        /// collapses them; any other value shows them with a swept arc, so 0 here means "the glyph
        /// shows no dwell progress".
        /// </summary>
        public int VisibleArcs =>
            ((Canvas)Window.FindName("BackdropCanvas")!).Children
                .OfType<System.Windows.Shapes.Path>()
                .Count(path => path.Visibility == Visibility.Visible
                    && path.Data is PathGeometry geometry
                    && geometry.Figures.OfType<PathFigure>().Any(figure =>
                        figure.Segments.OfType<ArcSegment>().Any()));
    }

    /// <summary>
    /// Creates the window OFF-SCREEN with both seam recorders installed. The positioner proves the
    /// bounds cannot intersect a screen before the show, so nothing is painted on the owner's screen.
    /// </summary>
    private static Run Start(double opacity)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var settings = new BackdropSettings { Type = BackdropType.Mica, Enabled = true, Opacity = opacity };
        var run = new Run
        {
            Window = new GlassBackdropWindow(),
            Settings = settings
        };

        var realDwm = run.Window.SetDwmAttribute;
        run.Window.SetDwmAttribute = (hwnd, attribute, value) =>
        {
            if (attribute == 13) run.Cloaks.Add((value, run.Window.IsVisible));
            return realDwm(hwnd, attribute, value);
        };
        var realPolicy = run.Window.SetAccentPolicy;
        run.Window.SetAccentPolicy = (hwnd, state, flags, gradient) =>
        {
            run.Policies.Add((state, flags, gradient));
            return realPolicy(hwnd, state, flags, gradient);
        };
        run.Window.PositionForShow = w =>
        {
            OverlayPositioning.CoverPrimaryScreen(w);
            w.Left = -40000;
            w.Top = -40000;
            // Prove it cannot appear BEFORE the window is shown.
            var bounds = new Rect(w.Left, w.Top, w.Width, w.Height);
            var screen = new Rect(
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            Check.WithCustomMessage("the gate runs off-screen: the window must not intersect a screen.")
                .That(screen.IntersectsWith(bounds)).IsFalse();
        };

        return run;
    }

    private static void Cleanup(GlassBackdropWindow window)
    {
        try { window.Hide(); } catch { /* the window may already be gone */ }
        try { window.Close(); } catch { /* the window may already be gone */ }
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
}
