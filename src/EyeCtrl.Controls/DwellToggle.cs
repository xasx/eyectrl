namespace EyeCtrl.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Processing;

/// <summary>
/// A dwell-enabled boolean toggle control.
/// </summary>
/// <remarks>
/// Displays the current state using a checkbox symbol (☑/☐) prepended to the label
/// text and colours the background green (on) or dark-red (off).
/// Dwell feedback sweeps a progress bar along the bottom edge; the state colour holds still,
/// so the label's contrast on it does not decay as the dwell completes.
/// A mouse click fallback toggles the value without requiring eye-tracking.
/// </remarks>
public class DwellToggle : DwellControl
{
    private static Color OnColor => Tokens.ToggleOn;
    private static Color OffColor => Tokens.ToggleOff;

    private readonly string _label;
    private readonly Func<bool> _get;
    private readonly Action<bool> _set;
    private readonly TextBlock _text;
    // One mutable brush on Root.Background, mutated in place by RefreshVisual and
    // ApplyProgress so dwell updates at gaze rate never allocate a new brush per tick.
    private readonly SolidColorBrush _brush = new();

    // Mutated in place by ApplyProgress at gaze rate: no new brush/transform/geometry is
    // allocated per tick, mirroring DwellButton's bar.
    private readonly Rectangle _progressBar;
    private readonly ScaleTransform _progressScale;

    /// <summary>The checkbox-and-label text block. Root's child is this control's layout container.</summary>
    public TextBlock Label => _text;

    /// <summary>
    /// The dwell progress bar along the toggle's bottom edge. Mirrors
    /// <see cref="DwellButton.ProgressBar"/>: the state fill stays put through the dwell, so the
    /// label's contrast on it does not decay.
    /// </summary>
    public Rectangle ProgressBar => _progressBar;

    /// <summary>
    /// Creates a <see cref="DwellToggle"/>.
    /// </summary>
    /// <param name="registrar">The dwell registrar for this overlay.</param>
    /// <param name="id">Unique target identifier within <paramref name="category"/>.</param>
    /// <param name="label">Descriptive text shown next to the checkbox symbol.</param>
    /// <param name="get">Returns the current boolean value of the setting.</param>
    /// <param name="set">Applies a new boolean value when the toggle activates.</param>
    /// <param name="category">Dwell category this toggle is registered under.</param>
    /// <param name="radiusPhysical">Hit-test radius in physical screen pixels.</param>
    /// <param name="dwellMs">Dwell duration in milliseconds.</param>
    public DwellToggle(
        IDwellRegistrar registrar,
        string id,
        string label,
        Func<bool> get,
        Action<bool> set,
        DwellCategory category,
        double radiusPhysical,
        int dwellMs)
        : base(registrar, id, category, radiusPhysical, dwellMs)
    {
        _label = label;
        _get = get;
        _set = set;

        _text = new TextBlock
        {
            FontSize = Tokens.FontSizeTitle,
            FontWeight = Tokens.FontWeightMedium,
            Foreground = Tokens.BrushTextPrimary,
            VerticalAlignment = VerticalAlignment.Center
        };

        System.Windows.Automation.AutomationProperties.SetAutomationId(_text, $"Settings_Toggle_{id}");

        Root.Height = 100;
        Root.Margin = new Thickness(0, 0, 0, 24);
        Root.Padding = new Thickness(20, 0, 20, 0);
        Root.CornerRadius = Tokens.StandardCornerRadius;
        Root.HorizontalAlignment = HorizontalAlignment.Left;
        Root.Cursor = Cursors.Hand;
        Root.Background = _brush;

        _progressScale = new ScaleTransform(0, 1);
        _progressBar = new Rectangle
        {
            Height = DwellButton.ProgressBarHeight,
            Fill = Tokens.BrushAccent,
            // Halo outline, matching DwellButton's fix for the same problem: a flat accent
            // fill is invisible on an accent-adjacent background. In the Contrast theme,
            // ToggleOn is #00FF00 and Accent is #00FFFF — bar-fill-vs-toggle-fill is ~1.09:1,
            // i.e. imperceptible, in precisely the theme built for low vision. StrokeHalo there
            // is #000000, which clears ~15:1 against #00FF00, so the halo is what makes the bar
            // perceptible at all on that theme.
            Stroke = Tokens.BrushStrokeHalo,
            StrokeThickness = 1,
            // Stretch + a ScaleX transform, not a Width: Width would need ActualWidth, which is
            // 0 until the first layout pass. Scaling is layout-independent and costs one double
            // per tick. Pinned by ApplyProgress_SweepsFromTheLeftEdge — HorizontalAlignment.Left
            // here collapses the bar to a fixed 1 DIP dot that never moves, with every other
            // test in this file staying green.
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            RenderTransform = _progressScale,
            // Y is irrelevant here: ScaleY is fixed at 1, so the origin's Y component never
            // participates in any transform this control applies. X = 0 is what matters —
            // it is why the bar grows from the left edge instead of from its centre.
            RenderTransformOrigin = new Point(0, 0),
            IsHitTestVisible = false
        };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_text, 0);
        Grid.SetRow(_progressBar, 1);
        layout.Children.Add(_text);
        layout.Children.Add(_progressBar);
        Root.Child = layout;

        Root.MouseLeftButtonDown += (_, _) => Toggle();
        RefreshVisual();
    }

    /// <inheritdoc/>
    protected override Border CreateRoot() => new();

    /// <inheritdoc/>
    protected override void OnActivated() => Toggle();

    /// <inheritdoc/>
    protected override void ApplyProgress(double progress)
    {
        _progressScale.ScaleX = Math.Clamp(progress, 0, 1);
    }

    private void Toggle()
    {
        _set(!_get());
        RefreshVisual();
    }

    private void RefreshVisual()
    {
        var on = _get();
        _text.Text = (on ? "\u2611 " : "\u2610 ") + _label;
        _brush.Color = on ? OnColor : OffColor;
    }
}
