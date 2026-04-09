namespace EyeCtrl.App.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EyeCtrl.Core.Processing;

public class DwellButton : DwellControl
{
    private readonly Action _action;
    private readonly Color _baseColor;

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
        double height = 80)
        : base(registrar, id, category, radiusPhysical, dwellMs)
    {
        _action = action;
        _baseColor = baseColor;

        Root.Width = width;
        Root.Height = height;
        Root.Margin = new Thickness(4, 0, 4, 0);
        Root.CornerRadius = new CornerRadius(8);
        Root.Background = new SolidColorBrush(baseColor);
        Root.Cursor = Cursors.Hand;
        Root.Child = new TextBlock
        {
            Text = label,
            Foreground = Brushes.White,
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        Root.MouseLeftButtonDown += (_, _) => _action();
    }

    protected override Border CreateRoot() => new();

    protected override void OnActivated() => _action();

    protected override void ApplyProgress(double progress)
    {
        Root.Background = new SolidColorBrush(
            ColorBlend.Blend(_baseColor, Colors.White, progress * 0.5));
    }
}
