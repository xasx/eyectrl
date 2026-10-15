namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels.Messages;
using NFluent;

/// <summary>
/// The T0(a) lag-mark button, headless: it is never shown (a shown overlay is the user's only input
/// surface), so the geometry is driven through its internal seams and the dwell through its public
/// <c>OnGaze</c>, as <see cref="RestartNoticeWindowTests"/> does for the card. The window IS the
/// grown hit extent: the 160 x 96 DIP visual centred inside a 48 DIP transparent margin per side.
/// </summary>
[Collection("StringTable Tests (App)")]
public class LagMarkWindowTests
{
    private static readonly Rect WorkArea = new(0, 0, 1920, 1152);

    private static void WithDpi(double scale, Action action)
    {
        var (x, y) = (DpiHelper.ScaleX, DpiHelper.ScaleY);
        try
        {
            DpiHelper.Update(scale, scale);
            action();
        }
        finally
        {
            DpiHelper.Update(x, y);
        }
    }

    /// <summary>
    /// A button whose "show" is a headless layout: <see cref="LagMarkWindow.ShowButton"/> runs for
    /// real (placement, dwell target, label) except that nothing is put on screen, because a shown
    /// overlay is the user's only input surface.
    /// </summary>
    private static LagMarkWindow HeadlessButton(IMessenger? messenger = null, Func<IReadOnlyList<Rect>>? keepClear = null) =>
        new(dwellMs: 25, messenger, keepClear, present: button =>
        {
            button.Measure(new Size(button.Width, button.Height));
            button.Arrange(new Rect(0, 0, button.Width, button.Height));
            button.UpdateLayout();
        });

    private static PhysicalPoint Centre(PhysicalRect rect) =>
        new(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

    /// <summary>The visual's rectangle inside the window, from the real layout of the shown button.</summary>
    private static Rect VisualInWindow(LagMarkWindow button) =>
        button.ButtonBorder.TransformToAncestor((Visual)button.Content)
            .TransformBounds(new Rect(button.ButtonBorder.RenderSize));

    [StaFact]
    public void TheWindow_IsAnOverlay_ThatNeverTakesTheForeground_AndIsClickThrough()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var button = new LagMarkWindow(dwellMs: 700);

        Check.That(button is OverlayWindow).IsTrue();
        Check.That(button.IsClickThrough).IsTrue();          // WS_EX_TRANSPARENT: input passes through
        Check.That(button.ExcludeFromScreenCapture).IsTrue();
        Check.That(button.Topmost).IsTrue();
        Check.That(button.ShowInTaskbar).IsFalse();
        // WS_EX_NOACTIVATE is applied by OverlayWindow for every subclass (focusable: false at
        // OnSourceInitialized); pin that this window does not override the interaction itself.
        Check.That(typeof(LagMarkWindow).GetMethod(
            "OnSourceInitialized", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)).IsNull();
        button.Close();
    }

