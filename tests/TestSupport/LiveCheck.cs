namespace EyeCtrl.Tests.TestSupport;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

/// <summary>
/// T11's live-check harness: the opt-in gate, the dispatcher pump, and the proof that a window is
/// actually visible while it is held on the screen. One copy, linked into EyeCtrl.App.Tests and
/// EyeCtrl.Supervisor.Tests, so both sides of the check prove visibility the same way.
/// </summary>
/// <remarks>
/// The failure this exists for: a banner whose content sits under <c>Opacity="0"</c> in XAML and is
/// raised to 1 only by the production show path (<c>ShowCaregiverAlert</c>, <c>ShowDriftNotice</c>).
/// A live check that merely <c>Show()</c>s the window paints nothing and still "passes" — which is
/// exactly what T11's first showcase run did before fixup-02.
/// </remarks>
internal static class LiveCheck
{
    /// <summary>The opt-in gate: the showcase paints real windows on the owner's screen, which
    /// they control by gaze, so it runs only with <c>EYECTRL_LIVE_CHECK=1</c> and the owner's
    /// go-ahead.</summary>
    internal static void Require() =>
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("EYECTRL_LIVE_CHECK") is "1" or "true" or "TRUE",
            "The T11 live check paints real windows on the screen; set EYECTRL_LIVE_CHECK=1 with the owner's go-ahead.");

    /// <summary>
    /// Pumps this thread's dispatcher until the timer ends the frame. A WPF pump also dispatches
    /// the WinForms tray's messages, which is what lets the Supervisor's real tray icon answer.
    /// </summary>
    internal static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
        }
    }

    /// <summary>
    /// Shows <paramref name="window"/> through <paramref name="show"/> — the surface's production
    /// show path when it has one, so the message, the opacity and the position all come from
    /// production; a plain <see cref="Window.Show"/> otherwise — brings it to the foreground,
    /// settles one layout pass, proves it is visible, holds it for <paramref name="duration"/>,
    /// then closes it in a <c>finally</c>.
    /// </summary>
    internal static void ShowAndHold(Window window, string name, TimeSpan duration, Action? show = null)
    {
        (show ?? window.Show)();
        try
        {
            window.Activate();
            // One layout and paint pass before the proof: ActualWidth/Height exist after it, and a
            // window opened from a background test process has had its first chance to render.
            PumpFor(TimeSpan.FromMilliseconds(250));
            AssertVisible(window, name);
            PumpFor(duration);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The visibility proof, run right before the hold: the window is visible, topmost and inside
    /// the virtual screen, and every visible TextBlock with text has an effective opacity above
    /// 0.9 — the product of <see cref="UIElement.Opacity"/> from the text up to and including the
    /// window. The failure names the window and every dim text it holds, so the log says which
    /// surface stayed invisible.
    /// </summary>
    internal static void AssertVisible(Window window, string name)
    {
        Check.WithCustomMessage($"{name}: the window is not visible after it was shown.")
            .That(window.IsVisible).IsTrue();
        Check.WithCustomMessage($"{name}: the window is not Topmost, so it can sit behind the owner's active window.")
            .That(window.Topmost).IsTrue();

        var screen = new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var bounds = new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
        Check.WithCustomMessage($"{name}: its bounds {bounds} leave the virtual screen {screen}.")
            .That(screen.Contains(bounds)).IsTrue();

        var dim = FitText.Descendants<TextBlock>(window)
            .Where(text => text.IsVisible && text.Text.Length > 0)
            .Select(text => (text.Text, Opacity: EffectiveOpacity(text, window)))
            .Where(pair => pair.Opacity <= 0.9)
            .Select(pair => $"'{pair.Text}' (effective opacity {pair.Opacity:0.##})")
            .ToList();
        Check.WithCustomMessage(
                $"{name}: these texts are not effectively visible (is the XAML's Opacity still 0?):\n  "
                + string.Join("\n  ", dim))
            .That(dim).IsEmpty();
    }

    /// <summary>
    /// The per-step half of the proof (T12 review-01 F2), for steps INSIDE one window:
    /// <see cref="AssertVisible"/> proves the window, and this proves the step's target is what is
    /// on screen — a visible, effectively-opaque <see cref="TextBlock"/> carries each
    /// <paramref name="literals"/> entry. A step whose row never rendered (a navigation that
    /// quietly did nothing) fails here instead of holding the wrong page.
    /// </summary>
    internal static void AssertShowsText(Window window, string name, params string[] literals)
    {
        foreach (var literal in literals) AssertShowsOne(window, name, literal);
    }

    private static void AssertShowsOne(Window window, string name, string literal)
    {
        var matches = FitText.Descendants<TextBlock>(window)
            .Where(text => text.IsVisible && text.Text.Contains(literal, StringComparison.Ordinal))
            .Select(text => (text.Text, Opacity: EffectiveOpacity(text, window)))
            .ToList();
        Check.WithCustomMessage(
                $"{name}: no visible text carries \"{literal}\", so this step's target is not what "
                + "is on screen (did the navigation do nothing?).")
            .That(matches).Not.IsEmpty();

        var opaque = matches.Where(pair => pair.Opacity > 0.9).ToList();
        Check.WithCustomMessage(
                $"{name}: \"{literal}\" is on screen but not effectively opaque:\n  "
                + string.Join("\n  ", matches.Select(pair => $"'{pair.Text}' (effective opacity {pair.Opacity:0.##})")))
            .That(opaque).Not.IsEmpty();
    }

    /// <summary>
    /// The product of <see cref="UIElement.Opacity"/> from <paramref name="start"/> up to and
    /// including <paramref name="root"/>.
    /// </summary>
    private static double EffectiveOpacity(DependencyObject start, DependencyObject root)
    {
        var opacity = 1.0;
        for (DependencyObject? node = start; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement element) opacity *= element.Opacity;
            if (ReferenceEquals(node, root)) break;
        }

        return opacity;
    }

    /// <summary>
    /// The INK box of a text element in device pixels — the drawn glyphs, not the layout box. A
    /// corner button's layout box is its whole dwell disc (100 DIP for the backdrop's ✕), which the
    /// corner's offset decides: at the 2026-10-13 offset of 53 DIP the disc is fully on screen (a
    /// headless gate pins that), and the ink the viewer sees sits well inside either way. "Is it on
    /// the screen" is judged on the ink because the ink is what the viewer sees.
    /// The geometry is built from the element's own font at the LINE's origin
    /// (<c>ContentStart.GetCharacterRect</c>, the read <c>ArcLabelFit.InkBoxOf</c> uses) — not the
    /// element's corner, which says nothing about where a centred glyph starts — then mapped through
    /// <see cref="Visual.PointToScreen"/>.
    /// </summary>
    internal static Rect InkBoundsInDevicePixels(TextBlock element)
    {
        var lineStart = element.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        var formatted = new FormattedText(
            element.Text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(element.FontFamily, element.FontStyle, element.FontWeight, element.FontStretch),
            element.FontSize,
            element.Foreground,
            VisualTreeHelper.GetDpi(element).PixelsPerDip);
        var ink = formatted.BuildGeometry(new Point(lineStart.Left, lineStart.Top)).Bounds;
        return new Rect(
            element.PointToScreen(ink.TopLeft),
            element.PointToScreen(ink.BottomRight));
    }
}
