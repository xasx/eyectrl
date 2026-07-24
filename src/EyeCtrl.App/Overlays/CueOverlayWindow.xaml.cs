namespace EyeCtrl.App.Overlays;

using System;
using System.Windows;
using System.Windows.Media.Animation;
using EyeCtrl.App.Services;

/// <summary>
/// The adaptation cue overlay (design §10): a subtle top-right corner badge whose peak opacity scales
/// with struggle intensity and which auto-fades over ~2 s. It is click-through and
/// <see cref="OverlayWindow.ExcludeFromScreenCapture"/>=true, so it is never baked into the magnifier /
/// action-menu <c>CopyFromScreen</c> grabs and never intercepts gaze/mouse (non-interactive).
/// </summary>
public partial class CueOverlayWindow : OverlayWindow, ICuePresenter
{
    private const double MarginDip = 24;         // gap from the work-area corner
    private const double MinPeakOpacity = 0.25;  // faintly visible even at low intensity ("subtle")
    private static readonly Duration FadeDuration = new(TimeSpan.FromSeconds(2));

    // Bumped on each Show so a superseded fade's Completed handler is ignored (WPF may or may not raise
    // Completed on a replaced animation; the generation check makes the hide fire only for the latest).
    private int _fadeGeneration;
    private bool _positioned;

    /// <summary>Creates the cue window (click-through + excluded from screen capture, set before init).</summary>
    public CueOverlayWindow()
    {
        IsClickThrough = true;
        ExcludeFromScreenCapture = true; // keep the cue out of the magnifier/action-menu preview capture (§10)
        InitializeComponent();
    }

    /// <inheritdoc />
    public void Show(double intensity)
    {
        EnsurePositioned();
        if (!IsVisible)
            base.Show();
        RaiseToTop();

        double peak = MinPeakOpacity + (1.0 - MinPeakOpacity) * Math.Clamp(intensity, 0.0, 1.0);
        int generation = ++_fadeGeneration;
        var fade = new DoubleAnimation(peak, 0.0, FadeDuration) { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) =>
        {
            // Only the latest fade completing (struggle stopped) hides the window; a re-flash bumped the
            // generation, so its predecessor's Completed is a no-op and the badge stays up while struggling.
            if (generation == _fadeGeneration)
                Hide();
        };
        Badge.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void EnsurePositioned()
    {
        if (_positioned)
            return;
        var area = SystemParameters.WorkArea; // DIPs, matching Window.Left/Top/Width
        Left = area.Right - Width - MarginDip;
        Top = area.Top + MarginDip;
        _positioned = true;
    }
}
