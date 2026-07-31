namespace EyeCtrl.App.Tests;

using System.Windows;
using System.Windows.Media;
using EyeCtrl.App.Services;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// Startup theme selection, exercised without launching the app.
///
/// These mutate Tokens.Current, which is process-global, so they are serialised
/// into one collection and each restores Midnight afterwards. Every other theme
/// test reads an ITheme directly and is unaffected.
/// </summary>
[Collection("ActiveTheme")]
public class ThemeStartupTests
{
    [Fact]
    public void Apply_ActivatesTheNamedTheme()
    {
        try
        {
            var settings = new EyeCtrlSettings();
            settings.Theme.Name = "Contrast";
            var resources = new ResourceDictionary();

            ThemeStartup.Apply(settings, resources);

            Check.That(Tokens.Current.Name).IsEqualTo("Contrast");
            Check.That(Tokens.Accent).IsEqualTo(ContrastTheme.Instance.Accent);
            Check.That(Tokens.BorderThicknessStandard).IsEqualTo(2.0);
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }
    }

    [Fact]
    public void Apply_PublishesTheThemeToTheResourceDictionary()
    {
        try
        {
            var settings = new EyeCtrlSettings();
            settings.Theme.Name = "Ember";
            var resources = new ResourceDictionary();

            ThemeStartup.Apply(settings, resources);

            Check.That(((SolidColorBrush)resources["BrushAccent"]!).Color)
                 .IsEqualTo(EmberTheme.Instance.Accent);
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }
    }

    [Fact]
    public void Apply_FallsBackToMidnight_ForAnUnknownName()
    {
        try
        {
            var settings = new EyeCtrlSettings();
            settings.Theme.Name = "Sepia";   // e.g. hand-edited settings.json

            ThemeStartup.Apply(settings, new ResourceDictionary());

            Check.That(Tokens.Current.Name).IsEqualTo("Midnight");
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }
    }
}
