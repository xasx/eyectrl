namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The restart notice window (holistic review F5), headless: it is never shown (a shown overlay is
/// the user's only input surface), so the geometry is driven through its internal seams and the
/// dwell through its public <c>OnGaze</c>, as <see cref="EyeCareBannerCueTests"/> does for the banner.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class RestartNoticeWindowTests
{
    private static readonly Rect WorkArea = new(0, 0, 1920, 1080);

    private sealed class CueProbe
    {
        public int Count { get; private set; }
        public bool? ClosedAtCue { get; private set; }
        public Func<bool>? ReadClosed { get; init; }

        public void Attach(IMessenger messenger) =>
            messenger.Register<DwellActivationMessage>(this, (_, _) =>
            {
                Count++;
                ClosedAtCue = ReadClosed?.Invoke();
            });
    }

    /// <summary>Lays the never-shown window out at its prepared size, as a shown one would be.</summary>
    private static void LayOut(RestartNoticeWindow window)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        window.UpdateLayout();
    }

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

    [StaFact]
    public void TheWindow_IsAnOverlay_ThatNeverTakesTheForeground_AndIsClickThrough()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new RestartNoticeWindow();

        Check.That(window is OverlayWindow).IsTrue();
        Check.That(window.IsClickThrough).IsTrue();          // WS_EX_TRANSPARENT: input passes through
        Check.That(window.ExcludeFromScreenCapture).IsTrue();
        Check.That(window.Topmost).IsTrue();
        Check.That(window.ShowInTaskbar).IsFalse();
        // WS_EX_NOACTIVATE is applied by OverlayWindow for every subclass (focusable: false at
        // OnSourceInitialized); pin that this window does not override the interaction itself.
        Check.That(typeof(RestartNoticeWindow).GetMethod(
            "OnSourceInitialized", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)).IsNull();
        window.Close();
    }

    [StaFact]
    public void ThePreparedWindow_SitsAtTheBottomCentre_AndAGrownTextRisesFromAFixedBottomEdge()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new RestartNoticeWindow();
        try
        {
            window.SetText("Short.");
            window.Prepare(WorkArea);
            var shortTop = window.Top;
            var shortBottom = window.Top + window.Height;

            Check.That(window.Left).IsEqualTo((1920 - window.Width) / 2);
            Check.That(shortBottom).IsEqualTo(1080 - RestartNoticePlacement.MarginDip);

            window.SetText("Titel\n" + string.Join(" ", Enumerable.Repeat("Ein langer deutscher Satz über den Neustart.", 12)));
            window.Prepare(WorkArea);

            Check.WithCustomMessage("a longer text must grow the window, not clip it").That(window.Height)
                .IsStrictlyGreaterThan(window.MinHeight);
            Check.That(window.Top + window.Height).IsEqualTo(shortBottom);
            Check.That(window.Top).IsStrictlyLessThan(shortTop);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// "Text too small" (the user, on the first sheet): sizes come from the theme's tokens, the first
    /// line is a title, the button label is at least body-sized, and the card is wide enough that a
    /// longer German text wraps instead of clipping.
    /// </summary>
    [StaFact]
    public void TheText_IsTitleAndBody_AtTheThemeTokens_AndTheButtonLabelIsAtLeastBody()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new RestartNoticeWindow();
        try
        {
            window.SetText("Titel\nKörper eins\nKörper zwei");
            Check.That(window.TitleText.Text).IsEqualTo("Titel");
            Check.That(window.BodyText.Text).IsEqualTo("Körper eins\nKörper zwei");
            Check.That(window.BodyText.Visibility).IsEqualTo(Visibility.Visible);

            // A message with no line break (a refusal reason) is all title.
            window.SetText("Neustart fehlgeschlagen.");
            Check.That(window.TitleText.Text).IsEqualTo("Neustart fehlgeschlagen.");
            Check.That(window.BodyText.Visibility).IsEqualTo(Visibility.Collapsed);

            Check.That(window.TitleText.FontSize).IsEqualTo(EyeCtrl.Controls.Theme.Tokens.FontSizeTitle);
            Check.That(window.TitleText.FontWeight).IsEqualTo(EyeCtrl.Controls.Theme.Tokens.FontWeightMedium);
            Check.That(window.BodyText.FontSize).IsEqualTo(EyeCtrl.Controls.Theme.Tokens.FontSizeSubtitle);
            Check.That(window.CloseLabel.FontSize).IsStrictlyGreaterThan(window.BodyText.FontSize - 1e-9);
            Check.WithCustomMessage("the card is 880-960 DIP wide").That(window.Width >= 880 && window.Width <= 960).IsTrue();
            Check.WithCustomMessage("the button is at least the old 170 x 64").That(
                window.CloseButton.Width >= 170 && window.CloseButton.Height >= 64).IsTrue();
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void TheCloseBounds_AreTheButtonsOwnRectangle_InPhysicalPixels()
    {
        var bounds = RestartNoticeWindow.CloseBounds(
            window: new Rect(600, 700, 720, 240),
            buttonInWindow: new Rect(275, 150, 170, 64),
            scaleX: 2.0, scaleY: 2.0);

        Check.That(bounds.X).IsEqualTo((600 + 275) * 2.0);
        Check.That(bounds.Y).IsEqualTo((700 + 150) * 2.0);
        Check.That(bounds.Width).IsEqualTo(340.0);
        Check.That(bounds.Height).IsEqualTo(128.0);
    }

    [StaFact]
    public void TheCloseButton_IsInsideTheWindow_AndGazeOnItClosesTheNoticeThenCues()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        WithDpi(2.0, () =>
        {
            var messenger = new WeakReferenceMessenger();
            var window = new RestartNoticeWindow(dwellMs: 25, messenger: messenger);
            try
            {
                window.SetText("EyeCtrl wird neu gestartet.\nDas neue EyeCtrl startet gerade.");
                window.CloseLabel.Text = "✕ Ausblenden";
                window.Prepare(WorkArea);
                LayOut(window);
                window.PlaceDwellTarget();

                // Where the button is on screen, in physical pixels, from the real layout.
                var button = window.CloseButton.TransformToAncestor((System.Windows.Media.Visual)window.Content)
                    .TransformBounds(new Rect(window.CloseButton.RenderSize));
                Check.WithCustomMessage("the close button must lie inside the window").That(
                    button.Left >= 0 && button.Top >= 0
                    && button.Right <= window.Width && button.Bottom <= window.Height).IsTrue();
                var center = new PhysicalPoint(
                    (window.Left + button.Left + button.Width / 2) * 2.0,
                    (window.Top + button.Top + button.Height / 2) * 2.0);

                var dwell = (DwellDetector)typeof(RestartNoticeWindow)
                    .GetField("_dwell", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                Check.That(dwell.HitTest(RestartNoticeWindow.CloseTargetId, center)).IsTrue();
                // A point just outside the drawn button is not a hit (a rectangle, no halo).
                Check.That(dwell.HitTest(RestartNoticeWindow.CloseTargetId,
                    new PhysicalPoint(center.X + button.Width + 4, center.Y))).IsFalse();

                var probe = new CueProbe
                {
                    // The effect: the close target is gone by the time the cue publishes.
                    ReadClosed = () => !dwell.HitTest(RestartNoticeWindow.CloseTargetId, center),
                };
                probe.Attach(messenger);

                for (var i = 1; i <= 20; i++)
                    window.OnGaze(center, i * 2_000);

                Check.That(probe.Count).IsEqualTo(1);
                Check.WithCustomMessage("the cue must publish AFTER the notice is down (spec D8)")
                    .That(probe.ClosedAtCue).IsEqualTo(true);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [StaFact]
    public void GazeAwayFromTheButton_NeverClosesIt()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        WithDpi(2.0, () =>
        {
            var messenger = new WeakReferenceMessenger();
            var window = new RestartNoticeWindow(dwellMs: 25, messenger: messenger);
            try
            {
                window.Prepare(WorkArea);
                LayOut(window);
                window.PlaceDwellTarget();
                var probe = new CueProbe();
                probe.Attach(messenger);

                for (var i = 1; i <= 40; i++)
                    window.OnGaze(new PhysicalPoint(10, 10), i * 2_000);

                Check.That(probe.Count).IsEqualTo(0);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
