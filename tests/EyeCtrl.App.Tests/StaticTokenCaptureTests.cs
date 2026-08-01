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
    /// Opening of a static constructor. A static ctor is always parameterless and
    /// named for its type, so <c>static Identifier()</c> cannot also match a method
    /// — a method would need a return type between <c>static</c> and its name.
    /// <see cref="StaticCtorReadsTokens"/> walks the body from here.
    /// </summary>
    private static readonly Regex CtorOpen = new(
        @"static\s+\w+\(\)\s*\{",
        RegexOptions.Compiled);

    /// <summary>
    /// Whether any static constructor in <paramref name="text"/> reads Tokens
    /// anywhere in its body — the same type-init snapshot as a field initialiser,
    /// and invisible to <see cref="Capture"/>. KeyboardWindow's constructor held
    /// five such reads before they became forwarding properties.
    ///
    /// Brace-walks to the matching close rather than spanning <c>[^{}]*</c>. The
    /// span form stopped at the first nested brace, so a read inside an
    /// <c>if</c>/<c>foreach</c>/<c>try</c> block escaped the guard entirely —
    /// exactly the shape someone reintroducing a capture is likely to write.
    /// </summary>
    /// <param name="text">Full source text of one file.</param>
    /// <param name="body">The offending constructor body, when one is found.</param>
    /// <returns><c>true</c> if a static constructor reads Tokens.</returns>
    private static bool StaticCtorReadsTokens(string text, out string body)
    {
        foreach (Match open in CtorOpen.Matches(text))
        {
            var depth = 0;
            var start = open.Index + open.Length - 1;   // the opening '{'

            for (var i = start; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth != 0) continue;

                    var span = text[start..(i + 1)];
                    if (span.Contains("Tokens.", StringComparison.Ordinal))
                    {
                        body = open.Value + " … }";
                        return true;
                    }

                    break;   // this ctor is clean; on to the next
                }
            }
        }

        body = string.Empty;
        return false;
    }

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
                if (StaticCtorReadsTokens(text, out var ctorBody))
                    offenders.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}: static ctor reads Tokens: {ctorBody.Trim()}");
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
