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
    public void SetupSources_UseTheSetupRule_AndNeverTheAppRule()
    {
        // G1(a): every Setup source derives its profile from SetupProcessProfile. The App rule may
        // appear ONLY on the AppProbeMutexName declaration (the App's own session name, which the
        // pre-hand-off probe must use), and the parameterless marker constructor is forbidden (G2).
        var repo = InstalledProductProfile.FindRepoRoot();
        var sourceRoot = Path.Combine(repo, "src", "EyeCtrl.Setup");
        Check.WithCustomMessage($"source folder not found: {sourceRoot}")
            .That(Directory.Exists(sourceRoot)).IsTrue();

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(repo, file);
            var lines = StripCSharpComments(File.ReadAllText(file)).Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].TrimEnd('\r');
                var where = $"{relative}:{index + 1}";

                if (Regex.IsMatch(line, @"ProfileLocation\.Current(?!Setup)")
                    && !line.Contains("AppProbeMutexName", StringComparison.Ordinal))
                {
                    violations.Add(
                        $"{where} uses ProfileLocation.Current (the App rule); Setup must use " +
                        "SetupProcessProfile.Location (the Setup rule)");
                }

                if (Regex.IsMatch(line, @"ProfileLocation\.CurrentSetup")
                    && !relative.EndsWith("SetupProcessProfile.cs", StringComparison.Ordinal))
                {
                    violations.Add(
                        $"{where} names ProfileLocation.CurrentSetup directly; route it through " +
                        "SetupProcessProfile so the process has one profile source");
                }

                if (Regex.IsMatch(line, @"new\s+FileHeldButtonMarker\s*\(\s*\)"))
                {
                    violations.Add(
                        $"{where} builds the held-button marker from the App rule; pass " +
                        "SetupPaths.AppDataRoot (the Setup profile)");
                }
            }
        }

        Check.WithCustomMessage(
                "These Setup sources bypass the Setup profile rule (fixup 02 G1/G2):\n"
                + string.Join("\n", violations))
            .That(violations).IsEmpty();
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
