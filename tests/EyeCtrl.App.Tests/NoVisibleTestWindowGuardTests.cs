namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NFluent;
using Xunit;

/// <summary>
/// The guard behind the "no App test shows a visible window" rule. The owner controls the PC only by
/// eye-tracking; a plain <c>dotnet test</c> that paints a window (a toolbar, a keyboard, a ring)
/// takes their foreground and covers what they look at. A test source that calls a SHOWING API on a
/// real window must therefore do one of three things, in the same test member:
/// <list type="number">
/// <item>make the window invisible and non-activating first, through
/// <c>InvisibleTestWindow.Prepare</c> / <c>PrepareCloaked</c> (or a fixture helper that does:
/// <c>ToolbarTestHelpers.NewBoundToolbar</c>);</item>
/// <item>sit behind an opt-in gate (<c>LiveCheck.Require()</c>, <c>BeginShowcaseCase()</c>,
/// <c>AppFixture.SkipIfDisabled()</c>, <c>UiTestsEnabled</c>); or</item>
/// <item>carry an <see cref="AllowList"/> entry with a reason (a window that is already shown
/// off-screen through a product seam, a fake).</item>
/// </list>
/// <para>
/// This is a static scan of the test sources (comments and string literals removed, so a test that
/// merely QUOTES <c>Show()</c> does not count). It cannot see a window shown through an
/// indirection it does not know (a new product method that shows); add the method's name to
/// <see cref="ShowingApis"/> when one appears. It reads files only: running it shows nothing.
/// </para>
/// </summary>
public class NoVisibleTestWindowGuardTests
{
    /// <summary>Methods that put a real window on the screen (product methods, <c>Window.Show</c> and friends).</summary>
    internal static readonly string[] ShowingApis =
    [
        "Show", "ShowDialog", "ShowAtRest", "ShowThenSlideIn", "ShowAt", "ShowBackdrop", "ShowWindow",
        "ShowPanel", "ShowCompletion", "ShowCaregiverAlert", "ShowDriftNotice", "ShowAsync", "RunAsync",
        "ShowAtTopCentre", "ShowCursor", "ShowHighlight", "ShowFullScreen", "StartTracking",
    ];

    /// <summary>What counts as "invisible before the first show" or "opt-in only" inside a member.</summary>
    internal static readonly string[] Gates =
    [
        "InvisibleTestWindow.Prepare", "InvisibleTestWindow.PrepareCloaked", "ToolbarTestHelpers.NewBoundToolbar",
        "LiveCheck.Require", "BeginShowcaseCase", "SkipIfDisabled", "UiTestsEnabled",
        // ToolbarSlideTests' local helper: a docked toolbar built on ToolbarTestHelpers.NewBoundToolbar.
        "NewDockedToolbar(",
    ];

    /// <summary>
    /// <c>file</c> or <c>file#member</c> (repo-relative, tests project) → why it may show without the
    /// helper. Each entry is a decision: a stale entry fails
    /// <see cref="EveryAllowListEntry_StillMatchesAShowingFile_AndNamesAReason"/>.
    /// </summary>
    internal static readonly Dictionary<string, string> AllowList = new()
    {
        ["Overlays/GlassBackdropCloakTests.cs"] =
            "the GlassBackdropWindow runs OFF-SCREEN through the PositionForShow seam (-30000) and the DWM writes are recorded, not painted",
        ["Overlays/GlassBackdropShowBackdropTests.cs"] =
            "the GlassBackdropWindow runs OFF-SCREEN through the PositionForShow seam (-30000)",
        ["Overlays/BackdropHideBeforeInputTests.cs"] =
            "the GlassBackdropWindow runs OFF-SCREEN through the PositionForShow seam (-30000), asserted before every show",
        ["Services/ScrollModeControllerLoggingTests.cs"] =
            "ShowBackdrop is called on FakeGlassBackdropOverlay, not a real window",
        ["Services/Coordinators/TaskSwitcherReArmTests.cs#SwitcherClosing_DuringSlideIn_ReArmsToolbarDwell"] =
            "the toolbar comes from Harness, whose constructor prepares it with InvisibleTestWindow.Prepare before its Show",
        ["Localization/SettingsLiveCheckTests.cs#Open"] =
            "a helper reached only from tests that begin with LiveCheck.Require() (EYECTRL_LIVE_CHECK opt-in)",
        ["Diagnostics/SubPixel/ProbeRingOnScreenHarness.cs#RunWhenEnabled"] =
            "reached only after Run's EYECTRL_UI_TESTS gate returns; ProbeRingOnScreenGateTests pins that gate headless",
    };