    [StaFact]
    public void WithoutChrome_RestsAtTheWorkAreasBottomRight_Inset24_AtScale2()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        WithDpi(2.0, () =>
        {
            var button = HeadlessButton();
            try
            {
                button.Prepare(WorkArea);

                var shown = button.ShownRectDip;
                Check.That(shown.Width).IsEqualTo(160 + 2 * 48);
                Check.That(shown.Height).IsEqualTo(96 + 2 * 48);
                Check.That(shown.Right).IsEqualTo(1920 - 24);
                Check.That(shown.Bottom).IsEqualTo(1152 - 24);
                Check.WithCustomMessage("the button must lie inside the work area")
                    .That(WorkArea.Contains(shown)).IsTrue();

                // The hit area is the whole window rectangle in physical pixels.
                var hit = button.HitBounds;
                Check.That(hit.X).IsEqualTo(shown.Left * 2.0);
                Check.That(hit.Y).IsEqualTo(shown.Top * 2.0);
                Check.That(hit.Width).IsEqualTo(shown.Width * 2.0);
                Check.That(hit.Height).IsEqualTo(shown.Height * 2.0);
            }
            finally
            {
                button.Close();
            }
        });
    }

    [StaFact]
    public void WithChromeInTheBottomRight_TheButtonMovesUpOutOfIt_AndStaysInsideTheWorkArea()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        IReadOnlyList<Rect> chrome = [];
        var button = HeadlessButton(keepClear: () => chrome);
        try
        {
            // The edge bar sits in the corner the button rests in.
            chrome = [new Rect(1800, 900, 120, 252)];
            button.Prepare(WorkArea);
            Check.That(button.Top + button.Height).IsEqualTo(900 - 24);
            Check.That(WorkArea.Contains(button.ShownRectDip)).IsTrue();

            // A right-docked toolbar filling the height: up above it too.
            chrome = [new Rect(1770, 700, 150, 452)];
            button.Prepare(WorkArea);
            Check.That(button.Top + button.Height).IsEqualTo(700 - 24);
            Check.That(WorkArea.Contains(button.ShownRectDip)).IsTrue();

            // Chrome nowhere near the corner changes nothing.
            chrome = [new Rect(0, 0, 300, 300)];
            button.Prepare(WorkArea);
            Check.That(button.Top + button.Height).IsEqualTo(1152 - 24);
        }
        finally
        {
            button.Close();
        }
    }

    /// <summary>
    /// Lays the window's content out at the window's (harness-pinned) size. A WPF Window that is
    /// never shown runs no layout pass of its own, so the content is measured and arranged directly,
    /// as the probe's 1:1 and usability tests do.
    /// </summary>
    private static FrameworkElement LayOutContent(LagMarkWindow button)
    {
        var content = (FrameworkElement)button.Content;
        content.Measure(new Size(button.Width, button.Height));
        content.Arrange(new Rect(0, 0, button.Width, button.Height));
        content.UpdateLayout();
        return content;
    }

    [StaFact]
    public void TheHitArea_IsTheVisualGrownBy48DipPerSide()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        WithDpi(2.0, () =>
        {
            var button = new LagMarkWindow(dwellMs: 25);
            try
            {
                // The window's own layout, at its declared size, independent of this host's screen.
                OverlayWindowTestHarness.EnsureHandleAt(button, button.Width, button.Height);
                LayOutContent(button);

                var visual = VisualInWindow(button);
                Check.WithCustomMessage("the visual is 160 x 96 DIP inside the window")
                    .That(visual.Width).IsEqualTo(LagMarkWindow.VisualWidthDip);
                Check.That(visual.Height).IsEqualTo(LagMarkWindow.VisualHeightDip);
                Check.WithCustomMessage("the hit area grows the visual by 48 DIP on every side")
                    .That(visual.Left).IsEqualTo(LagMarkWindow.HitGrowthDip);
                Check.That(visual.Top).IsEqualTo(LagMarkWindow.HitGrowthDip);
                Check.That(button.Width - visual.Right).IsEqualTo(LagMarkWindow.HitGrowthDip);
                Check.That(button.Height - visual.Bottom).IsEqualTo(LagMarkWindow.HitGrowthDip);

                button.PlaceDwellTarget();
                // Outside the grown hit area is not a hit; the centre is.
                var centre = Centre(button.HitBounds);
                var dwell = (EyeCtrl.Core.Processing.DwellDetector)typeof(LagMarkWindow)
                    .GetField("_dwell", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(button)!;
                Check.That(dwell.HitTest(LagMarkWindow.TargetId, centre)).IsTrue();
                Check.That(dwell.HitTest(LagMarkWindow.TargetId,
                    new PhysicalPoint(centre.X, button.HitBounds.Y - 1))).IsFalse();
                Check.That(dwell.HitTest(LagMarkWindow.TargetId,
                    new PhysicalPoint(button.HitBounds.X - 1, centre.Y))).IsFalse();
            }
            finally
            {
                button.Close();
            }
        });
    }

    /// <summary>
    /// The harness is not vacuous: an over-size request (bigger than this host's screen) really
    /// reaches the window and its laid-out content. Without the hook the OS clamps the HWND and WPF
    /// writes the clamped size back — the SP-8 fixup 02 CI failure.
    /// </summary>
    [StaFact]
    public void TheHarness_DefeatsTheHostsTrackClamp()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var button = new LagMarkWindow(dwellMs: 700);
        try
        {
            OverlayWindowTestHarness.EnsureHandleAt(button, 4000, 3000);
            var content = LayOutContent(button);

            Check.WithCustomMessage("the OS clamped the window to this host's track size")
                .That(button.Width).IsEqualTo(4000);
            Check.That(button.Height).IsEqualTo(3000);
            Check.WithCustomMessage("the content laid out at the clamped size, not the requested one")
                .That(content.RenderSize.Width).IsEqualTo(4000);
            Check.That(content.RenderSize.Height).IsEqualTo(3000);
        }
        finally
        {
            button.Close();
        }
    }

    [StaFact]
    public void OneDwell_GivesOneMark_AtTheCompletingSamplesTimestamp_AndReArmsOnlyAfterLeaving()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        WithDpi(2.0, () =>
        {
            var messenger = new WeakReferenceMessenger();
            var cues = 0;
            messenger.Register<DwellActivationMessage>(this, (_, _) => cues++);
            var button = HeadlessButton(messenger);
            try
            {
                var marks = new List<long>();
                button.Marked += timestamp => marks.Add(timestamp);
                button.ShowButton();

                var centre = Centre(button.HitBounds);
                for (var i = 1; i <= 20; i++)
                    button.OnGaze(centre, i * 2_000);

                Check.That(marks).HasSize(1);
                Check.WithCustomMessage("the mark time must be the completing SAMPLE's timestamp")
                    .That(marks[0]).IsEqualTo(14 * 2_000);
                Check.That(cues).IsEqualTo(1);

                // Resting on the button after the mark must not mark again: the dwell re-arms only
                // once gaze has LEFT the hit area.
                for (var i = 21; i <= 80; i++)
                    button.OnGaze(centre, i * 2_000);
                Check.That(marks).HasSize(1);

                // Leaving the hit area re-arms; a fresh dwell then marks again.
                var outside = new PhysicalPoint(button.HitBounds.X - 10, centre.Y);
                button.OnGaze(outside, 81 * 2_000);
                for (var i = 82; i <= 101; i++)
                    button.OnGaze(centre, i * 2_000);
                Check.That(marks).HasSize(2);
                Check.That(marks[1]).IsEqualTo(95 * 2_000);
            }
            finally
            {
                button.Close();
            }
        });
    }

    [StaFact]
    public void TheLabel_ShowsMarkedForTheHold_ThenRevertsToTheRestingLabel()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        WithDpi(2.0, () =>
        {
            var button = HeadlessButton();
            try
            {
                button.ShowButton();
                Check.That(button.LabelText.Text).IsEqualTo(StringTable.EmbeddedEnglish.T("overlays.lagmark.label"));

                var centre = Centre(button.HitBounds);
                for (var i = 1; i <= 20; i++)
                    button.OnGaze(centre, i * 2_000);

                Check.That(button.LabelText.Text).IsEqualTo("Marked ✓");
                Check.That(LagMarkWindow.MarkedHoldMs).IsEqualTo(1000);

                button.RevertMarked();
                Check.That(button.LabelText.Text).IsEqualTo("Lag");
            }
            finally
            {
                button.Close();
            }
        });
    }

    [StaFact]
    public void TheLabels_AreTheShippedGerman_WhenTheTableIsActive()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        StringTable.Activate(ShippedGerman.Load());
        try
        {
            var button = HeadlessButton();
            try
            {
                button.ShowButton();
                Check.That(button.LabelText.Text).IsEqualTo("Hängt");

                var centre = Centre(button.HitBounds);
                for (var i = 1; i <= 20; i++)
                    button.OnGaze(centre, i * 2_000);
                Check.That(button.LabelText.Text).IsEqualTo("Markiert ✓");
            }
            finally
            {
                button.Close();
            }
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    public static TheoryData<string> Themes()
    {
        var data = new TheoryData<string>();
        foreach (var theme in ThemeRegistry.Names)
            data.Add(theme);
        return data;
    }

    /// <summary>
    /// The two labels, EN and the shipped DE, fit the 160 DIP visual in every theme, and the label's
    /// TextPrimary clears the 4.5:1 floor on the SurfaceLight fill (the probe's readable-button
    /// pairing). Contrast is computed from the theme's token colours by the WCAG formula, so a
    /// palette edit that regresses either pairing fails here.
    /// </summary>
    [StaTheory]
    [MemberData(nameof(Themes))]
    public void TheLabels_FitTheVisual_AndKeepContrast_InEveryTheme(string themeName)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var scope = ThemeScope.Activate(ThemeRegistry.Resolve(themeName));
        var button = HeadlessButton();
        try
        {
            button.ShowButton();
            var room = LagMarkWindow.VisualWidthDip - button.ButtonBorder.BorderThickness.Left
                                                      - button.ButtonBorder.BorderThickness.Right;
            var german = ShippedGerman.Load();
            foreach (var text in new[]
                     {
                         StringTable.EmbeddedEnglish.T("overlays.lagmark.label"),
                         StringTable.EmbeddedEnglish.T("overlays.lagmark.marked"),
                         german.T("overlays.lagmark.label"),
                         german.T("overlays.lagmark.marked"),
                     })
            {
                var natural = FitText.NaturalWidth(button.LabelText, text);
                Check.WithCustomMessage($"\"{text}\" needs {FitText.Dip(natural)} in {FitText.Dip(room)} ({themeName})")
                    .That(natural).IsLessOrEqualThan(room + 0.5);
            }

            var theme = ThemeRegistry.Resolve(themeName);
            var ratio = Contrast(theme.TextPrimary, theme.SurfaceLight);
            Check.WithCustomMessage($"TextPrimary on SurfaceLight is {ratio:0.00}:1 under {themeName}")
                .That(ratio).IsGreaterOrEqualThan(4.5);
        }
        finally
        {
            button.Close();
        }
    }

    [StaFact]
    public void TheButton_FollowsTheChrome_WhileItIsUp()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        IReadOnlyList<Rect> chrome = [];
        var calls = 0;
        var button = HeadlessButton(keepClear: () => { calls++; return chrome; });
        try
        {
            button.ShowButton();
            var resting = button.Top;
            var far = new PhysicalPoint(10, 10);

            // The keyboard opens under the button; the first sample after the show reads the chrome
            // and moves the button off it.
            chrome = [new Rect(300, button.Top, 1320, 420)];
            button.OnGaze(far, 1_000);
            var above = button.Top;
            Check.WithCustomMessage("the button did not move off the keyboard that opened under it")
                .That(above).IsStrictlyLessThan(resting);

            // The keyboard closes; a sample 100 ms later is throttled (the chrome is not even read),
            // and the first one a quarter second after the last read moves the button back.
            chrome = [];
            var before = calls;
            button.OnGaze(far, 1_000 + 100_000);
            Check.That(calls).IsEqualTo(before);
            Check.That(button.Top).IsEqualTo(above);

            button.OnGaze(far, 1_000 + LagMarkWindow.FollowCheckIntervalMicroseconds);
            Check.That(button.Top).IsEqualTo(resting);
        }
        finally
        {
            button.Close();
        }
    }

    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) =>
        (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
