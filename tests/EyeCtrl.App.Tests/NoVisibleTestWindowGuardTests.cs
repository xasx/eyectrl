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
/// takes their foreground and covers what they look at.
///
/// <para>
/// <b>The rule, per WINDOW.</b> In a test member, every real window the test constructs
/// (<c>new XxxWindow(...)</c>, not a Fake/Recording/Stub double) that is then SHOWN (a showing API called on it,
/// <c>Visibility = Visible</c>, or the call inlined on the construction) or HANDED to an overlay container
/// (<c>new ...Overlays(...)</c> / <c>new ...OverlayManager(...)</c> — the product code under test shows it)
/// must itself be made invisible first: constructed inside, or later passed to,
/// <c>InvisibleTestWindow.Prepare</c> / <c>PrepareCloaked</c> / <c>PrepareOffScreen</c>. One prepared window does NOT
/// excuse another in the same member. A window that <c>PrepareOffScreen</c> prepared (the non-layered glass
/// backdrop) may only be shown through <c>ShowBackdrop</c>: its plain <c>Show()</c> bypasses the off-screen seam.
/// A member behind an opt-in gate (<c>LiveCheck.Require()</c>, <c>BeginShowcaseCase()</c>,
/// <c>AppFixture.SkipIfDisabled()</c>, <c>UiTestsEnabled</c>; matched as whole identifiers) is exempt, as is an
/// <see cref="AllowList"/> entry (<c>file</c> or <c>file#member</c>) with a reason. A bare P/Invoke
/// <c>ShowWindow(...)</c> call in a member is a violation unless gated.
/// </para>
///
/// <para>
/// <b>Method.</b> A static scan of the test sources (comments and string literals blanked, so quoting
/// <c>Show()</c> does not count; methods, constructors, accessors, expression-bodied members and field
/// initializers are scanned as members). Window names come from the assignment target of the construction
/// (<c>var w = new ...</c>, <c>_field = new ...</c>) — a documented name heuristic.
/// </para>
///
/// <para>
/// <b>Known limits</b> (named, not hidden): a window that comes from a helper in ANOTHER file, or a parameter, or a
/// field assigned in a different member, is invisible to the scan (the fixture helpers
/// <c>ToolbarTestHelpers.NewBoundToolbar</c> / <c>NewDockedToolbar</c> are prepared at their one construction
/// site, so their callers are safe); a showing method it does not know, or a container not named
/// <c>*Overlays</c>/<c>*OverlayManager</c>, is not seen — add the name to <see cref="ShowingApis"/> when a new one
/// appears. It reads files only: running it shows nothing.
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
        "ShowButton", "ShowNotice", "ShowToolbar", "ShowRingIfVisible",
    ];

    /// <summary>Opt-in gates (whole identifiers): a member that starts with one only runs when the owner opted in.</summary>
    internal static readonly string[] OptInGates =
        ["LiveCheck.Require", "BeginShowcaseCase", "SkipIfDisabled", "UiTestsEnabled"];

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
        ["Localization/SettingsLiveCheckTests.cs#Open"] =
            "a helper reached only from tests that begin with LiveCheck.Require() (EYECTRL_LIVE_CHECK opt-in)",
        ["Diagnostics/SubPixel/ProbeRingOnScreenHarness.cs#RunWhenEnabled"] =
            "reached only after Run's EYECTRL_UI_TESTS gate returns; ProbeRingOnScreenGateTests pins that gate headless",
    };

    [Fact]
    public void NoTestShowsARealWindow_WithoutTheInvisibleHelper_AnOptInGate_OrAnAllowListEntry()
    {
        var violations = new List<string>();
        foreach (var (relative, text) in TestSources())
            violations.AddRange(FindViolations(relative, text));

        Check.WithCustomMessage(
                "A test shows (or hands to an overlay container) a real window on the owner's screen (they control " +
                "the PC by eye-tracking). Pass THAT window to InvisibleTestWindow.Prepare(window) (PrepareCloaked when " +
                "its own code fades it, PrepareOffScreen for the glass backdrop) BEFORE the first show, or gate the " +
                "test behind LiveCheck.Require(), or add an AllowList entry with a reason:\n  " + string.Join("\n  ", violations))
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
            Check.WithCustomMessage($"AllowList entry '{key}' no longer shows a window: remove it.")
                .That(members.Any(m => ShowingCall.IsMatch(m.Body))).IsTrue();
        }
    }

    // ---- the scanner's own pins (synthetic sources; nothing is shown) --------------------------------------

    private static List<string> Scan(string members) =>
        FindViolations("T/C.cs", "namespace T;\npublic class C\n{\n" + members + "\n}\n");

    [Fact]
    public void ARealWindowShownWithoutPreparation_IsAViolation_InEveryShowingShape()
    {
        const string body = "    [StaFact]\n    public void Shows()\n    {\n        var window = new ToolbarWindow();\n        window.CALL;\n    }";
        foreach (var call in new[]
                 {
                     "Show()", "ShowAtRest()", "ShowThenSlideIn()", "ShowButton()", "ShowNotice()", "ShowDialog()",
                     "Visibility = Visibility.Visible",
                 })
            Check.WithCustomMessage(call).That(Scan(body.Replace("CALL", call))).HasSize(1);
    }

    [Fact]
    public void APreparedWindow_IsAccepted_WrappedOrPreparedLater_ForEveryHelper()
    {
        foreach (var helper in new[] { "InvisibleTestWindow.Prepare", "Fixtures.InvisibleTestWindow.PrepareCloaked" })
        {
            Check.That(Scan($"    public void A()\n    {{\n        var w = {helper}(new ToolbarWindow());\n        w.Show();\n    }}")).IsEmpty();
            Check.That(Scan($"    public void B()\n    {{\n        var w = new ToolbarWindow();\n        {helper}(w);\n        w.Show();\n    }}")).IsEmpty();
            Check.That(Scan($"    public void C()\n    {{\n        var w = {helper}(new ToolbarWindow {{ DataContext = vm }});\n        w.ShowAtRest();\n    }}")).IsEmpty();
        }
    }

    [Fact]
    public void OnePreparedWindow_DoesNotExcuseAnotherWindowInTheSameMember()
    {
        // V2: a different window is shown. V9: an unprepared window is handed to a container beside a prepared one.
        var shown = Scan("""
                public void Shows()
                {
                    var a = InvisibleTestWindow.Prepare(new ToolbarWindow());
                    var b = new KeyboardWindow();
                    b.Show();
                }
            """);
        Check.That(shown).HasSize(1);
        Check.That(shown[0]).Contains("KeyboardWindow 'b'");

        var handed = Scan("""
                public void Hands()
                {
                    var toolbar = InvisibleTestWindow.Prepare(new ToolbarWindow());
                    var indicator = new PauseIndicatorWindow();
                    var overlays = new FakeOverlayManager(toolbar, indicator);
                }
            """);
        Check.That(handed).HasSize(1);
        Check.That(handed[0]).Contains("PauseIndicatorWindow 'indicator'").And.Contains("overlay container");
    }

    [Fact]
    public void AFieldAssignedWindow_HandedToAContainer_MustBePreparedToo()
    {
        const string ctor = """
                internal Harness()
                {
                    Toolbar = InvisibleTestWindow.Prepare(new ToolbarWindow());
                    _switcherWindow = new TaskSwitcherWindow();
                    var o = new FakeOverlays(Toolbar, _switcherWindow);
                }
            """;
        var found = Scan(ctor);
        Check.That(found).HasSize(1);
        Check.That(found[0]).Contains("TaskSwitcherWindow '_switcherWindow'");
        Check.That(Scan(ctor.Replace("_switcherWindow = new TaskSwitcherWindow();",
            "_switcherWindow = InvisibleTestWindow.Prepare(new TaskSwitcherWindow());"))).IsEmpty();
    }

    [Fact]
    public void AnExpressionBodiedMember_AndAnInlineShow_AreScanned()
    {
        Check.That(Scan("    public void Shows() => new ToolbarWindow().Show();")).HasSize(1);
        Check.That(Scan("    private static void Shows()\n    {\n        new ToolbarWindow().ShowAtRest();\n    }")).HasSize(1);
        Check.That(Scan("    public void Fine() => InvisibleTestWindow.Prepare(new ToolbarWindow()).Show();")).IsEmpty();
        // a field initializer that builds a window the test then shows from elsewhere is out of reach (known limit),
        // but an initializer that shows inline is seen:
        Check.That(Scan("    private readonly object _x = new ToolbarWindow().ShowAtRest();")).HasSize(1);
    }

    [Fact]
    public void AWindowBuiltWithAHeadlessPresentSeam_IsNotShown()
    {
        const string body = """
                public void P()
                {
                    var card = new RestartNoticeWindow(dwellMs: 25, messenger, keepClear: null, present: c => { c.Measure(s); });
                    card.ShowNotice("a", "b", t);
                }
            """;
        Check.That(Scan(body)).IsEmpty();
        Check.That(Scan(body.Replace("present: c => { c.Measure(s); }", "dwell: 3"))).HasSize(1);
    }

    [Fact]
    public void ShowAtOnAnActivationButtonWindow_IsNotAShow_ButItsShowIs()
    {
        const string body = """
                public void H()
                {
                    var window = new ActivationButtonWindow();
                    window.ShowAt(b, hit);
                    window.CALL;
                }
            """;
        Check.That(Scan(body.Replace("CALL", "UpdateDwellProgress(0)"))).IsEmpty();
        Check.That(Scan(body.Replace("CALL", "Show()"))).HasSize(1);
        Check.That(Scan(body.Replace("CALL", "ShowAtRest()"))).HasSize(1);
        // another window type's ShowAt is still a show (DwellFeedbackOverlay.ShowAt calls Show())
        Check.That(Scan(body.Replace("ActivationButtonWindow", "DwellFeedbackWindow").Replace("CALL", "UpdateDwellProgress(0)"))).HasSize(1);
        Check.That(Scan("    public void Inline() => new ActivationButtonWindow().ShowAt(b, hit);")).IsEmpty();
        Check.That(Scan("    public void Inline() => new ActivationButtonWindow().Show();")).HasSize(1);
    }

    [Fact]
    public void AMethodGroupOfAShowingApi_IsAShow()
    {
        Check.That(Scan("    public void G()\n    {\n        var w = new ToolbarWindow();\n        Action a = w.Show;\n        a();\n    }")).HasSize(1);
    }

    [Fact]
    public void ABareShowWindowPInvoke_IsAViolation_UnlessGated_AndItsExternDeclarationIsNot()
    {
        Check.That(Scan("    public void P()\n    {\n        ShowWindow(handle, 5);\n    }")).HasSize(1);
        Check.That(Scan("    [DllImport(\"user32.dll\")]\n    private static extern bool ShowWindow(IntPtr h, int n);")).IsEmpty();
        Check.That(Scan("    public void P()\n    {\n        LiveCheck.Require();\n        ShowWindow(handle, 5);\n    }")).IsEmpty();
    }

    [Fact]
    public void OptInGates_AreWholeIdentifiers_NotSubstrings()
    {
        const string body = "    public void Shows()\n    {\n        GATE\n        var w = new ToolbarWindow();\n        w.Show();\n    }";
        foreach (var gate in new[] { "LiveCheck.Require();", "Step3LiveCheckTests.BeginShowcaseCase();", "AppFixture.SkipIfDisabled();", "if (!AppFixture.UiTestsEnabled) return;" })
            Check.WithCustomMessage(gate).That(Scan(body.Replace("GATE", gate))).IsEmpty();
        foreach (var impostor in new[]
                 {
                     "var UiTestsEnabledFlag = 1;", "MyLiveCheck.Required();", "var xBeginShowcaseCaseY = 2;", "SkipIfDisabledElsewhere();",
                     "var xUiTestsEnabled = 1;", "NotLiveCheck.Require();", "MyBeginShowcaseCase();", "ReallySkipIfDisabled();",
                 })
            Check.WithCustomMessage(impostor).That(Scan(body.Replace("GATE", impostor))).HasSize(1);
    }

    [Fact]
    public void AnOffScreenPreparedBackdrop_MayOnlyBeShownThroughShowBackdrop()
    {
        const string body = "    public void B()\n    {\n        var backdrop = InvisibleTestWindow.PrepareOffScreen(new GlassBackdropWindow());\n        backdrop.CALL;\n    }";
        Check.That(Scan(body.Replace("CALL", "ShowBackdrop(settings, centre)"))).IsEmpty();
        var found = Scan(body.Replace("CALL", "Show()"));
        Check.That(found).HasSize(1);
        Check.That(found[0]).Contains("PrepareOffScreen").And.Contains("ShowBackdrop");
    }

    [Fact]
    public void TheAllowList_FakesCommentsAndStrings_AreNotViolations()
    {
        const string quoting = """
                public void Quotes()
                {
                    var w = new ToolbarWindow();
                    // w.Show();
                    var s = "w.Show();";
                    var v = @"w.Show()";
                    var r = $"{x} w.ShowAtRest()";
                    /* w.ShowAtRest(); */
                    var fake = new FakeToolbarOverlay();
                    fake.ShowAtRest();
                    var recording = new RecordingToolbarWindow();
                    recording.Show();
                }
            """;
        Check.That(Scan(quoting)).IsEmpty();
        Check.That(FindViolations("Overlays/GlassBackdropCloakTests.cs",
            "namespace T;\npublic class C\n{\n public void X()\n {\n var w = new GlassBackdropWindow();\n w.Show();\n }\n}\n")).IsEmpty();
        Check.That(FindViolations("Localization/SettingsLiveCheckTests.cs",
            "namespace T;\npublic class C\n{\n public void Open()\n {\n var w = new SettingsWindow();\n w.Show();\n }\n}\n")).IsEmpty();
    }

    [Fact]
    public void ANestedHelperClassMember_IsScannedOnItsOwn()
    {
        const string source = """
                private sealed class Harness
                {
                    internal Harness()
                    {
                        Toolbar = new ToolbarWindow();
                        Toolbar.Show();
                    }
                }
            """;
        Check.That(Scan(source)).HasSize(1);
    }

    // ---- the scanner ---------------------------------------------------------------------------------------

    private static readonly string ApiAlternation = string.Join("|", ShowingApis);

    // ActivationButtonWindow.ShowAt only positions the window and starts its fade-in; the product calls Show() afterwards
    // (OverlayVmBindingCoordinator). A test that calls ShowAt alone never shows it (FixationHaloRenderTests), so ShowAt is
    // not a showing API on THAT window type; its Show() still is.
    private static readonly string ApiAlternationWithoutShowAt = string.Join("|", ShowingApis.Where(a => a != "ShowAt"));

    private static string ApisFor(string windowType) =>
        windowType == "ActivationButtonWindow" ? ApiAlternationWithoutShowAt : ApiAlternation;

    private static readonly Regex ShowingCall = new(
        @"\.\s*(?:" + ApiAlternation + @")\b|(?<![\w.])ShowWindow\s*\(", RegexOptions.Compiled);

    private static readonly Regex RealConstruction = new(
        @"\bnew\s+(?!Fake|Recording|Stub|Test)(?<type>\w*Window)\b\s*[({]", RegexOptions.Compiled);

    private static readonly Regex OverlayContainerArgs = new(
        @"\bnew\s+\w*(?:Overlays|OverlayManager)\w*\s*\((?<args>[^;]*)", RegexOptions.Compiled);

    private static readonly Regex[] OptInGateRegexes = OptInGates
        .Select(g => new Regex(@"(?<![\w])" + Regex.Escape(g).Replace(@"\.", @"\s*\.\s*") + @"(?![\w])", RegexOptions.Compiled))
        .ToArray();

    private static readonly Regex BareShowWindow = new(@"(?<![\w.])ShowWindow\s*\(", RegexOptions.Compiled);

    internal static List<string> FindViolations(string relative, string text)
    {
        var violations = new List<string>();
        if (AllowList.Keys.Any(key => !key.Contains('#') && relative.EndsWith(key, StringComparison.Ordinal)))
            return violations;
        var stripped = StripCommentsAndStrings(text);

        foreach (var member in Members(stripped))
        {
            if (AllowList.ContainsKey($"{relative}#{member.Name}")) continue;
            if (OptInGateRegexes.Any(g => g.IsMatch(member.Body))) continue;
            foreach (var problem in MemberProblems(member.Body))
                violations.Add($"{relative}:{LineOf(stripped, member.Start)} {member.Name}: {problem}");
        }
        return violations;
    }

    private sealed record WindowUse(string Type, string? Name, bool Prepared, bool OffScreen, bool InlineShown);

    private static IEnumerable<string> MemberProblems(string body)
    {
        // A bare P/Invoke ShowWindow(...) call (the extern declaration itself is not a call).
        if (BareShowWindow.IsMatch(body) && !Regex.IsMatch(body, @"\bextern\b"))
            yield return "a bare ShowWindow(...) call shows a window the guard cannot name";

        foreach (var use in WindowsConstructedIn(body))
        {
            var prepared = use.Prepared
                || (use.Name is not null && Regex.IsMatch(body,
                    @"InvisibleTestWindow\s*\.\s*Prepare\w*\s*\(\s*" + Regex.Escape(use.Name) + @"\b"));
            var offScreen = use.OffScreen
                || (use.Name is not null && Regex.IsMatch(body,
                    @"InvisibleTestWindow\s*\.\s*PrepareOffScreen\s*\(\s*" + Regex.Escape(use.Name) + @"\b"));

            if (use.InlineShown && !prepared)
            {
                yield return $"{use.Type} is constructed and shown inline without InvisibleTestWindow.Prepare";
                continue;
            }
            if (use.Name is null) continue;

            var name = Regex.Escape(use.Name);
            var shownBy = Regex.Matches(body, $@"(?<![\w.]){name}\s*\.\s*(?<api>{ApisFor(use.Type)})\b")
                .Select(m => m.Groups["api"].Value).ToList();
            var madeVisible = Regex.IsMatch(body, $@"(?<![\w.]){name}\s*\.\s*Visibility\s*=\s*Visibility\s*\.\s*Visible\b");
            var handed = OverlayContainerArgs.Matches(body)
                .Any(m => Regex.IsMatch(m.Groups["args"].Value, $@"(?<![\w.]){name}\b"));

            if (offScreen)
            {
                if (shownBy.Any(api => api != "ShowBackdrop") || madeVisible)
                    yield return $"{use.Type} '{use.Name}' was prepared with PrepareOffScreen, which only covers ShowBackdrop: " +
                                 "its Show() bypasses the off-screen PositionForShow seam";
                continue;
            }
            if (prepared) continue;
            if (shownBy.Count > 0 || madeVisible)
                yield return $"{use.Type} '{use.Name}' is shown ({(shownBy.Count > 0 ? shownBy[0] : "Visibility = Visible")}) without InvisibleTestWindow.Prepare";
            else if (handed)
                yield return $"{use.Type} '{use.Name}' is handed to an overlay container (production shows it) without InvisibleTestWindow.Prepare";
        }
    }

    /// <summary>Every real window a member constructs: its assigned name (when it has one) and whether the construction is itself wrapped.</summary>
    private static IEnumerable<WindowUse> WindowsConstructedIn(string body)
    {
        foreach (var statement in body.Split(';'))
        {
            foreach (Match m in RealConstruction.Matches(statement))
            {
                // LagMarkWindow / RestartNoticeWindow put themselves on screen ONLY through their `present:` callback;
                // a test that passes one (it measures and arranges, nothing more) never shows the window.
                if (Regex.IsMatch(statement[m.Index..], @"^new\s+\w+\s*\((?:[^()]|\([^()]*\))*\bpresent\s*:"))
                    continue;
                var before = statement[..m.Index];
                var wrapped = Regex.Match(before, @"InvisibleTestWindow\s*\.\s*(?<helper>Prepare\w*)\s*\(\s*$");
                var prepared = wrapped.Success;
                var offScreen = wrapped.Success && wrapped.Groups["helper"].Value == "PrepareOffScreen";

                // `name =` (or `Type name =` / `var name =`) directly before the construction or its wrapper
                var assigned = Regex.Match(before, @"(?<name>[A-Za-z_]\w*)\s*=\s*(?!=)(?:[\w.]*InvisibleTestWindow\s*\.\s*Prepare\w*\s*\(\s*)?$");
                var name = assigned.Success ? assigned.Groups["name"].Value : null;

                // `new Window(...).Show()` inline
                var inlineShown = Regex.IsMatch(statement[m.Index..],
                    $@"^new\s+\w+\s*(?:\([^()]*\))?\s*(?:\{{[^{{}}]*\}})?\s*(?:\))?\s*\.\s*(?:{ApisFor(m.Groups["type"].Value)})\b");
                yield return new WindowUse(m.Groups["type"].Value, name, prepared, offScreen, inlineShown);
            }
        }
    }

    private static bool CallsShowingApiAnywhere(string body) => ShowingCall.IsMatch(body);

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    internal readonly record struct Member(string Name, int Start, string Body);

    /// <summary>
    /// The members of every class in <paramref name="stripped"/>, nested classes included: a brace block whose
    /// header is not a type or namespace and whose parent is a type or namespace (methods, constructors, accessors),
    /// plus the <c>;</c>-terminated declarations at that level (expression-bodied members, field initializers).
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
                var parentIsContainer = stack.Count == 0 || stack.Peek().Container;
                if (parentIsContainer)
                {
                    var text = stripped[headerStart..(i + 1)];
                    if (text.Contains('=') || text.Contains("extern", StringComparison.Ordinal))
                        members.Add(new Member(MemberName(text), headerStart, text));
                }
                headerStart = i + 1;
            }
        }
        return members;
    }

    private static string MemberName(string header)
    {
        var m = Regex.Match(header, @"(\w+)\s*(?:<[^>]*>)?\s*\([^)]*\)\s*(?:=>|:[^{]*|\{|;|$)");
        if (m.Success) return m.Groups[1].Value;
        var field = Regex.Match(header, @"(\w+)\s*=");
        return field.Success ? field.Groups[1].Value : header.Trim().Split('\n').LastOrDefault()?.Trim() ?? "?";
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
