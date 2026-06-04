namespace EyeCtrl.Sdk.Wpf.Sample;

using System.Windows;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Log.Text = $"Save clicked at {DateTime.Now:HH:mm:ss}";
    }

    private void OnPlain(object sender, RoutedEventArgs e)
    {
        Log.Text = $"Plain clicked at {DateTime.Now:HH:mm:ss}";
    }
}