    private static readonly Regex ShowCall = new(
        @"\.(" + string.Join("|", ShowingApis) + @")\s*(?:<[^>]*>)?\s*\(", RegexOptions.Compiled);

    private static readonly Regex KeyboardVisibleTrue = new(@"SetKeyboardVisible\s*\(\s*true", RegexOptions.Compiled);

    // A real window the file builds (not a Fake*/Recording*/Stub* double), or the toolbar helper that builds one.
    private static readonly Regex RealWindow = new(
        @"\bnew\s+(?!Fake|Recording|Stub|Test)[A-Za-z_]*(Window|Overlay)\b|\bNewBoundToolbar\s*\(", RegexOptions.Compiled);

    [Fact]
    public void NoTestShowsARealWindow_WithoutTheInvisibleHelper_AnOptInGate_OrAnAllowListEntry()
    {
        var violations = new List<string>();
        foreach (var (relative, text) in TestSources())
            violations.AddRange(FindViolations(relative, text));

        Check.WithCustomMessage(
                "A test shows a real window on the owner's screen (they control the PC by eye-tracking). " +
                "Call InvisibleTestWindow.Prepare(window) (or PrepareCloaked when the window fades itself) " +
                "BEFORE the first Show, or gate the test behind LiveCheck.Require(), or add an AllowList entry " +
                "with a reason:\n  " + string.Join("\n  ", violations))
            .That(violations).IsEmpty();
    }

    [Fact]
    public void EveryAllowListEntry_StillMatchesAShowingFile_AndNamesAReason()
    {
        var sources = TestSources().ToDictionary(s => s.Relative, s => StripCommentsAndStrings(s.Text));
        foreach (var (key, reason) in AllowList)
        {
            Check.WithCustomMessage($"AllowList entry '{key}' needs a reason.").That(reason.Length).IsStrictlyGreaterThan(20);
            var parts = key.Split('#');
            var match = sources.Where(s => s.Key.EndsWith(parts[0], StringComparison.Ordinal)).ToList();
            Check.WithCustomMessage($"AllowList entry '{key}' matches no test file (stale?).").That(match.Count).IsEqualTo(1);
            var members = Members(match[0].Value).Where(m => parts.Length == 1 || m.Name == parts[1]).ToList();
            Check.WithCustomMessage($"AllowList entry '{key}' matches no member (renamed?).").That(members.Count).IsStrictlyGreaterThan(0);
            Check.WithCustomMessage($"AllowList entry '{key}' no longer calls a showing API: remove it.")
                .That(members.Any(m => CallsShowingApi(m.Body))).IsTrue();
        }
    }

    // ---- the scanner's own pins (synthetic sources; nothing is shown) --------------------------------------

    [Fact]
    public void ARealWindowShownWithoutAGate_IsAViolation()
    {
        const string source = """
            namespace T;
            public class C
            {
                [StaFact]
                public void Shows()
                {
                    var window = new ToolbarWindow();
                    window.Show();
                }
            }
            """;
        Check.That(FindViolations("T/C.cs", source)).HasSize(1);
        Check.That(FindViolations("T/C.cs", source.Replace("window.Show();", "window.ShowAtRest();"))).HasSize(1);
        Check.That(FindViolations("T/C.cs", source.Replace("window.Show();", "window.ShowThenSlideIn();"))).HasSize(1);
    }

