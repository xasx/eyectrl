namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.Core.Localization;
using NFluent;
using Xunit;

/// <summary>
/// Cross-language drift guard, the <c>ThemeParityTests</c> precedent: the Python validator runs the
/// gates over its own copies of the closed enums, so the C# copies in
/// <see cref="StringTableSchema"/> must match them. The Python file is parsed rather than restating
/// the lists a third time.
/// </summary>
public class StringsEnumParityTests
{
    private static string ValidatorSource()
    {
        var source = Path.Combine(
            SourceScanner.RepoRoot().FullName, "tools", "strings_validate.py");
        Check.WithCustomMessage($"Could not find {source}").That(File.Exists(source)).IsTrue();
        return File.ReadAllText(source);
    }

    private static List<string> ParseList(string source, string name)
    {
        var block = Regex.Match(
            source,
            $@"^\s*{name}\s*=\s*\[(?<body>[^\]]*)\]",
            RegexOptions.Multiline | RegexOptions.Singleline);
        Check.WithCustomMessage($"tools/strings_validate.py has no {name} list").That(block.Success).IsTrue();

        var values = Regex.Matches(block.Groups["body"].Value, @"""(?<value>[^""]+)""")
            .Select(match => match.Groups["value"].Value)
            .ToList();

        Check.WithCustomMessage($"tools/strings_validate.py {name} is empty").That(values).Not.IsEmpty();
        return values;
    }

    [Fact]
    public void Surfaces_MatchTheValidator()
    {
        var parsed = ParseList(ValidatorSource(), "SURFACES");
        Check.That(StringTableSchema.Surfaces).IsEqualTo(parsed);
    }

    [Fact]
    public void Audiences_MatchTheValidator()
    {
        var parsed = ParseList(ValidatorSource(), "WHO_VALUES");
        Check.That(StringTableSchema.Audiences).IsEqualTo(parsed);
    }

    [Fact]
    public void FitClasses_MatchTheValidator()
    {
        var parsed = ParseList(ValidatorSource(), "FIT_CLASSES");
        Check.That(StringTableSchema.FitClasses).IsEqualTo(parsed);
    }
}
