namespace EyeCtrl.Setup.Tests;

using System.Text;
using System.Text.RegularExpressions;
using EyeCtrl.Core.Services;
using EyeCtrl.Setup;
using EyeCtrl.Setup.Tests.TestSupport;
using EyeCtrl.Setup.Windows;

/// <summary>
/// Fixup 02 G1/G2: the one-line wiring that closes review-01's blocker must itself be pinned.
/// <para>
/// The D3 entry-point tests prove that the SETUP RULE, applied to a real entry point's argv,
/// yields the installed App's paths; they cannot prove that the Setup PROCESS uses that rule,
/// because a Debug test host resolves the same dev answer under both rules. These tests close the
/// two surviving mutants:
/// </para>
/// <list type="bullet">
/// <item><b>W1</b> — <c>ProductionPaths()</c> back to <see cref="ProfileLocation.Current"/>:
/// the source ratchet names the line, and the reference test pins the composed instance.</item>
/// <item><b>W2</b> — <c>SessionMutexName</c> back to <see cref="ProfileLocation.Current"/>: same
/// two pins via <see cref="SetupSessionGuard.SessionProfile"/>.</item>
/// </list>
/// <para>
/// G2 rides along: Setup's held-button marker must come from the Setup profile, never the
/// parameterless <see cref="FileHeldButtonMarker"/> default (the App rule).
/// </para>
/// </summary>
public class SetupProfileWiringTests
{
    [Fact]
    public void TheSetupProcess_ComposesWithTheSetupRule_NotTheAppRule()
    {
        // Object identity, because in a Debug test host Current and CurrentSetup have EQUAL VALUES
        // (both dev). They are distinct instances by construction; a value comparison proves
        // nothing (W1/W2 survived on exactly that).
        Check.That(ReferenceEquals(SetupComposition.ProcessProfile, ProfileLocation.CurrentSetup)).IsTrue();
        Check.That(ReferenceEquals(SetupComposition.ProcessProfile, ProfileLocation.Current)).IsFalse();
        Check.That(ReferenceEquals(SetupSessionGuard.SessionProfile, ProfileLocation.CurrentSetup)).IsTrue();
        Check.That(ReferenceEquals(SetupSessionGuard.SessionProfile, ProfileLocation.Current)).IsFalse();

        // Both names are derived from those instances.
        Check.That(SetupSessionGuard.SessionMutexName)
            .IsEqualTo(SetupComposition.ProcessProfile.SetupSessionMutexName);
        Check.That(SetupSessionGuard.SessionMutexName)
            .IsEqualTo(SetupSessionGuard.SessionProfile.SetupSessionMutexName);
    }

    [Fact]
    public void TheHeldButtonMarker_IsTheSetupProfilesMarker()
    {
        // G2: the marker the kill path reads/sends must live where the INSTALLED App writes it.
        // A parameterless FileHeldButtonMarker would take the App rule (EyeCtrl.Dev in a test
        // host / a flagless run copy) and leave the owner's left button stuck.
        var paths = new SetupPaths(
            @"C:\Program Files", @"C:\Users\owner\AppData\Roaming", @"C:\Users\owner\AppData\Local");

        var recovery = new CoreHeldButtonRecovery(paths);

        Check.That(recovery.Marker.Path)
            .IsEqualTo(Path.Combine(paths.AppDataRoot, FileHeldButtonMarker.FileName));
        Check.That(paths.AppDataRoot)
            .IsEqualTo(Path.Combine(@"C:\Users\owner\AppData\Roaming", "EyeCtrl"));
    }

    [Fact]
    public void ProductionPaths_KeepTheSetupRulesProfileInstance()
    {
        // H1.4 (fixup 03): a behavioural pin that no regex can be talked out of and no spelling
        // can evade — the layout Setup actually composes with carries the resolver instance it was
        // built from. In a Debug test host the App rule and the Setup rule produce equal VALUES;
        // only object identity tells them apart.
        var paths = SetupComposition.ProductionPaths();

        Check.That(paths.Profile).IsNotNull();
        Check.That(ReferenceEquals(paths.Profile, ProfileLocation.CurrentSetup)).IsTrue();
        Check.That(ReferenceEquals(paths.Profile, ProfileLocation.Current)).IsFalse();

        // The session mutex name is derived from that same instance.
        Check.That(ReferenceEquals(SetupSessionGuard.SessionProfile, paths.Profile)).IsTrue();
        Check.That(SetupSessionGuard.SessionMutexName)
            .IsEqualTo(paths.Profile!.SetupSessionMutexName);
    }

