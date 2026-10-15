namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shapes;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using Xunit;

/// <summary>
/// The flicker fix's gates: <c>ShowBackdrop</c> prepares position, handle, material, dim layer and ✕
/// while the window is HIDDEN and shows last. What these pin is WPF's state at
/// <c>IsVisibleChanged</c> — the canvas and the accent policy are finished before the window becomes
/// visible; DWM's first COMPOSED frame is what the first-frame look's capture judges, not this.
/// Headless: the <c>PositionForShow</c> seam keeps production's <c>CoverPrimaryScreen</c> sizing but
/// moves the window to -30000, so nothing is painted on the owner's screen; the accent call is
/// wrapped to record the policy, its hwnd and whether the window was visible at the moment it ran.
/// </summary>
[Collection("WpfApplication")]
public class GlassBackdropShowBackdropTests
{
    [StaTheory]
    [InlineData(BackdropType.Mica)]
    [InlineData(BackdropType.Acrylic)]
    public void ShowBackdrop_PreparesEverythingWhileHidden_AndIsReadyBeforeVisible(BackdropType type)
    {
        var prepared = RunShowBackdrop(type, opacity: 0.3, shows: 1);

        Check.WithCustomMessage("the accent must be applied while the window is still hidden.")
            .That(prepared.Calls.Any(call => call.VisibleAtCall)).IsFalse();
        var enable = prepared.Calls[^1];
        Check.That((enable.State, enable.Flags, enable.Gradient)).IsEqualTo((4, 0x20, 0x01000000));
        Check.WithCustomMessage("a zero hwnd would make the accent fail silently into the rgb(18) fallback.")
            .That(enable.Hwnd).IsNotEqualTo(IntPtr.Zero);

        Check.That(prepared.Opened).HasSize(1);
        var frame = prepared.Opened[0];
        Check.WithCustomMessage("at the moment the window becomes visible (WPF state; DWM's composed frame is the show look's job) the dim layer must already be there.")
            .That(frame.Rects).IsEqualTo(1);
        Check.WithCustomMessage("at the moment the window becomes visible the ✕ must already be drawn.")
            .That(frame.Glyph).IsTrue();
        Check.That(frame.Policy).IsEqualTo((4, 0x20, 0x01000000));
        Check.That(frame.Hwnd).IsNotEqualTo(IntPtr.Zero);
        Check.That(prepared.LayerWidth).IsEqualTo(prepared.WindowWidth);
        Check.That(prepared.LayerHeight).IsEqualTo(prepared.WindowHeight);
        Check.WithCustomMessage("the ✕ must sit above the dim layer.")
            .That(prepared.GlyphIndex).IsStrictlyGreaterThan(prepared.LayerIndex);
    }

    [StaFact]
    public void ShowBackdrop_OnAReshow_IsReadyBeforeVisibleAgain()
    {
        var prepared = RunShowBackdrop(BackdropType.Mica, opacity: 0.3, shows: 2);

        Check.WithCustomMessage("every accent call on both shows must happen while hidden.")
            .That(prepared.Calls.Any(call => call.VisibleAtCall)).IsFalse();
        Check.WithCustomMessage("each show enables the blur once, so a re-show cannot reuse a stale accent.")
            .That(prepared.Calls.Count(call => call.State == 4)).IsEqualTo(2);
        Check.That(prepared.Opened).HasSize(2);

        var second = prepared.Opened[1];
        Check.WithCustomMessage("the re-show must be ready before it becomes visible too.")
            .That(second.Rects).IsEqualTo(1);
        Check.That(second.Glyph).IsTrue();
        Check.That(second.Policy).IsEqualTo((4, 0x20, 0x01000000));
        Check.That(second.Hwnd).IsNotEqualTo(IntPtr.Zero);
    }

    [StaFact]
    public void ShowBackdrop_WithSolidDarken_IsReadyBeforeVisible()
    {
        var prepared = RunShowBackdrop(BackdropType.SolidDarken, opacity: 0.3, shows: 1);

        var enable = prepared.Calls[^1];
        // Flags 2, not 0x20: the D1 fix (a state-2 enable re-sent while HIDDEN to an already-shown
        // window with 0x20 falls back to the system accent colour).
        Check.That((enable.State, enable.Flags, enable.Gradient)).IsEqualTo((2, 0x2, 0x4C000000));
        Check.That(enable.Hwnd).IsNotEqualTo(IntPtr.Zero);
        Check.That(prepared.Opened).HasSize(1);
        Check.That(prepared.Opened[0].Rects).IsEqualTo(0);
        Check.That(prepared.Opened[0].Glyph).IsTrue();
    }

    [StaFact]
    public void ShowBackdrop_WithTheRealSetter_TakesTheAccent_AndNeverDrawsTheFallback()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            SetOffScreenPositioning(window);
            window.ShowBackdrop(
                new BackdropSettings { Type = BackdropType.Mica, Enabled = true, Opacity = 0.3 },
                new PhysicalPoint(100, 100));

