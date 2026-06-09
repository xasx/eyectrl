namespace EyeCtrl.Sdk.WinUI.Sample;

using Microsoft.UI.Xaml;
using EyeCtrl.Sdk.WinUI;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        EyeCtrlDwell.Initialize(this);
    }

    private void OnSave(object sender, RoutedEventArgs e) => Log.Text = $"Saved at {DateTime.Now:T}";
    private void OnPlain(object sender, RoutedEventArgs e) => Log.Text = "Plain click";
}
