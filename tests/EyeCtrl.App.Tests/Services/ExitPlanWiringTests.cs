namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

/// <summary>
/// Fixup-01 R2 (review-01): the plan is only as good as the App handing its fields to it. The old
/// guards pinned the App fields inside <c>OnExit</c>; once the step list moved to
/// <see cref="ExitSequencePlan"/> nothing checked that <c>OnExit</c> still fills
/// <see cref="ShutdownDependencies"/>. Dropping <c>ServiceProvider = _serviceProvider</c> silently
/// skips the gaze-engine stop, the settings save, the adaptation persist, the recorder close, the UIA
/// removal and DI disposal — review-01's mutant G+H survived the whole App suite.
/// <para>
/// The reflection half extends itself: a dependency added to <see cref="ShutdownDependencies"/> is
/// required here without touching this file.
/// </para>
/// </summary>
public class ExitPlanWiringTests
{
    [Fact]
    public void OnExit_HandsAnAppFieldToTheExitPlan_ForEveryDependency()
    {
        string body = OnExitBody();

        Check.WithCustomMessage("App.OnExit no longer builds the exit plan")
            .That(body.Contains("ExitSequencePlan.Create(", StringComparison.Ordinal)).IsTrue();

        PropertyInfo[] properties = typeof(ShutdownDependencies)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Check.WithCustomMessage("no dependencies reflected; the check below would be vacuous")
            .That(properties).Not.IsEmpty();

        string[] missing = properties
            .Select(property => property.Name)
            .Where(name => !Regex.IsMatch(body, $@"(?<![\w.]){Regex.Escape(name)}\s*=\s*_\w+"))
            .ToArray();

        Check.WithCustomMessage(
                "App.OnExit no longer assigns these ShutdownDependencies properties from an App field, "
                + "so their steps silently become no-ops: " + string.Join(", ", missing))
            .That(missing).IsEmpty();
    }

    /// <summary>
    /// The body of the first method whose declaration starts with <paramref name="signature"/>,
    /// braces matched. Comments are stripped first, so a commented-out assignment cannot satisfy the
    /// check (and a comment cannot shadow one either).
    /// </summary>
    private static string OnExitBody()
    {
        string source = SourceScanner.StripComments(File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "App.xaml.cs")));
        const string signature = "protected override void OnExit(";

        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Check.WithCustomMessage($"signature not found: {signature}").That(start).IsStrictlyGreaterThan(-1);

        int open = source.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }

        throw new InvalidOperationException("unbalanced OnExit body");
    }
}
