namespace EyeCtrl.App.Tests;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NFluent;
using Xunit;

/// <summary>
/// A static field initialised from Tokens snapshots the palette at type-init time,
/// which is whenever the CLR first touches that type — before App.OnStartup has
/// activated the persisted theme, in general. The field then holds Midnight
/// forever, silently, under every other theme. Assigning Tokens brushes inside a
/// static constructor is the same snapshot in a form a `= Tokens.` grep cannot
/// see — KeyboardWindow had five such assignments, and the guard below catches
/// both forms.
///
/// Forwarding properties (=&gt; Tokens.X) have no such moment. This guard keeps it
/// that way; 30 captures existed before the theme system and all were converted.
/// </summary>
public class StaticTokenCaptureTests
{
    private static readonly string[] ScannedProjects =
    [
        "src/EyeCtrl.App",
        "src/EyeCtrl.Controls",
    ];

    /// <summary>
    /// A static field whose initialiser reads Tokens. Matches `= Tokens.` but not
    /// `=&gt; Tokens.`, because the arrow form is the fix, not the defect.
    /// </summary>
    private static readonly Regex Capture = new(
        @"static\s+(?:readonly\s+)?[\w.<>\[\]?]+\s+\w+\s*=\s*Tokens\.",
        RegexOptions.Compiled);

    /// <summary>
    /// A static constructor (or parameterless static method) whose first-level body
    /// reads Tokens — the same type-init snapshot as a field initialiser, invisible
    /// to <see cref="Capture"/>. KeyboardWindow's constructor held five such reads.
    /// The `[^{}]*` span stops at nested braces, so a read nested deeper than one
    /// level is missed — widening that span is a future refinement, not a gap in
    /// what this task removes.
    /// </summary>
    private static readonly Regex CtorCapture = new(
        @"static\s+\w+\(\)\s*\{[^{}]*Tokens\.",
        RegexOptions.Compiled);

    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(
            typeof(StaticTokenCaptureTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent;
        Check.That(dir).IsNotNull();
        return dir!;
    }

    [Fact]
    public void NoStaticField_SnapshotsAToken()
    {
        var root = RepoRoot().FullName;
        var offenders = new List<string>();

        foreach (var project in ScannedProjects)
        {
            var dir = Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

                var text = File.ReadAllText(file);
                foreach (Match m in Capture.Matches(text))
                    offenders.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}: {m.Value.Trim()}");
                foreach (Match m in CtorCapture.Matches(text))
                    offenders.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}: static ctor reads Tokens: {m.Value.Trim()}");
            }
        }

        Check.WithCustomMessage(
                "These static fields and static ctors snapshot the palette at type-init "
                + "time and will hold Midnight under every other theme. Change '= Tokens.X' "
                + "to '=> Tokens.X' and drop 'readonly'; delete any static ctor that reads "
                + "Tokens:\n  "
                + string.Join("\n  ", offenders))
            .That(offenders).IsEmpty();
    }
}