    [Fact]
    public void TheHelper_TheOptInGates_AndTheAllowList_AreAccepted()
    {
        const string template = """
            namespace T;
            public class C
            {
                [StaFact]
                public void Shows()
                {
                    GATE
                    var window = new ToolbarWindow();
                    window.Show();
                }
            }
            """;
        foreach (var gate in new[]
                 {
                     "InvisibleTestWindow.Prepare(w);", "Fixtures.InvisibleTestWindow.PrepareCloaked(w);", "LiveCheck.Require();",
                     "Step3LiveCheckTests.BeginShowcaseCase();", "AppFixture.SkipIfDisabled();",
                 })
            Check.WithCustomMessage(gate).That(FindViolations("T/C.cs", template.Replace("GATE", gate))).IsEmpty();

        Check.That(FindViolations("Overlays/GlassBackdropCloakTests.cs", template.Replace("GATE", ""))).IsEmpty();
    }

    [Fact]
    public void TheGate_MustBeInTheSameMember_AsTheShow()
    {
        const string source = """
            namespace T;
            public class C
            {
                [StaFact]
                public void Prepared()
                {
                    var w = InvisibleTestWindow.Prepare(new ToolbarWindow());
                    w.Show();
                }

                [StaFact]
                public void Bare()
                {
                    var w = new ToolbarWindow();
                    w.Show();
                }
            }
            """;
        var found = FindViolations("T/C.cs", source);
        Check.That(found).HasSize(1);
        Check.That(found[0]).Contains("Bare");
    }

    [Fact]
    public void CommentsStringsAndFakes_AreNotViolations()
    {
        const string source = """
            namespace T;
            public class C
            {
                [Fact]
                public void Quotes()
                {
                    // window.Show();
                    var s = "window.Show();";
                    var v = @"base.Show()";
                    /* w.ShowAtRest(); */
                    var fake = new FakeToolbarOverlay();
                    fake.ShowAtRest();
                }
            }
            """;
        Check.That(FindViolations("T/C.cs", source)).IsEmpty();
    }

    [Fact]
    public void ANestedHelperClassMember_IsScannedOnItsOwn()
    {
        const string source = """
            namespace T;
            public class C
            {
                private sealed class Harness
                {
                    internal Harness()
                    {
                        Toolbar = new ToolbarWindow();
                        Toolbar.Show();
                    }
                }
            }
            """;
        Check.That(FindViolations("T/C.cs", source)).HasSize(1);
    }

    // ---- the scanner ---------------------------------------------------------------------------------------

    internal static List<string> FindViolations(string relative, string text)
    {
        var violations = new List<string>();
        var stripped = StripCommentsAndStrings(text);
        if (AllowList.Keys.Any(key => !key.Contains('#') && relative.EndsWith(key, StringComparison.Ordinal)))
            return violations;
        if (!RealWindow.IsMatch(stripped))
            return violations;

        foreach (var member in Members(stripped))
        {
            if (!CallsShowingApi(member.Body)) continue;
            if (Gates.Any(g => member.Body.Contains(g, StringComparison.Ordinal))) continue;
            if (AllowList.ContainsKey($"{relative}#{member.Name}")) continue;
            violations.Add($"{relative}:{LineOf(stripped, member.Start)} {member.Name}");
        }
        return violations;
    }

    private static bool CallsShowingApi(string body) => ShowCall.IsMatch(body) || KeyboardVisibleTrue.IsMatch(body);

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    internal readonly record struct Member(string Name, int Start, string Body);