    [Fact]
    public void SetupSources_UseTheSetupRule_AndNeverTheAppRule()
    {
        // G1(a) / fixup 03 H1/H2. The App rule is allowed at exactly ONE place: the App's own
        // pre-hand-off session name (`SetupSessionGuard.AppProbeMutexName`). Everything is matched
        // over the whole comment-stripped text, so a line break before the dot (E3), a static
        // import (E1) or a type alias (E2) cannot slip through, and the exemption is the exact
        // declaration rather than any line that merely contains its name (H1.3).
        var repo = InstalledProductProfile.FindRepoRoot();
        var sourceRoot = Path.Combine(repo, "src", "EyeCtrl.Setup");
        Check.WithCustomMessage($"source folder not found: {sourceRoot}")
            .That(Directory.Exists(sourceRoot)).IsTrue();

        var appProbeDeclaration = new Regex(
            @"public\s+static\s+string\s+AppProbeMutexName\s*=>\s*"
            + @"ProfileLocation\s*\.\s*Current\.SetupSessionMutexName\s*;",
            RegexOptions.Singleline);

        // \s matches newlines too (E3: `ProfileLocation` newline `.Current`).
        var appRule = new Regex(@"ProfileLocation\s*\.\s*Current(?!Setup)\b");
        var setupRule = new Regex(@"ProfileLocation\s*\.\s*CurrentSetup\b");
        var staticImport = new Regex(@"using\s+static\s+[\w.]*ProfileLocation\s*;");
        var aliasImport = new Regex(@"using\s+\w+\s*=\s*[\w.]*ProfileLocation\s*;");
        var coreAppRuleDefaults = new (string Token, string Why)[]
        {
            ("FileHeldButtonMarker.DefaultPath", "the App rule's marker folder"),
            ("SettingsService.WriterMutexName", "the App rule's settings-writer mutex"),
            ("SingleInstanceGuard.DefaultMutexName", "the App rule's singleton mutex"),
        };
        var defaultMarker = new Regex(@"new\s+FileHeldButtonMarker\s*\(\s*(?:\)|null\s*\))");

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(repo, file);
            var code = StripCSharpComments(File.ReadAllText(file));
            // Remove exactly the one legitimate declaration; anything else remains scannable.
            var scannable = appProbeDeclaration.Replace(code, "/* app probe's named exemption */");

            foreach (Match match in staticImport.Matches(scannable))
            {
                violations.Add(
                    $"{relative}:{LineOf(scannable, match.Index)} uses 'using static …ProfileLocation'; " +
                    "a static import hides the App rule (H1.2)");
            }

            foreach (Match match in aliasImport.Matches(scannable))
            {
                violations.Add(
                    $"{relative}:{LineOf(scannable, match.Index)} aliases ProfileLocation; " +
                    "an alias hides the App rule (H1.2)");
            }

            foreach (Match match in appRule.Matches(scannable))
            {
                violations.Add(
                    $"{relative}:{LineOf(scannable, match.Index)} uses ProfileLocation.Current (the App " +
                    "rule); Setup must use SetupProcessProfile.Location (the Setup rule)");
            }

            if (!relative.EndsWith("SetupProcessProfile.cs", StringComparison.Ordinal))
            {
                foreach (Match match in setupRule.Matches(scannable))
                {
                    violations.Add(
                        $"{relative}:{LineOf(scannable, match.Index)} names ProfileLocation.CurrentSetup " +
                        "directly; route it through SetupProcessProfile so the process has one profile source");
                }
            }

            foreach (var (token, why) in coreAppRuleDefaults)
            {
                if (scannable.Contains(token, StringComparison.Ordinal))
                {
                    violations.Add(
                        $"{relative} uses {token} ({why}); derive it from SetupProcessProfile.Location");
                }
            }

            foreach (Match match in defaultMarker.Matches(scannable))
            {
                violations.Add(
                    $"{relative}:{LineOf(scannable, match.Index)} builds the held-button marker from the " +
                    "App-rule default; pass SetupPaths.AppDataRoot (the Setup profile)");
            }
        }

        Check.WithCustomMessage(
                "These Setup sources bypass the Setup profile rule (fixup 02 G1/G2, fixup 03 H1/H2):\n"
                + string.Join("\n", violations))
            .That(violations).IsEmpty();
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
                line++;
        }

        return line;
    }

    /// <summary>Removes line (<c>//</c>, <c>///</c>) and block (<c>/* */</c>) comments, preserving
    /// line breaks for the locations; string literals are copied verbatim.</summary>
    private static string StripCSharpComments(string source)
    {
        var span = source.AsSpan();
        var result = new StringBuilder(source.Length);
        var i = 0;
        while (i < span.Length)
        {
            var c = span[i];
            if (c == '"' || c == '\'')
            {
                var quote = c;
                result.Append(c);
                i++;
                while (i < span.Length)
                {
                    result.Append(span[i]);
                    if (span[i] == '\\' && i + 1 < span.Length)
                    {
                        result.Append(span[i + 1]);
                        i += 2;
                        continue;
                    }

                    if (span[i] == quote)
                    {
                        i++;
                        break;
                    }

                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < span.Length && span[i + 1] == '/')
            {
                while (i < span.Length && span[i] != '\n')
                    i++;
                continue;
            }

            if (c == '/' && i + 1 < span.Length && span[i + 1] == '*')
            {
                var end = span.Slice(i + 2).IndexOf("*/".AsSpan());
                i = end < 0 ? span.Length : i + 2 + end + 2;
                continue;
            }

            result.Append(c);
            i++;
        }

        return result.ToString();
    }
}