            // The REAL native setter with the REAL pre-show hwnd: accepted, so the single rectangle
            // is the pure-black dim layer, never the rgb(18) fallback.
            var rects = Rectangles(window);
            Check.WithCustomMessage("a refused accent would have drawn the rgb(18) fallback instead.")
                .That(rects).HasSize(1);
            Check.That(((System.Windows.Media.SolidColorBrush)rects[0].Fill).Color)
                .IsEqualTo(System.Windows.Media.Color.FromArgb(0x4C, 0, 0, 0));
        }
        finally
        {
            try { window.Hide(); } catch { /* the window may already be gone */ }
            try { window.Close(); } catch { /* the window may already be gone */ }
        }
    }

    private static PreparedShow RunShowBackdrop(BackdropType type, double opacity, int shows)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(2.0, 2.0); // the user's scale; the assembly hook restores it
        var window = new GlassBackdropWindow();
        try
        {
            var calls = new List<(int State, int Flags, int Gradient, IntPtr Hwnd, bool VisibleAtCall)>();
            var real = window.SetAccentPolicy;
            window.SetAccentPolicy = (hwnd, state, flags, gradient) =>
            {
                calls.Add((state, flags, gradient, hwnd, window.IsVisible));
                return real(hwnd, state, flags, gradient);
            };
            SetOffScreenPositioning(window);

            var opened = new List<(int Rects, bool Glyph, (int State, int Flags, int Gradient) Policy, IntPtr Hwnd)>();
            window.IsVisibleChanged += (_, _) =>
            {
                if (!window.IsVisible) return;
                var last = calls[^1];
                opened.Add((Rectangles(window).Count, HasGlyph(window), (last.State, last.Flags, last.Gradient), last.Hwnd));
            };

            var settings = new BackdropSettings { Type = type, Enabled = true, Opacity = opacity };
            for (var show = 0; show < shows; show++)
            {
                window.ShowBackdrop(settings, new PhysicalPoint(100, 100));
                if (show < shows - 1)
                {
                    window.Hide();
                    // A REAL re-show, later than the hand-off window: an immediate same-settings
                    // re-show is the magnifier → ring hand-off, which reuses the prepared content
                    // instead of re-applying (pinned in GlassBackdropCloakTests).
                    LiveCheck.PumpFor(TimeSpan.FromMilliseconds(200));
                }
            }

            var layer = Rectangles(window).FirstOrDefault();
            return new PreparedShow(
                calls.ToList(), opened.ToList(), // snapshots: the finally's Hide would append a disable call
                layer?.Width ?? double.NaN, layer?.Height ?? double.NaN,
                window.Width, window.Height,
                RectIndex(window), GlyphIndex(window));
        }
        finally
        {
            try { window.Hide(); } catch { /* the window may already be gone */ }
            try { window.Close(); } catch { /* the window may already be gone */ }
        }
    }

    /// <summary>
    /// Production's <c>CoverPrimaryScreen</c> sizing, then OFF-SCREEN: a headless gate must not put
    /// the backdrop on the owner's screen, and WPF makes a window shown at -30000 visible without
    /// painting anything anyone sees.
    /// </summary>
    private static void SetOffScreenPositioning(GlassBackdropWindow window) =>
        window.PositionForShow = w =>
        {
            OverlayPositioning.CoverPrimaryScreen(w);
            w.Left = -30000;
            w.Top = -30000;
        };

    private static List<Rectangle> Rectangles(GlassBackdropWindow window) =>
        ((Canvas)window.FindName("BackdropCanvas")!).Children.OfType<Rectangle>().ToList();

    private static bool HasGlyph(GlassBackdropWindow window) =>
        ((Canvas)window.FindName("BackdropCanvas")!).Children.OfType<TextBlock>()
        .Any(text => text.Text == "\u2715");

    private static int RectIndex(GlassBackdropWindow window)
    {
        var children = ((Canvas)window.FindName("BackdropCanvas")!).Children;
        return children.OfType<Rectangle>().Select(children.IndexOf).FirstOrDefault(-1);
    }

    private static int GlyphIndex(GlassBackdropWindow window)
    {
        var children = ((Canvas)window.FindName("BackdropCanvas")!).Children;
        return children.OfType<TextBlock>()
            .Where(text => text.Text == "\u2715")
            .Select(children.IndexOf)
            .FirstOrDefault(-1);
    }

    private sealed record PreparedShow(
        List<(int State, int Flags, int Gradient, IntPtr Hwnd, bool VisibleAtCall)> Calls,
        List<(int Rects, bool Glyph, (int State, int Flags, int Gradient) Policy, IntPtr Hwnd)> Opened,
        double LayerWidth,
        double LayerHeight,
        double WindowWidth,
        double WindowHeight,
        int LayerIndex,
        int GlyphIndex);
}
