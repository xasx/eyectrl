namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.Core.Localization;
using NFluent;
using Xunit;

/// <summary>
/// Headless source scan for T02+'s non-key number call sites: every string literal passed to
/// <c>FormatProviderFor("…")</c> in <c>src/</c> must name a surface from the closed enum in
/// <see cref="StringTableSchema.Surfaces"/>. A typo would silently render English numbers on a
/// German surface; the runtime warns and stays invariant, and this scan fails the build first,
/// including at the call sites T02 will add.
/// </summary>
/// <remarks>
/// A non-literal argument (a variable, an interpolated string) is invisible to the scan; the
/// convention is a literal surface id at the call site, like a key.
/// </remarks>
public class FormatProviderForSurfaceTests
{
    private static readonly Regex LiteralCall = new(
        @"(?<![A-Za-z0-9_])FormatProviderFor\s*\(\s*""(?<surface>[^""]+)""",
        RegexOptions.Compiled);

    internal static List<string> FindUnknownSurfaces(IEnumerable<(string Relative, string Text)> files)
    {
        var violations = new List<string>();
        foreach (var (relative, text) in files)
        {
            foreach (Match match in LiteralCall.Matches(text))
            {
                var surface = match.Groups["surface"].Value;
                if (!StringTableSchema.Surfaces.Contains(surface, StringComparer.Ordinal))
                {
                    violations.Add(
                        $"{relative}: FormatProviderFor(\"{surface}\") does not name a surface id");
                }
            }
        }
        return violations;
    }

    [Fact]
    public void EveryLiteralSurfaceId_InSource_IsAKnownSurface()
    {
        var violations = FindUnknownSurfaces(LocalizationTestData.SourceFiles());

        Check.WithCustomMessage(
                "FormatProviderFor is called with a literal that is not a surface id; a typo would "
                + "silently render English numbers on a German surface. Use one of "
                + $"{string.Join(", ", StringTableSchema.Surfaces)}.\n  "
                + string.Join("\n  ", violations))
            .That(violations).IsEmpty();
    }

    [Fact]
    public void TheScan_CatchesATypo()
    {
        var violations = FindUnknownSurfaces(
            [("src/Fixture.cs", """var p = StringTable.Current.FormatProviderFor("setings");""")]);

        Check.That(violations).HasSize(1);
        Check.That(violations[0]).Contains("setings");
    }

    [Fact]
    public void TheScan_AcceptsAKnownSurface()
    {
        var violations = FindUnknownSurfaces(
            [("src/Fixture.cs", """var p = StringTable.Current.FormatProviderFor("settings");""")]);

        Check.That(violations).IsEmpty();
    }

    [Fact]
    public void TheScan_IgnoresNonLiteralArguments()
    {
        var violations = FindUnknownSurfaces(
            [("src/Fixture.cs", """var p = StringTable.Current.FormatProviderFor(surface);""")]);

        Check.That(violations).IsEmpty();
    }
}
