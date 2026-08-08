namespace EyeCtrl.App.Tests.Fixtures;

using System;
using System.Linq;
using System.Windows;
using Xunit;

/// <summary>
/// Creates the process-wide <see cref="Application"/> with the token dictionary merged in, once.
/// Shared by <see cref="EyeCtrl.App.Tests.OverlayResourceResolutionTests"/> and
/// <see cref="EyeCtrl.App.Tests.CornerRadiusClampTests"/>, which both construct overlay windows
/// and therefore both need <c>Application.Current.Resources</c> to hold the merged
/// OverlayStyles.xaml dictionary the way App.xaml sets it up at runtime — hence one shared copy
/// of this setup rather than two.
///
/// The lock is what makes a second caller safe: <c>new Application()</c> throws if one already
/// exists. That alone is not enough, though — see <see cref="WpfApplicationCollection"/> for why
/// the two consumer classes must also be prevented from running concurrently.
/// </summary>
internal static class WpfApplicationGate
{
    private static readonly object ApplicationGate = new();

    internal static void EnsureApplicationWithTokens()
    {
        lock (ApplicationGate)
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

            // StandardBorderThickness needs no special handling: it is declared in
            // OverlayStyles.xaml via UniformThicknessExtension, so merging the dictionary above
            // is enough. It previously lived in App.OnStartup, which forced this method to
            // REPLICATE production wiring — meaning a change to App.OnStartup would leave this
            // test green.
        }
    }
}

/// <summary>
/// Serialises every test class that calls <see cref="WpfApplicationGate.EnsureApplicationWithTokens"/>
/// against the WPF <see cref="Application"/> singleton it creates. The gate's <c>lock</c> only
/// closes the race on *creating* the singleton; it does nothing about what happens after. Without
/// this collection, <c>Xunit.StaFact</c> spins a fresh STA thread per test, so a test class that
/// loses that race constructs its <see cref="Window"/>s on a thread whose
/// <see cref="System.Windows.Threading.Dispatcher"/> is not <c>Application.Current.Dispatcher</c>.
/// That is green today only because <see cref="Window"/>'s constructor gates its
/// <c>Application.Current</c> registration on a <c>CheckAccess()</c> and silently skips on
/// mismatch — undocumented tolerance, not a contract, and a poor foundation for tests whose whole
/// subject is load-time resource resolution.
///
/// <para>
/// That guarantee is INTRA-collection only. A class outside this collection that spins its own
/// STA thread — <c>ExternalDwellServiceTests</c> does — can still win the singleton-creation race
/// and leave <c>Application.Current.Dispatcher</c> bound to a thread that has already exited, which
/// is the same mismatch reached from outside. It is not a regression (<c>Xunit.StaFact</c>'s
/// per-test STA thread dies too) and WPF tolerates it for the same reason, but do not read this
/// collection as pinning the Application's thread. Closing that properly would need an
/// assembly-level fixture owning a long-lived STA thread.
/// </para>
///
/// <para>
/// What this does NOT cover: it does not serialise against <c>ThemeStartupTests</c>, which is
/// <c>[Collection("ActiveTheme")]</c> and mutates <c>Tokens.Current</c> concurrently with tests in
/// this collection. That residual is accepted rather than closed, because
/// <c>ThemeShapeTests.AllThreeThemes_DeclareIdenticalGeometryAndType</c> (in
/// EyeCtrl.Controls.Tests) pins <c>CornerRadiusCircle == 9999.0</c> for every registered theme, so
/// the value this collection's tests read cannot diverge across themes without that test failing
/// first. Say so here so nobody removes the pin thinking it is redundant with this collection.
/// </para>
/// </summary>
[CollectionDefinition("WpfApplication", DisableParallelization = true)]
public class WpfApplicationCollection
{
}
