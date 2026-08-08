namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using EyeCtrl.App.Overlays;
using NFluent;
using Xunit;

/// <summary>
/// Instantiates each migrated overlay so its XAML is actually parsed. A misspelled
/// {StaticResource} key throws ResourceReferenceKeyNotFoundException at load time —
/// invisible to a build and to every literal-scanning test. A key that exists but holds
/// the WRONG TYPE for the property it is written on throws a XamlParseException instead;
/// both are reported here by name.
///
/// Requires STA plus an Application whose resources include the merged token
/// dictionary, mirroring what App.xaml sets up at runtime.
/// </summary>
public class OverlayResourceResolutionTests
{
    private static readonly object ApplicationGate = new();

    /// <summary>
    /// Creates the process-wide <see cref="Application"/> with the token dictionary merged in,
    /// once. Also used by <see cref="CornerRadiusClampTests"/>, which constructs
    /// QuickActionsGridWindow for the same reason — hence internal rather than private, so
    /// there is exactly one copy of this setup. The lock is what makes a second caller safe:
    /// <c>new Application()</c> throws if one already exists, and xUnit may run the two classes
    /// in parallel because neither is in a serialising collection.
    /// </summary>
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

    /// <summary>
    /// Overlays whose XAML references {StaticResource} token brushes. Covers the Plan A
    /// filled overlays plus the Plan B haloed overlays (which each gained 2-6 brush keys).
    /// SettingsWindow is excluded: its constructor requires 7 non-optional dependencies
    /// (ViewModel/Settings/Services/Messenger/CalibrationService), so it cannot be
    /// constructed headlessly. It is not left unguarded, though —
    /// <see cref="SettingsWindowXaml_ResolvesEveryStaticResource"/> parses its XAML directly.
    /// NoteWindow is excluded: it is a code-only overlay with no .xaml file.
    ///
    /// TargetHighlightWindow was silently absent with no stated reason, which mattered because
    /// it is one of only two Task 8 files this suite did not cover (SettingsWindow above being
    /// the other): a type-wrong {StaticResource} written into it during Task 8 would pass every
    /// headless test and first throw when the user opened the window. It has a parameterless
    /// constructor, so it is now listed. SettingsWindow is still not constructible, but it is
    /// no longer a coverage gap: its XAML is parsed by
    /// <see cref="SettingsWindowXaml_ResolvesEveryStaticResource"/>.
    /// </summary>
    public static TheoryData<Type> MigratedOverlays() =>
    [
        typeof(KeyboardWindow), typeof(ToolbarWindow), typeof(QuickActionsGridWindow),
        typeof(SplashWindow), typeof(TrackStatusWindow), typeof(TaskSwitcherWindow),
        typeof(EyeCareBannerWindow), typeof(EyeCareExerciseWindow),
        typeof(CalibrationWindow), typeof(PauseIndicatorWindow),
        typeof(DriftNoticeWindow), typeof(TargetHighlightWindow),
        // Plan B haloed overlays
        typeof(CursorCircleWindow), typeof(ActivationButtonWindow),
        typeof(ManualSelectionGazeIndicatorWindow), typeof(ScrollOverlayWindow),
        typeof(CueOverlayWindow), typeof(DwellFeedbackOverlay),
        // Drag trail: migrated onto tokens + reads DragSettings.TrailColor via Configure
        typeof(DragTrailOverlay),
        // Magnifier: its rim + halo are marks over the captured image (§4.1.3)
        typeof(ManualSelectionOverlayWindow),
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
        catch (Exception ex) when (IsResourceFault(ex))
        {
            Check.WithCustomMessage(DescribeResourceFault(overlayType.Name, XamlOf(overlayType), ex))
                .That(false).IsTrue();
        }
        finally
        {
            w?.Close();
        }
    }

