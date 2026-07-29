namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using EyeCtrl.App.Overlays;
using NFluent;
using Xunit;

/// <summary>
/// Instantiates each migrated overlay so its XAML is actually parsed. A misspelled
/// {StaticResource} key throws ResourceReferenceKeyNotFoundException at load time —
/// invisible to a build and to every literal-scanning test.
///
/// Requires STA plus an Application whose resources include the merged token
/// dictionary, mirroring what App.xaml sets up at runtime.
/// </summary>
public class OverlayResourceResolutionTests
{
    private static void EnsureApplicationWithTokens()
    {
        if (Application.Current is null)
        {
            // OnExplicitShutdown so this singleton Application never tears the test
            // host down when the overlay windows we construct are closed; otherwise
            // NoteManagerTests.CreateNote_ShowsWindow (which runs later in the same
            // AppDomain and calls Window.Show) reports the window as not visible.
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }

        const string uri =
            "pack://application:,,,/EyeCtrl.Controls;component/Theme/OverlayStyles.xaml";

        var already = Application.Current!.Resources.MergedDictionaries
            .Any(d => d.Source?.ToString() == uri);

        if (!already)
            Application.Current.Resources.MergedDictionaries.Add(
                new ResourceDictionary { Source = new Uri(uri, UriKind.Absolute) });
    }

    /// <summary>Overlays migrated by Plan A whose XAML uses StaticResource brushes.</summary>
    public static TheoryData<Type> MigratedOverlays() =>
    [
        typeof(KeyboardWindow), typeof(ToolbarWindow), typeof(QuickActionsGridWindow),
        typeof(SplashWindow), typeof(TrackStatusWindow), typeof(TaskSwitcherWindow),
        typeof(EyeCareBannerWindow), typeof(EyeCareExerciseWindow),
        typeof(CalibrationWindow), typeof(PauseIndicatorWindow),
        typeof(DriftNoticeWindow),
    ];

    [StaTheory]
    [MemberData(nameof(MigratedOverlays))]
    public void Overlay_LoadsWithoutUnresolvedResourceKeys(Type overlayType)
    {
        EnsureApplicationWithTokens();

        Window? w = null;
        try
        {
            // Activator runs InitializeComponent, which parses the XAML and
            // resolves every StaticResource. The 9 truly-parameterless windows
            // activate directly; KeyboardWindow and EyeCareBannerWindow take only
            // optional parameters, so their defaults are supplied via Type.Missing
            // (Activator's parameterless overload cannot reach optional params).
            var ctor = overlayType.GetConstructors()[0];
            var args = ctor.GetParameters()
                .Select(_ => Type.Missing)
                .ToArray();
            w = (Window)ctor.Invoke(BindingFlags.CreateInstance, null, args, null)!;
        }
        catch (Exception ex) when (ex.InnerException is ResourceReferenceKeyNotFoundException inner)
        {
            Check.WithCustomMessage(
                    $"{overlayType.Name} references an undefined resource key "
                    + $"'{inner.Key}'. Check the spelling against OverlayStyles.xaml.")
                .That(false).IsTrue();
        }
        finally
        {
            w?.Close();
        }
    }
}
