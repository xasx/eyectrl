namespace EyeCtrl.App.Tests;

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NFluent;
using Xunit;

/// <summary>
/// App.xaml must merge the Controls theme dictionary, otherwise every
/// {StaticResource BrushXxx} in the overlay XAML throws at window load — a
/// runtime failure no compile step catches.
/// </summary>
public class ThemeResourcesTests
{
    private static string AppXaml()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(
            typeof(ThemeResourcesTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent;
        Check.That(dir).IsNotNull();
        return File.ReadAllText(Path.Combine(dir!.FullName, "src/EyeCtrl.App/App.xaml"));
    }

    [Fact]
    public void AppXaml_MergesTheOverlayStylesDictionary()
    {
        const string expected =
            "pack://application:,,,/EyeCtrl.Controls;component/Theme/OverlayStyles.xaml";

        Check.WithCustomMessage(
                "src/EyeCtrl.App/App.xaml must merge " + expected
                + " inside <Application.Resources>, or all StaticResource brush "
                + "lookups fail at runtime.")
            .That(AppXaml()).Contains(expected);
    }
}
