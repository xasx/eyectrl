namespace EyeCtrl.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Processing;

/// <summary>
/// A dwell-enabled boolean toggle control.
/// </summary>
/// <remarks>
/// Displays the current state using a checkbox symbol (☑/☐) prepended to the label
/// text and colours the background green (on) or dark-red (off).
/// Dwell feedback blends the current state colour toward white as dwell accumulates.
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
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = Tokens.BrushTextPrimary,
            VerticalAlignment = VerticalAlignment.Center
        };

        System.Windows.Automation.AutomationProperties.SetAutomationId(_text, $"Settings_Toggle_{id}");

        Root.Height = 80;
        Root.Margin = new Thickness(0, 4, 0, 12);
        Root.Padding = new Thickness(20, 0, 20, 0);
        Root.CornerRadius = new CornerRadius(8);
        Root.HorizontalAlignment = HorizontalAlignment.Left;
        Root.Cursor = Cursors.Hand;
        Root.Child = _text;
        Root.Background = _brush;

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
        var baseColor = _get() ? OnColor : OffColor;
        _brush.Color = ColorBlend.Blend(baseColor, Tokens.Accent, progress * 0.5);
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