    /// <summary>
    /// The members (methods, constructors, accessors with a body) of every class in <paramref name="stripped"/>,
    /// nested classes included: a brace block whose header is not a type or namespace and whose parent is a
    /// type or namespace.
    /// </summary>
    internal static IEnumerable<Member> Members(string stripped)
    {
        var members = new List<Member>();
        var stack = new Stack<(bool Container, int Start, string Header)>();
        var headerStart = 0;
        for (var i = 0; i < stripped.Length; i++)
        {
            var c = stripped[i];
            if (c == '{')
            {
                var header = stripped[headerStart..i];
                var isType = Regex.IsMatch(header, @"\b(class|struct|record|interface|namespace|enum)\b");
                var parentIsContainer = stack.Count == 0 || stack.Peek().Container;
                stack.Push((isType, i, header));
                headerStart = i + 1;
            }
            else if (c == '}')
            {
                if (stack.Count == 0) continue;
                var (container, start, header) = stack.Pop();
                var parentIsContainer = stack.Count == 0 || stack.Peek().Container;
                if (!container && parentIsContainer)
                    members.Add(new Member(MemberName(header), start, stripped[start..(i + 1)]));
                headerStart = i + 1;
            }
            else if (c == ';')
            {
                headerStart = i + 1;
            }
        }
        return members;
    }

    private static string MemberName(string header)
    {
        var m = Regex.Match(header, @"(\w+)\s*(?:<[^>]*>)?\s*\([^)]*\)\s*(?::[^{]*)?\s*$");
        return m.Success ? m.Groups[1].Value : header.Trim().Split('\n').LastOrDefault()?.Trim() ?? "?";
    }

    /// <summary>Replaces comment and string/char literal CONTENT with spaces (newlines kept), so indices and lines hold.</summary>
    internal static string StripCommentsAndStrings(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n') { sb.Append(' '); i++; }
            }
            else if (c == '/' && next == '*')
            {
                sb.Append("  ");
                i += 2;
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    sb.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }
                if (i < text.Length) { sb.Append("  "); i += 2; }
            }
            else if (c == '"' && next == '"' && i + 2 < text.Length && text[i + 2] == '"')
            {
                // raw string literal: """ ... """
                var quotes = 3;
                while (i + quotes < text.Length && text[i + quotes] == '"') quotes++;
                sb.Append(' ', quotes);
                i += quotes;
                while (i < text.Length && !(text[i] == '"' && text.AsSpan(i).StartsWith(new string('"', quotes))))
                {
                    sb.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }
                var take = Math.Min(quotes, text.Length - i);
                sb.Append(' ', take);
                i += take;
            }
            else if (c == '"' || (c == '@' && next == '"') || (c == '$' && (next == '"' || (next == '@' && i + 2 < text.Length && text[i + 2] == '"'))))
            {
                var verbatim = c == '@' || (c == '$' && next == '@') || (c == '$' && next == '"' && false);
                // skip the prefix
                while (i < text.Length && text[i] != '"') { sb.Append(' '); i++; }
                sb.Append('"'); i++;
                while (i < text.Length)
                {
                    if (verbatim && text[i] == '"' && i + 1 < text.Length && text[i + 1] == '"') { sb.Append("  "); i += 2; continue; }
                    if (!verbatim && text[i] == '\\') { sb.Append("  "); i += 2; continue; }
                    if (text[i] == '"') break;
                    sb.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }
                if (i < text.Length) { sb.Append('"'); i++; }
            }
            else if (c == '\'' )
            {
                // char literal (or a lone apostrophe that cannot start one): blank up to the closing quote
                var j = i + 1;
                if (j < text.Length && text[j] == '\\') j++;
                j += 1;
                if (j < text.Length && text[j] == '\'')
                {
                    sb.Append(' ', j - i + 1);
                    i = j + 1;
                }
                else { sb.Append(c); i++; }
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }

    private static IEnumerable<(string Relative, string Text)> TestSources()
    {
        var root = SourceScanner.RepoRoot().FullName;
        var project = Path.Combine(root, "tests", "EyeCtrl.App.Tests");
        foreach (var path in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(project, path).Replace('\\', '/');
            if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal))
                continue;
            if (relative is "NoVisibleTestWindowGuardTests.cs" or "Fixtures/InvisibleTestWindow.cs")
                continue;
            yield return (relative, File.ReadAllText(path));
        }
    }
}