    /// <summary>
    /// The one Task 8 file <see cref="MigratedOverlays"/> cannot reach. SettingsWindow's
    /// constructor takes 7 non-optional dependencies, so instead of constructing it this loads
    /// its XAML directly — with x:Class stripped, since that attribute makes XamlReader demand
    /// the compiled code-behind type. Everything else about the parse is real: every
    /// {StaticResource} is looked up in the same merged token dictionary the app uses, and every
    /// resolved value is assigned to the same property, so a misspelled key AND a
    /// type-mismatched one both throw here.
    ///
    /// Without this, a wrong key in Views/SettingsWindow.xaml passes every headless test and
    /// first surfaces as a XamlParseException when the user opens the settings window — a user
    /// whose only input channel is gaze and who therefore has no way to recover.
    ///
    /// The window is never shown: XamlReader builds the object tree and nothing more.
    /// </summary>
    [StaFact]
    public void SettingsWindowXaml_ResolvesEveryStaticResource()
    {
        EnsureApplicationWithTokens();

        var path = Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Views", "SettingsWindow.xaml");
        Check.WithCustomMessage($"SettingsWindow.xaml not found at {path}.")
            .That(File.Exists(path)).IsTrue();

        var xaml = File.ReadAllText(path);
        var loadable = Regex.Replace(xaml, @"\s+x:Class=""[^""]*""", string.Empty);
        // A clr-namespace with no assembly= resolves against the assembly that is loading the
        // XAML — this test assembly, not EyeCtrl.App — so the root element's namespace is made
        // explicit. Compiled XAML needs no such qualification, which is why the file omits it.
        loadable = loadable.Replace(
            "clr-namespace:EyeCtrl.App.Overlays\"",
            "clr-namespace:EyeCtrl.App.Overlays;assembly=EyeCtrl.App\"",
            StringComparison.Ordinal);

        Window? w = null;
        try
        {
            w = (Window)XamlReader.Parse(loadable);
        }
        catch (Exception ex) when (IsResourceFault(ex))
        {
            Check.WithCustomMessage(DescribeResourceFault("SettingsWindow.xaml", xaml, ex))
                .That(false).IsTrue();
        }
        finally
        {
            w?.Close();
        }
    }

    /// <summary>Walks an exception and its InnerException chain.</summary>
    /// <param name="ex">The outermost exception.</param>
    /// <returns>The exception followed by each inner exception.</returns>
    private static IEnumerable<Exception> Chain(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            yield return e;
    }

    /// <summary>
    /// True for the two ways a {StaticResource} can break a XAML load. A misspelled key
    /// surfaces as <see cref="ResourceReferenceKeyNotFoundException"/>; a key whose value is of
    /// the wrong type for the property surfaces only as a <see cref="XamlParseException"/>,
    /// because WPF runs no type converter on a StaticResource assignment. The original filter
    /// matched the first case only, so a type mismatch escaped uncaught with a message that
    /// named neither the overlay nor the key. Anything else still propagates untouched.
    /// </summary>
    /// <param name="ex">The exception thrown while loading the XAML.</param>
    /// <returns>True if the load failed in a way this test can explain.</returns>
    private static bool IsResourceFault(Exception ex) =>
        Chain(ex).Any(e => e is ResourceReferenceKeyNotFoundException or XamlParseException);

    /// <summary>
    /// Builds the failure message: names the overlay, and names the key — the missing one when
    /// the key does not exist, otherwise every key in the file whose resolved value cannot be
    /// assigned to the property it is written on.
    ///
    /// The key is recovered by re-resolving the file's references against the live dictionary
    /// rather than mined out of the exception, because the exception names the *property* and
    /// not the resource, and its text is localised (this machine reports German). The typed
    /// exception's Key is still preferred where WPF supplies one: compiled BAML raises
    /// <see cref="ResourceReferenceKeyNotFoundException"/> for a missing key, while the loose
    /// XAML path used for SettingsWindow reports the same fault as a bare
    /// <see cref="XamlParseException"/> — hence the scan is the fallback for both faults, not
    /// just for type mismatches.
    /// </summary>
    /// <param name="label">Overlay type name, or file name for a directly parsed file.</param>
    /// <param name="xaml">The overlay's XAML, used to name the offending key.</param>
    /// <param name="ex">The exception thrown while loading the XAML.</param>
    /// <returns>The message to report.</returns>
    private static string DescribeResourceFault(string label, string xaml, Exception ex)
    {
        var thrownKey = Chain(ex).OfType<ResourceReferenceKeyNotFoundException>()
            .FirstOrDefault()?.Key;
        List<string> missing = thrownKey is not null
            ? [$"'{thrownKey}'"]
            : UnresolvedResourceKeys(xaml).Select(k => $"'{k}'").ToList();

        if (missing.Count > 0)
            return $"{label} references undefined resource key(s) {string.Join(", ", missing)}. "
                + "Check the spelling against OverlayStyles.xaml.";

        var mismatched = TypeMismatchedResourceKeys(xaml);
        var named = mismatched.Count > 0
            ? string.Join("; ", mismatched)
            : "(could not pin the key by scanning the file — see the underlying error)";

        return $"{label} assigns a resource of the WRONG TYPE. Every key it names exists, so "
            + "this is not a spelling mistake — WPF runs no type converter on a StaticResource "
            + "assignment, so the value must already be the property's type. Offending: "
            + $"{named}. Underlying error: {ex.GetType().Name}: {ex.Message}";
    }

