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
/// What this collection does NOT pin is the Application's thread, and the reason is sequencing
/// rather than concurrency. A class outside this collection that spins its own STA thread —
/// <c>ExternalDwellServiceTests</c> does — can still create the singleton FIRST and leave
/// <c>Application.Current.Dispatcher</c> bound to a thread that has since exited, which is the
/// same mismatch reached from outside. It cannot do so *concurrently*: that class sits in
/// "ExternalDwellPipe", which like this one is <c>DisableParallelization</c>, and xUnit v3 runs
/// such a collection exclusively (see <c>NonParallelCollectionPin</c>). An earlier version of
/// this paragraph called it a race, which overstated it. The exposure is real but ordinal, it is
/// not a regression (<c>Xunit.StaFact</c>'s per-test STA thread dies too), and WPF tolerates it
/// for the same reason. Closing it properly would need an assembly-level fixture owning a
/// long-lived STA thread.
/// </para>
///
/// <para>
/// <c>ThemeStartupTests</c>, which mutates <c>Tokens.Current</c>, is <c>[Collection("ActiveTheme")]</c>
/// — also <c>DisableParallelization</c>, so it does NOT run concurrently with this collection.
/// (This paragraph used to say it did; that followed from the same xUnit v2 misreading corrected
/// above.) What remains is ordinal: it could run first, and a theme it failed to restore would
/// still be the theme this collection's tests read. It restores Midnight in a <c>finally</c> on
/// every test, and <c>ThemeShapeTests.AllThreeThemes_DeclareIdenticalGeometryAndType</c> (in
/// EyeCtrl.Controls.Tests) pins <c>CornerRadiusCircle == 9999.0</c> for every registered theme, so
/// the value this collection's tests read cannot diverge across themes without that test failing
/// first. Say so here so nobody removes the pin thinking it is redundant with this collection.
/// </para>
/// </summary>
[CollectionDefinition("WpfApplication", DisableParallelization = true)]
public class WpfApplicationCollection
{
}
