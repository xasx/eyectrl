namespace EyeCtrl.App.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

public class DwellLabel
{
    public TextBlock Root { get; }

    public DwellLabel(string text)
    {
        Root = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
            FontSize = 22,
            Margin = new Thickness(0, 8, 0, 2)
        };
    }

    public void Attach(Panel parent) => parent.Children.Add(Root);
    public void Detach(Panel parent) => parent.Children.Remove(Root);
}