    /// <summary>Matches a <c>Property="{StaticResource Key}"</c> attribute.</summary>
    private static readonly Regex ResourceAttribute = new(
        @"(?<prop>[\w.]+)\s*=\s*""\{StaticResource\s+(?<key>\w+)\s*\}""", RegexOptions.Compiled);

    /// <summary>
    /// Keys the XAML references that the live application resources do not define. Comments are
    /// stripped first so a commented-out example cannot be reported.
    /// </summary>
    /// <param name="xaml">The XAML text to scan.</param>
    /// <returns>The undefined keys, each once.</returns>
    private static List<string> UnresolvedResourceKeys(string xaml) =>
        ResourceAttribute.Matches(SourceScanner.StripComments(xaml))
            .Select(m => m.Groups["key"].Value)
            .Distinct(StringComparer.Ordinal)
            .Where(key => Application.Current?.TryFindResource(key) is null)
            .ToList();

    /// <summary>
    /// Every <c>Property="{StaticResource Key}"</c> in the XAML whose resolved value's type is
    /// not assignable to that property.
    /// </summary>
    /// <param name="xaml">The XAML text to scan.</param>
    /// <returns>Human-readable descriptions, one per mismatch.</returns>
    private static IReadOnlyList<string> TypeMismatchedResourceKeys(string xaml)
    {
        var found = new List<string>();
        foreach (Match element in Regex.Matches(SourceScanner.StripComments(xaml),
                     @"<(?<tag>[\w:.]+)(?<attrs>(?:\s+[\w:.]+\s*=\s*""[^""]*"")*)"))
        {
            var tag = element.Groups["tag"].Value;
            var type = ResolveElementType(tag);
            if (type is null) continue;

            foreach (Match attribute in ResourceAttribute.Matches(element.Groups["attrs"].Value))
            {
                var prop = attribute.Groups["prop"].Value;
                var key = attribute.Groups["key"].Value;

                // Attached properties (Grid.Row and friends) are not CLR properties of the
                // element; none of them take a token today, so they are simply out of scope.
                if (prop.Contains('.')) continue;

                var target = type.GetProperty(prop);
                var value = Application.Current?.TryFindResource(key);
                if (target is null || value is null) continue;

                if (!target.PropertyType.IsInstanceOfType(value))
                    found.Add($"{tag}.{prop} (expects {target.PropertyType.Name}) reads "
                        + $"'{key}' (a {value.GetType().Name})");
            }
        }

        return found;
    }

    /// <summary>
    /// Maps a XAML element name — with or without a namespace prefix — onto its CLR type,
    /// searching the WPF namespaces these overlays draw from plus EyeCtrl's own overlay base.
    /// </summary>
    /// <param name="tag">The element name as written in the XAML.</param>
    /// <returns>The type, or null when the element is not one this scan understands.</returns>
    private static Type? ResolveElementType(string tag)
    {
        var name = tag.Contains(':') ? tag[(tag.IndexOf(':') + 1)..] : tag;
        var wpf = typeof(Border).Assembly;
        string[] namespaces =
        [
            "System.Windows.Controls", "System.Windows.Controls.Primitives",
            "System.Windows.Shapes", "System.Windows",
        ];

        return namespaces.Select(ns => wpf.GetType($"{ns}.{name}")).FirstOrDefault(t => t is not null)
            ?? typeof(OverlayWindow).Assembly.GetType($"EyeCtrl.App.Overlays.{name}");
    }

    /// <summary>
    /// The overlay's .xaml text, located by type name under src/EyeCtrl.App. Returns an empty
    /// string for a code-only overlay, which simply means no key can be named from the file.
    /// </summary>
    /// <param name="overlayType">The overlay whose XAML to read.</param>
    /// <returns>The XAML text, or an empty string when there is no .xaml file.</returns>
    private static string XamlOf(Type overlayType)
    {
        var app = Path.Combine(SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App");
        var file = Directory
            .EnumerateFiles(app, $"{overlayType.Name}.xaml", SearchOption.AllDirectories)
            .FirstOrDefault(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

        return file is null ? string.Empty : File.ReadAllText(file);
    }
}
