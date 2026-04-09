namespace EyeCtrl.App.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EyeCtrl.Core.Processing;

public class DwellToggle : DwellControl
{
    private static readonly Color OnColor = Color.FromArgb(200, 30, 100, 30);
    private static readonly Color OffColor = Color.FromArgb(200, 80, 40, 40);

    private readonly string _label;
    private readonly Func<bool> _get;
    private readonly Action<bool> _set;
    private readonly TextBlock _text;

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
            Foreground = Brushes.White,
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

        Root.MouseLeftButtonDown += (_, _) => Toggle();
        RefreshVisual();
    }

    protected override Border CreateRoot() => new();

    protected override void OnActivated() => Toggle();

    protected override void ApplyProgress(double progress)
    {
        var baseColor = _get() ? OnColor : OffColor;
        Root.Background = new SolidColorBrush(
            ColorBlend.Blend(baseColor, Colors.White, progress * 0.5));
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
        Root.Background = new SolidColorBrush(on ? OnColor : OffColor);
    }
}
