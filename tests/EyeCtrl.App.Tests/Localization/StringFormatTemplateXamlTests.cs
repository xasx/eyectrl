namespace EyeCtrl.App.Tests.Localization;

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NFluent;
using Xunit;

/// <summary>
/// A <c>{loc:T}</c> used as a binding's <c>StringFormat</c> must pass <c>AsTemplate=True</c>. Without
/// it the extension formats the template with no arguments when the XAML loads, and every start logs
/// "String template '…{0}…' could not be formatted" (9th sitting, 18.11: the switcher's overflow
/// chip). The binding fills the placeholders, so the extension must hand back the raw template.
/// </summary>
public class StringFormatTemplateXamlTests
{
    private static readonly Regex StringFormatT = new(@"StringFormat=\{loc:T\s[^}]*\}", RegexOptions.Compiled);

    [Fact]
    public void EveryStringFormatFromTheTable_IsReadAsATemplate()
    {
        var root = SourceScanner.RepoRoot();
        var offenders = Directory
            .EnumerateFiles(Path.Combine(root.FullName, "src", "EyeCtrl.App"), "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(path => StringFormatT.Matches(File.ReadAllText(path))
                .Select(match => (File: Path.GetFileName(path), match.Value)))
            .Where(hit => !hit.Value.Contains("AsTemplate=True"))
            .Select(hit => $"{hit.File}: {hit.Value}")
            .ToList();

        Check.WithCustomMessage("A StringFormat that the binding fills must read its text with AsTemplate=True:\n"
                + string.Join("\n", offenders))
            .That(offenders).IsEmpty();
    }

    [Fact]
    public void TheSwitcherOverflowChip_IsCoveredByTheScan()
    {
        // The scan's only current hit: if this file stops matching, the regex has gone blind.
        var xaml = File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Overlays", "TaskSwitcherWindow.xaml"));

        Check.That(StringFormatT.Matches(xaml).Count).IsEqualTo(1);
    }
}
