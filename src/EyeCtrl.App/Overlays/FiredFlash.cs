namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using EyeCtrl.Controls.Theme;

/// <summary>
/// The "fired" flash (Wave C ticket C1): one brief luminance step on a ring, confirming that a
/// dwell fired. Drives a dedicated overlay ellipse that is declared LAST (so it paints over the
/// ring's own bands) and is collapsed at rest. A flash shows it in the lighter of the two
/// achromatic tokens for <see cref="DurationMs"/>, then collapses it again.
/// <para>
/// Nothing the ring's rest look is made of is ever written here: not the bands' stroke, thickness
/// or opacity (pinned by CursorRingStacksTests/CursorRingRenderTests), and never Window.Opacity,
/// which carries the quiet/pinned look and must stay exactly 0.6 or 1.0. Returning to rest is
/// therefore one Visibility write, and cannot leave a band half-restored.
/// </para>
/// <para>
/// A hard step rather than a fade: a fade spends most of its 120 ms near the rest look, which is
/// exactly the part of the flash that is hard to see. The end runs on a UI-thread
/// <see cref="DispatcherTimer"/>, never on the gaze thread. <see cref="End"/> is the test seam too:
/// a pumpless STA test never ticks a DispatcherTimer (the ToolbarActivationFlashTests reason).
/// </para>
/// </summary>
internal sealed class FiredFlash
{
    /// <summary>
    /// How long the step is lit. The ticket's bound is 150 ms for the whole flash; 120 leaves room
    /// for the DispatcherTimer's tick granularity inside it.
    /// </summary>
    internal const int DurationMs = 120;

    private readonly Shape _band;
    private readonly DispatcherTimer _timer;

    /// <param name="band">The overlay ellipse. Its geometry (size, thickness) stays the owner's job;
    /// this class writes only its stroke brush and its visibility.</param>
    internal FiredFlash(Shape band)
    {
        _band = band;
        _timer = new DispatcherTimer(DispatcherPriority.Normal, band.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(DurationMs),
        };
        _timer.Tick += (_, _) => End();
        _band.Visibility = Visibility.Collapsed;
    }

    /// <summary>True while the step is showing.</summary>
    internal bool IsLit => _band.Visibility == Visibility.Visible;

    /// <summary>True while the end is scheduled; false at rest.</summary>
    internal bool IsEndScheduled => _timer.IsEnabled;

    /// <summary>The scheduled length of the step.</summary>
    internal TimeSpan Interval => _timer.Interval;

    /// <summary>
    /// Lights the step and (re)schedules its end. A second fire during a live step restarts the
    /// timer, so the flash never outlasts <see cref="DurationMs"/> from the latest fire and never
    /// stacks into a longer one.
    /// </summary>
    internal void Start()
    {
        // Read per flash, not cached: the theme can change while the window lives. These are the
        // theme's cached brushes, so a flash allocates nothing.
        _band.Stroke = LightBrush();
        _band.Visibility = Visibility.Visible;
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Returns to the rest look at once. Idempotent.</summary>
    internal void End()
    {
        _timer.Stop();
        _band.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// The lighter of the two achromatic tokens the rings are drawn from: TextPrimary in Midnight,
    /// Ember and Contrast, StrokeHalo (white) in Daylight. Lighter, so the step is a brightening in
    /// every theme: the ring's dark band takes the light band's colour and the ring reads as one
    /// light band for the length of the step.
    /// </summary>
    internal static SolidColorBrush LightBrush() =>
        RelativeLuminance(Tokens.TextPrimary) >= RelativeLuminance(Tokens.StrokeHalo)
            ? Tokens.BrushTextPrimary
            : Tokens.BrushStrokeHalo;

    /// <summary>WCAG relative luminance of an sRGB colour.</summary>
    internal static double RelativeLuminance(Color c) =>
        0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    private static double Linear(byte channel)
    {
        var v = channel / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }
}
