namespace EyeCtrl.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Processing;

/// <summary>
/// An atomic dwell-enabled button that invokes an <see cref="Action"/> when the
/// user's gaze dwells on it for the configured duration.
/// </summary>
/// <remarks>
/// <see cref="DwellButton"/> is the building block used both standalone (attached
/// directly to a panel) and inside composite controls such as
/// <see cref="DwellSlider"/>, <see cref="DwellComboBox"/>, and
/// <see cref="DwellColorPicker"/>. Composites may override <see cref="DwellControl.Root"/>
/// properties (width, child content, etc.) after construction.
/// <para>
/// A mouse click fallback is wired to <see cref="DwellControl.Root"/> so the button
/// remains usable without eye-tracking.
/// </para>
/// <para>
/// Dwell visual feedback: <see cref="ApplyProgress"/> blends <see cref="Root"/>
/// background from <paramref name="baseColor"/> toward white as dwell accumulates.
/// </para>
/// </remarks>
public class DwellButton : DwellControl
{
    private readonly Action _action;
    private readonly Color _baseColor;
    // One mutable brush on Root.Background, mutated in place by ApplyProgress so dwell
    // updates at gaze rate never allocate a new brush per tick.
    private readonly SolidColorBrush _brush;

    /// <summary>
    /// Creates a <see cref="DwellButton"/>.
    /// </summary>
    /// <param name="registrar">The dwell registrar for this overlay.</param>
    /// <param name="id">Unique target identifier within <paramref name="category"/>.</param>
    /// <param name="label">Text displayed inside the button. May be empty for icon-only buttons.</param>
    /// <param name="action">Invoked when dwell completes or the button is clicked.</param>
    /// <param name="baseColor">Resting background colour; used as the start colour for dwell feedback.</param>
    /// <param name="category">Dwell category this button is registered under.</param>
    /// <param name="radiusPhysical">Hit-test radius in physical screen pixels.</param>
    /// <param name="dwellMs">Dwell duration in milliseconds.</param>
    /// <param name="width">Logical width of the button in WPF units. Pass 0 for auto-width.</param>
    /// <param name="height">Logical height of the button in WPF units.</param>
    /// <param name="textBrush">Label foreground. Defaults to white (suitable for dark surface fills);
    /// pass a dark brush for light semantic fills (AccentWarning/AccentDanger) so the label clears WCAG.</param>
    public DwellButton(
        IDwellRegistrar registrar,
        string id,
        string label,
        Action action,
        Color baseColor,
        DwellCategory category,
        double radiusPhysical,
        int dwellMs,
        double width = 100,
        double height = 80,
        Brush? textBrush = null)
        : base(registrar, id, category, radiusPhysical, dwellMs)
    {
        _action = action;
        _baseColor = baseColor;

        Root.Width = width;
        Root.Height = height;
        Root.Margin = new Thickness(4, 0, 4, 0);
        Root.CornerRadius = new CornerRadius(8);
        _brush = new SolidColorBrush(baseColor);
        Root.Background = _brush;
        Root.Cursor = Cursors.Hand;
        Root.Child = new TextBlock
        {
            Text = label,
            // Default white text suits the dark surface fills; light semantic fills
            // (AccentWarning/AccentDanger) need dark text to clear WCAG, so the caller
            // passes a textBrush for those.
            Foreground = textBrush ?? Tokens.BrushTextPrimary,
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        Root.MouseLeftButtonDown += (_, _) => _action();
    }

    /// <inheritdoc/>
    protected override Border CreateRoot() => new();

    /// <inheritdoc/>
    protected override void OnActivated() => _action();

    /// <inheritdoc/>
    protected override void ApplyProgress(double progress)
    {
        _brush.Color = ColorBlend.Blend(_baseColor, Tokens.Accent, progress * 0.5);
    }
}
