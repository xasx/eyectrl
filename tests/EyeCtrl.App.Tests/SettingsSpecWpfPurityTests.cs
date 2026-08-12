namespace EyeCtrl.App.Tests;

using System;
using System.IO;
using System.Linq;
using NFluent;
using Xunit;

/// <summary>
/// Source guard: the Settings spec layer (<c>src/EyeCtrl.App/Views/Settings/**/*.cs</c>) must stay
/// free of WPF coupling. The whole point of the SettingsWindow decomposition is that page sources
/// are pure data + delegates and every WPF/Dispatcher touch is injected through
/// <see cref="EyeCtrl.App.Views.Settings.SettingsPageContext"/> by the shell.
///
/// <para>Scope: the scan walks <c>Views/Settings/</c> only. The shell <c>SettingsWindow.xaml.cs</c>
/// lives at <c>Views/</c> — outside this tree — so it is excluded by path, not by <see cref="IsExempt"/>.
/// <see cref="ExemptFileName"/>/<see cref="IsExempt"/> is a defensive guard kept in case the shell
/// is ever moved into <c>Views/Settings/</c>; today it never matches.</para>
///
/// <para>This matches the repo's existing source-guard idiom
/// (<see cref="RawGeometryLiteralTests"/>, <see cref="ContainerRatioCensusTests"/>): walk the
/// tree, strip comments so a doc reference is not a false positive, and assert. The guard checks
/// for the <c>using System.Windows</c> import specifically — the precise thing an accidental WPF
/// dependency introduces — rather than the substring <c>System.Windows</c>, which appears inside
/// the <c>SettingsPageContext</c> doc comment as <c>&lt;c&gt;System.Windows.Clipboard&lt;/c&gt;</c>
/// and would false-positive. <b>Accepted limitation:</b> this catches the common accidental import
/// but not aliases (<c>using W = System.Windows.Application;</c>) or fully-qualified calls
/// (<c>System.Windows.Clipboard.GetText()</c>) — the cost of avoiding brittle substring matching.
/// A green run means "no <c>using System.Windows</c> import," not "no WPF coupling of any form."</para>
/// </summary>
public class SettingsSpecWpfPurityTests
{
    private const string ExemptFileName = "SettingsWindow.xaml.cs";
    private const string ForbiddenImport = "using System.Windows";

    private static string SettingsSourcesDir => Path.Combine(
        SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Views", "Settings");

    [Fact]
    public void NoSettingsSpecSource_ImportsSystemWindows()
    {
        var offenders = (from file in Directory.EnumerateFiles(SettingsSourcesDir, "*.cs", SearchOption.AllDirectories)
                         where !IsExempt(file)
                         let text = SourceScanner.StripComments(File.ReadAllText(file))
                         where text.Contains(ForbiddenImport, StringComparison.Ordinal)
                         select RelativeToRepo(file)).ToList();

        Check.WithCustomMessage(
            "A Settings spec/source file imports 'using System.Windows'. The spec layer must stay " +
            "WPF-free; WPF coupling belongs only in " + ExemptFileName + ". Offenders:\n  " +
            string.Join("\n  ", offenders))
            .That(offenders).IsEmpty();
    }

    private static bool IsExempt(string file) =>
        Path.GetFileName(file).Equals(ExemptFileName, StringComparison.OrdinalIgnoreCase);

    private static string RelativeToRepo(string file) =>
        Path.GetRelativePath(SourceScanner.RepoRoot().FullName, file).Replace('\\', '/');
}
