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
/// <b>Rule A, per CONSTRUCTION (the default).</b> Every real window a test constructs (<c>new XxxWindow(...)</c>, not a
/// Fake/Recording/Stub double) must go through <c>InvisibleTestWindow.Prepare</c> / <c>PrepareCloaked</c> /
/// <c>PrepareOffScreen</c> — wrapped around the construction, or applied to its name later in the member. It does not matter
/// how the window is later shown, passed around, aliased, put in an array or tuple, cast, or reached through <c>?.</c> /
/// <c>!.</c>: the construction itself is the unit. Exempt: a member behind an opt-in gate; an <see cref="AllowList"/> entry
/// (a full exemption, with a reason); a file in <see cref="HeadlessConstruction"/> (a test that builds windows only to
/// measure or inspect them: rule A is waived for the file, and the COARSE rule applies instead of rule B: such a file may not call
/// ANY showing API, write ANY <c>Visibility = Visible</c> or build ANY real overlay container, whatever the receiver); a window built with a headless
/// <c>present:</c> callback; and an <c>ActivationButtonWindow</c> whose <c>ShowAt</c> only positions and fades it.
/// The RUNTIME check (<c>RuntimeWindowMonitor</c>) is the backstop for everything the waived files might do.
/// </para>
///
/// <para>
/// <b>Rule B, per WINDOW (every file that is not headless-listed; the headless files get the coarse rule above, a superset).</b> In a test member, every real window the test constructs
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
        ["RuntimeWindowGuardTests.cs"] =
            "the probes of the runtime check show plain windows at -30000 (off every monitor) on purpose, to prove the check turns RED; nothing is visible",
        ["RuntimeOffScreenTagTests.cs"] =
            "the probes of the off-screen tag show plain windows at -30000 (off every monitor) on purpose, judged by a lenient monitor; nothing is visible",
        ["Localization/SettingsLiveCheckTests.cs#Open"] =
            "a helper reached only from tests that begin with LiveCheck.Require() (EYECTRL_LIVE_CHECK opt-in)",
        ["Diagnostics/SubPixel/ProbeRingOnScreenHarness.cs#RunWhenEnabled"] =
            "reached only after Run's EYECTRL_UI_TESTS gate returns; ProbeRingOnScreenGateTests pins that gate headless",
    };

    private const string MeasureOnly =
        "builds real windows only to measure, arrange, find elements in or read properties of (FindName, Measure, EnsureHandleAt, layout reads): " +
        "it never shows one nor hands one to an overlay container (rule B finds none) and the runtime check would catch it if it did";

    private const string KeyboardHost =
        "the KeyboardWindow's content is lifted into an off-screen HwndSource (WS_POPUP at -30000, never WS_VISIBLE); the window itself is never shown";

    private const string HandleOnlyProbe =
        "an EnsureHandle-only probe (SubPixel / ProbeRing harnesses): the HWND is created for geometry or capture reads, the window is never shown";

    private const string LiveCheckBuilder =
        "a builder reached only from live-check tests, which start with LiveCheck.Require() (EYECTRL_LIVE_CHECK opt-in)";

    /// <summary>
    /// <c>file#member</c> → why a headless-listed member may write <c>Visibility = Visible</c> (the coarse rule bans it): each one makes INNER
    /// ELEMENTS (panels, canvases, text blocks) of a window visible so the window can be MEASURED in its full state; the window itself is
    /// never shown. A stale entry (the member stopped writing Visible) fails <see cref="EveryVisibleWriteWaiver_StillWritesVisible_AndNamesAReason"/>.
    /// </summary>
    internal static readonly Dictionary<string, string> HeadlessVisibleWriteWaivers = new()
    {
        ["Localization/Fit/AppFitMeasurers.cs#SummaryCard"] = InnerElementsOfAMeasuredWindow,
        ["Localization/Fit/GeometryInvarianceTests.cs#CalibrationTarget"] = InnerElementsOfAMeasuredWindow,
        ["Localization/Fit/GermanGateTests.cs#CalibrationFailureLayout"] = InnerElementsOfAMeasuredWindow,
        ["Localization/Fit/GermanGateTests.cs#CalibrationButton"] = InnerElementsOfAMeasuredWindow,
    };

    private const string InnerElementsOfAMeasuredWindow =
        "sets Visibility.Visible on INNER ELEMENTS (panels, canvases, text) so the window can be measured in its full state; the window itself is never shown";

    /// <summary>
    /// The two product APIs that put a window on screen ONLY through a headless <c>present:</c> callback, and the window type each belongs to.
    /// In a headless file that names the type AND passes <c>present:</c>, calling the API on it shows nothing; the exemption covers only these two APIs.
    /// </summary>
    private static readonly Dictionary<string, string> PresentCallbackApis = new()
    {
        ["ShowButton"] = "LagMarkWindow",
        ["ShowNotice"] = "RestartNoticeWindow",
    };

    private static Dictionary<string, string> Group(string reason, params string[] files) => files.ToDictionary(f => f, _ => reason);

    /// <summary>
    /// Rule A waivers: <c>file</c> → why the test may CONSTRUCT real windows without preparing them. Rule B (a show, a hand-off to
    /// an overlay container) and the runtime check still apply to these files. A stale entry (the file no longer constructs
    /// an unprepared window) fails <see cref="EveryHeadlessEntry_StillConstructsAnUnpreparedWindow_AndNamesAReason"/>.
    /// </summary>
    internal static readonly Dictionary<string, string> HeadlessConstruction = new[]
    {
        Group(MeasureOnly,
            "CalibrationWindowVisualPinsTests.cs", "CornerRadiusClampTests.cs", "CursorRingRenderTests.cs", "Diagnostics/ProbeRingParityTests.cs",
            "GlyphSizeCouplingTests.cs", "InvisibleTestWindowTests.cs", "OverlayFontTests.cs", "ToolbarButtonVisualTests.cs",
            "ToolbarPausedTargetAlignmentTests.cs", "TestSupport/ArcLabelFit.cs",
            "Localization/Fit/AppFitMeasurers.cs", "Localization/Fit/CaregiverAlertGrowthTests.cs", "Localization/Fit/GeometryInvarianceTests.cs",
            "Localization/Fit/GermanGateTests.cs", "Localization/Fit/PreviewRowFit.cs", "Localization/Fit/PreviewRowFitTests.cs",
            "Localization/Fit/TrackStatusWidthTests.cs", "Localization/Review/ReviewSheetTests.cs", "Localization/Review/T15SheetTests.cs",
            "Localization/ShippedGermanStep3Tests.cs",
            "Overlays/ActionMenuPreviewCaptureLogTests.cs", "Overlays/ActionMenuPreviewUnderFillTests.cs", "Overlays/AdaptationPreviewWindowTests.cs",
            "Overlays/CrisisOverlayTextTests.cs", "Overlays/CursorRingQuantizationTests.cs", "Overlays/EdgeButtonBarCaptureTests.cs",
            "Overlays/EdgeButtonFitTests.cs", "Overlays/EyeCareBannerCueTests.cs", "Overlays/EyeCareBannerGeometryTests.cs",
            "Overlays/GlassBackdropHideButtonTests.cs", "Overlays/GlassBackdropMaterialPathTests.cs", "Overlays/GlassBackdropWindowTests.cs",
            "Overlays/LagMarkChromeTests.cs", "Overlays/LagMarkWindowTests.cs", "Overlays/ManualSelectionCaptureBeforeShowTests.cs",
            "Overlays/ManualSelectionCaptureFailureTests.cs", "Overlays/ManualSelectionCentreOnPointTests.cs",
            "Overlays/ManualSelectionGazeIndicatorWindowTests.cs", "Overlays/ManualSelectionMagnifierLogTests.cs",
            "Overlays/ManualSelectionRenderScaleTests.cs", "Overlays/ModesMenuFitTests.cs", "Overlays/NoteWindowNameTests.cs",
            "Overlays/OverlayClientOriginTests.cs", "Overlays/OverlayStaticTextTests.cs", "Overlays/RestartNoticePlacementTests.cs",
            "Overlays/RestartNoticeWindowTests.cs",
            "Overlays/ScrollOverlayWindowTests.cs", "Overlays/ToolbarBandRenderedButtonsTests.cs", "Overlays/TrackStatusWindowWiringTests.cs",
            "Services/MagnifierFitDpiTests.cs", "Services/MagnifierFitWiringTests.cs"),
        Group(KeyboardHost,
            "Keyboard/KeyboardBoxTargetingProbeTests.cs", "Keyboard/KeyboardCategoryGridExclusionTests.cs", "Keyboard/KeyboardE1BarAndGeometryTests.cs",
            "Keyboard/KeyboardE1PrivacyTests.cs", "Keyboard/KeyboardEmojiDwellFeedbackTests.cs", "Keyboard/KeyboardGateProbeTests.cs",
            "Keyboard/KeyboardNeverMovesTests.cs", "Keyboard/KeyboardPasswordBarTests.cs", "Keyboard/KeyboardPredictionBarStabilityTests.cs",
            "Keyboard/KeyboardPredictionClickTests.cs", "Keyboard/KeyboardPredictionExclusionTests.cs", "Keyboard/KeyboardPredictionRearmLatchTests.cs",
            "Keyboard/KeyboardSessionEndBarRefreshTests.cs", "Keyboard/KeyboardStaleSwapAnchorTests.cs",
            "Keyboard/SymbolCaptionFitTests.cs", "Overlays/KeyboardPredictionBarRefreshTests.cs"),
        Group(HandleOnlyProbe,
            "Diagnostics/SubPixel/ProbeRingOnScreenGateTests.cs", "Diagnostics/SubPixel/SubPixelProbeC5ExitTests.cs",
            "Diagnostics/SubPixel/SubPixelProbeContrastTests.cs", "Diagnostics/SubPixel/SubPixelProbeCornerLabelTests.cs",
            "Diagnostics/SubPixel/SubPixelProbeLabelTests.cs", "Diagnostics/SubPixel/SubPixelProbeUsabilityTests.cs",
            "Diagnostics/SubPixel/SubPixelProbeWindowTests.cs"),
        Group(LiveCheckBuilder, "Localization/Step3LiveCheckTests.cs"),
    }.SelectMany(d => d).ToDictionary(p => p.Key, p => p.Value);

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

    [Fact]
    public void EveryHeadlessEntry_StillConstructsAnUnpreparedWindow_AndNamesAReason()
    {
        var sources = TestSources().ToDictionary(t => t.Relative, t => StripCommentsAndStrings(t.Text));
        foreach (var (file, reason) in HeadlessConstruction)
        {
            Check.WithCustomMessage($"HeadlessConstruction entry '{file}' needs a reason.").That(reason.Length).IsStrictlyGreaterThan(20);
            var match = sources.Where(t => t.Key.EndsWith(file, StringComparison.Ordinal)).ToList();
            Check.WithCustomMessage($"HeadlessConstruction entry '{file}' matches no test file (renamed or deleted?).").That(match.Count).IsEqualTo(1);
            Check.WithCustomMessage($"HeadlessConstruction entry '{file}' no longer constructs an unprepared real window: remove it.")
                .That(Members(match[0].Value).Any(m => MemberProblems(m.Body, ruleAWaived: false).Any(p => p.Contains("constructed without")))).IsTrue();
        }
    }

    [Fact]
    public void EveryVisibleWriteWaiver_StillWritesVisible_AndNamesAReason()
    {
        var sources = TestSources().ToDictionary(t => t.Relative, t => StripCommentsAndStrings(t.Text));
        foreach (var (key, reason) in HeadlessVisibleWriteWaivers)
        {
            Check.WithCustomMessage($"waiver '{key}' needs a reason.").That(reason.Length).IsStrictlyGreaterThan(20);
            var parts = key.Split('#');
            Check.WithCustomMessage($"waiver '{key}' names a file that is not headless-listed: it needs no waiver.")
                .That(HeadlessConstruction.Keys.Any(h => parts[0].EndsWith(h, StringComparison.Ordinal))).IsTrue();
            var match = sources.Where(s => s.Key.EndsWith(parts[0], StringComparison.Ordinal)).ToList();
            Check.WithCustomMessage($"waiver '{key}' matches no test file (stale?).").That(match.Count).IsEqualTo(1);
            var members = Members(match[0].Value).Where(m => m.Name == parts[1]).ToList();
            Check.WithCustomMessage($"waiver '{key}' matches no member (renamed?).").That(members.Count).IsStrictlyGreaterThan(0);
            Check.WithCustomMessage($"waiver '{key}' no longer writes Visibility = Visible: remove it.")
                .That(members.Any(m => AnyVisibleWrite.IsMatch(m.Body))).IsTrue();
        }
    }

    [Fact]
    public void TheShowButtonAndShowNoticeExemption_CoversOnlyAHeadlessFileThatPassesPresent_AndOnlyThoseTwoApis()
    {
        const string headless = "Overlays/LagMarkWindowTests.cs";
        const string notice = "Overlays/RestartNoticeWindowTests.cs";
        const string present = "private static LagMarkWindow Headless() =>\n        new(dwellMs: 25, messenger, keepClear, present: b => { });\n";
        const string presentNotice = "private static RestartNoticeWindow Headless() =>\n        new(dwellMs: 25, messenger, keepClear, present: c => { });\n";
        static string File(string members) => "namespace T;\npublic class C\n{\n" + members + "\n}\n";
        static string Test(string call, string helper = "Headless") => "    public void M()\n    {\n        var b = " + helper + "();\n        " + call + "\n    }\n";

        // the shipped shape: present: + the type named in the file + ShowButton / ShowNotice => clean
        Check.That(FindViolations(headless, File(present + Test("b.ShowButton();")))).IsEmpty();
        Check.That(FindViolations(notice, File(presentNotice + Test("b.ShowNotice(\"t\", \"ok\", s);")))).IsEmpty();

        // RED: a headless file with `present:` that also calls .Show() (or any other showing API) still fails
        foreach (var other in new[] { "b.Show();", "b.ShowDialog();", "b.ShowAtRest();", "((Window)b).Show();" })
            Check.WithCustomMessage(other).That(FindViolations(headless, File(present + Test(other)))).Not.IsEmpty();
        Check.That(FindViolations(headless, File(present + Test("b.ShowButton();\n        b.Show();")))).Not.IsEmpty();

        // RED: a headless file WITHOUT `present:` that calls ShowNotice (or ShowButton) still fails
        Check.That(FindViolations(notice, File(Test("b.ShowNotice(\"t\", \"ok\", s);")))).Not.IsEmpty();
        Check.That(FindViolations(headless, File(Test("b.ShowButton();")))).Not.IsEmpty();

        // RED: a file that NAMES the window type but builds it without `present:` (a real window) gets no exemption
        Check.That(FindViolations(notice, File("private static RestartNoticeWindow Real() => new(dwellMs: 25, messenger, keepClear);\n" + Test("b.ShowNotice(\"t\", \"ok\", s);", "Real"))))
            .Not.IsEmpty();
        Check.That(FindViolations(headless, File("private static LagMarkWindow Real() => new(dwellMs: 25, messenger, keepClear);\n" + Test("b.ShowButton();", "Real"))))
            .Not.IsEmpty();

        // RED: `present:` for ANOTHER window type does not exempt the API of this one
        Check.That(FindViolations(headless, File(present + Test("b.ShowNotice(\"t\", \"ok\", s);")))).Not.IsEmpty();
        Check.That(FindViolations(notice, File(presentNotice + Test("b.ShowButton();")))).Not.IsEmpty();
    }

    [Fact]
    public void AVisibleWriteWaiver_CoversOnlyItsOwnMember_NotTheRestOfTheFile()
    {
        const string file = "Localization/Fit/GermanGateTests.cs";
        const string write = "        window.CompletionCard.Visibility = System.Windows.Visibility.Visible;\n";
        static string Source(string member) => "namespace T;\npublic class C\n{\n    public void " + member + "()\n    {\n" + write + "    }\n}\n";

        Check.WithCustomMessage("the waived member").That(FindViolations(file, Source("CalibrationButton"))).IsEmpty();
        Check.WithCustomMessage("another member of the same file").That(FindViolations(file, Source("SomeOtherMember"))).HasSize(1);
        Check.WithCustomMessage("the waived member's name in ANOTHER headless file").That(FindViolations("Overlays/ModesMenuFitTests.cs", Source("CalibrationButton"))).HasSize(1);
        // a waiver is for the Visible write only: the same member calling a showing API is still a violation
        Check.That(FindViolations(file, "namespace T;\npublic class C\n{\n    public void CalibrationButton()\n    {\n" + write + "        window.Show();\n    }\n}\n"))
            .HasSize(1);
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
                    var w = new ActivationButtonWindow();
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
    public void EveryReview02Shape_IsAViolation_BecauseTheConstructionItselfIsUnprepared()
    {
        // test-windows-invisible review 02 (R2-1, R2-2): shapes that defeat "follow the window by its name". Under rule A
        // the construction is the unit, so none of them gets past: each member builds a window without preparing it.
        var shapes = new Dictionary<string, string>
        {
            ["alias"] = "var w = new ToolbarWindow();\n var w2 = w;\n w2.Show();",
            ["array"] = "var ws = new[] { new ToolbarWindow() };\n ws[0].Show();",
            ["list"] = "var ws = new List<Window> { new ToolbarWindow() };\n ws[0].Show();",
            ["tuple"] = "var (a, b) = (new ToolbarWindow(), 1);\n a.Show();",
            ["ternary"] = "var w = flag ? new ToolbarWindow() : null;\n w.Show();",
            ["null-conditional"] = "var w = new ToolbarWindow();\n w?.Show();",
            ["null-forgiving"] = "var w = new ToolbarWindow();\n w!.Show();",
            ["cast"] = "var w = new ToolbarWindow();\n ((Window)w).Show();",
            ["passed to a sibling method"] = "var w = new ToolbarWindow();\n Open(w);",
            ["container not named *Overlays"] = "var w = new ToolbarWindow();\n var h = new Harness(w);",
            ["qualified Visibility.Visible"] = "var w = new ToolbarWindow();\n w.Visibility = System.Windows.Visibility.Visible;",
            ["SetCurrentValue"] = "var w = new ToolbarWindow();\n w.SetCurrentValue(Window.VisibilityProperty, System.Windows.Visibility.Visible);",
            ["unnamed expression"] = "Open(new ToolbarWindow());",
            ["present: seam then a plain Show()"] = "var w = new LagMarkWindow(dwellMs: 1, messenger, keepClear: null, present: b => { });\n w.Show();",
        };
        foreach (var (name, body) in shapes)
            Check.WithCustomMessage($"shape '{name}' got past the guard").That(Scan($"    public void S()\n    {{\n {body}\n    }}")).Not.IsEmpty();

        // the same members with the construction prepared are clean (the shapes themselves are not the problem)
        foreach (var (name, body) in shapes.Where(s => s.Value.Contains("new ToolbarWindow()")))
            Check.WithCustomMessage($"prepared shape '{name}' must be accepted")
                .That(Scan($"    public void S()\n    {{\n {body.Replace("new ToolbarWindow()", "InvisibleTestWindow.Prepare(new ToolbarWindow())")}\n    }}"))
                .IsEmpty();
    }

    [Fact]
    public void EveryWayOfMakingAConstructedWindowVisible_IsAShow_EvenWhereRuleAIsWaived()
    {
        // In a headless-listed file rule A is waived, so the coarse rule stands between a test and a shown window: each of these must still be seen.
        const string template = "namespace T;\npublic class C\n{\n    public void M()\n    {\n        var w = new ModesMenuWindow();\n        STATEMENT\n    }\n}\n";
        foreach (var statement in new[]
                 {
                     "w.Show();", "w.ShowDialog();", "Action a = w.Show;", "w.Visibility = Visibility.Visible;",
                     "w.Visibility = System.Windows.Visibility.Visible;",
                     "w.SetCurrentValue(Window.VisibilityProperty, Visibility.Visible);",
                     "w.SetCurrentValue(System.Windows.UIElement.VisibilityProperty, System.Windows.Visibility.Visible);",
                     "w.SetValue(UIElement.VisibilityProperty, System.Windows.Visibility.Visible);",
                 })
            Check.WithCustomMessage(statement)
                .That(FindViolations("Overlays/ModesMenuFitTests.cs", template.Replace("STATEMENT", statement))).HasSize(1);
        Check.That(FindViolations("Overlays/ModesMenuFitTests.cs", template.Replace("STATEMENT", "w.Measure(s);"))).IsEmpty();
    }

    [Fact]
    public void InAHeadlessListedFile_AnyShow_AnyVisibleWrite_AndAnyOverlayContainer_IsAViolation_WhateverTheReceiver()
    {
        // L3 (#258 review 01): the headless files never show a window, so the coarse rule bans the showing calls by NAME of the API only,
        // not by the window it is called on. Every review-02 shape that defeats "follow the window by its name" must be seen here, even
        // where no window is constructed in the member at all (a helper's return value, a field, a parameter, a list element).
        const string headless = "Overlays/ModesMenuFitTests.cs";
        const string template = "namespace T;\npublic class C\n{\n    public void M()\n    {\n        STATEMENT\n    }\n}\n";
        var shapes = new Dictionary<string, string>
        {
            ["plain"] = "var w = new ModesMenuWindow();\n w.Show();",
            ["alias"] = "var w = new ModesMenuWindow();\n var w2 = w;\n w2.Show();",
            ["array"] = "var ws = new[] { new ModesMenuWindow() };\n ws[0].Show();",
            ["tuple"] = "var (a, b) = (new ModesMenuWindow(), 1);\n a.ShowDialog();",
            ["ternary"] = "var w = flag ? new ModesMenuWindow() : null;\n w.Show();",
            ["null-conditional"] = "var w = new ModesMenuWindow();\n w?.Show();",
            ["null-forgiving"] = "var w = new ModesMenuWindow();\n w!.ShowAtRest();",
            ["cast"] = "var w = new ModesMenuWindow();\n ((Window)w).Show();",
            ["helper's return value"] = "Open().Show();",
            ["field"] = "_window.ShowThenSlideIn();",
            ["method group"] = "Action a = w.Show;",
            ["product method on a parameter"] = "overlay.ShowButton();",
            ["Visible on any receiver"] = "Get().Visibility = Visibility.Visible;",
            ["qualified Visible"] = "x.y.Visibility = System.Windows.Visibility.Visible;",
            ["SetCurrentValue on any receiver"] = "Get().SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);",
            ["SetValue qualified"] = "ws[0].SetValue(System.Windows.UIElement.VisibilityProperty, System.Windows.Visibility.Visible);",
            ["real overlay container"] = "var o = new ToolbarOverlays(settings, w);",
            ["real overlay manager"] = "var m = new OverlayManager(services);",
            ["container with a brace initialiser"] = "var o = new ToolbarOverlays { Toolbar = w };",
        };
        foreach (var (name, body) in shapes)
            Check.WithCustomMessage($"shape '{name}' got past the coarse rule of a headless-only file")
                .That(FindViolations(headless, template.Replace("STATEMENT", body))).Not.IsEmpty();

        // the same shapes in a file that is NOT headless-listed are judged by rules A and B as before (the coarse rule is only for the listed files)
        Check.That(FindViolations("Overlays/NotListedTests.cs", template.Replace("STATEMENT", "Get().Show();"))).IsEmpty();

        // what a headless file legitimately does stays clean: measure, arrange, find elements, read geometry, fakes and recordings
        foreach (var fine in new[]
                 {
                     "var w = new ModesMenuWindow();\n w.Measure(s);\n w.Arrange(r);\n w.UpdateLayout();",
                     "var h = new WindowInteropHelper(w).EnsureHandle();",
                     "var fake = new FakeOverlays(toolbar);\n var n = fake.Count;",
                     "var m = new FakeOverlayManager(w);",
                     "border.Visibility = Visibility.Collapsed;",
                     "var shown = w.IsVisible;\n var title = w.Title;",
                 })
            Check.WithCustomMessage(fine).That(FindViolations(headless, template.Replace("STATEMENT", fine))).IsEmpty();
    }

    [Fact]
    public void ARealOverlayContainerBuiltInANonHeadlessFile_IsStillJudgedByRuleB_NotTheCoarseRule()
    {
        // outside the headless list nothing changed: a container is a violation only when it is handed an unprepared real window
        Check.That(Scan("    public void S()\n    {\n        var o = new ToolbarOverlays(settings);\n    }")).IsEmpty();
        Check.That(Scan("    public void S()\n    {\n        var w = InvisibleTestWindow.Prepare(new ToolbarWindow());\n        var o = new ToolbarOverlays(w);\n    }")).IsEmpty();
    }

    [Fact]
    public void ReasonGroups_MatchWhatTheFilesDo_RestartNoticePlacementIsMeasureOnly()
    {
        // L4 (#258 review 01): its RestartNoticeWindow cards are only positioned and measured, so the MeasureOnly reason is the honest one
        var reason = HeadlessConstruction.Single(p => p.Key.EndsWith("Overlays/RestartNoticePlacementTests.cs", StringComparison.Ordinal)).Value;
        Check.That(reason).Contains("only to measure");
        Check.That(reason).Not.Contains("HwndSource");
    }

    [Fact]
    public void AMemberThatOnlyConstructsAnUnpreparedWindow_IsAViolation_UnlessTheFileIsHeadlessListed()
    {
        const string body = "    public void M()\n    {\n        var w = new ToolbarWindow();\n        w.Measure(s);\n    }";
        Check.That(Scan(body)).HasSize(1);
        Check.That(Scan(body).Single()).Contains("constructed without");
        Check.That(FindViolations("Overlays/ModesMenuFitTests.cs", "namespace T;\npublic class C\n{\n" + body + "\n}\n")).IsEmpty();
        // the waiver is for construction only: a show in a headless-listed file is still a violation (the coarse rule)
        Check.That(FindViolations("Overlays/ModesMenuFitTests.cs",
            "namespace T;\npublic class C\n{\n    public void M()\n    {\n        var w = new ModesMenuWindow();\n        w.Show();\n    }\n}\n")).HasSize(1);
    }

    // ---- the #259 review gaps T1-T4 (synthetic sources; nothing is shown) ------------------------------------

    private const string LagMarkHeadless = "Overlays/LagMarkWindowTests.cs";
    private const string RestartHeadless = "Overlays/RestartNoticeWindowTests.cs";

    private static string HeadlessFile(string members) => "namespace T;\npublic class C\n{\n" + members + "\n}\n";

    private static string TestMember(string body) => "    public void M()\n    {\n" + body + "\n    }\n";

    private const string ExpressionHelper =
        "    private static LagMarkWindow HeadlessButton(IMessenger? messenger = null, Func<IReadOnlyList<Rect>>? keepClear = null) =>\n" +
        "        new(dwellMs: 25, messenger, keepClear, present: button => { button.Measure(s); });\n";

    private const string BlockHelper =
        "    private static LagMarkWindow BlockButton()\n    {\n        return new LagMarkWindow(dwellMs: 25, present: b => { });\n    }\n";

    [Fact]
    public void ThePresentExemption_IsPerReceiver_NotPerFile()
    {
        // T1 (#259 review): ShowButton / ShowNotice are exempt only on a receiver built through a present: construction (a variable assigned from
        // such a `new`, or from a same-file helper whose body passes present:). A real window of the same type in the same file is a real show.
        foreach (var clean in new[]
                 {
                     "var button = HeadlessButton();\n button.ShowButton();",
                     "var button = HeadlessButton(messenger, () => chrome);\n button.ShowButton();",
                     "var button = BlockButton();\n button.ShowButton();",
                     "var b = new LagMarkWindow(dwellMs: 25, null, null, present: x => { });\n b.ShowButton();",
                 })
            Check.WithCustomMessage(clean).That(FindViolations(LagMarkHeadless, HeadlessFile(ExpressionHelper + BlockHelper + TestMember(clean)))).IsEmpty();

        foreach (var (name, body) in new Dictionary<string, string>
                 {
                     ["a real window beside the present helper"] = "var real = new LagMarkWindow(dwellMs: 700);\n real.ShowButton();",
                     ["a real window, a present one also exists"] = "var button = HeadlessButton();\n var real = new LagMarkWindow(dwellMs: 700);\n button.ShowButton();\n real.ShowButton();",
                     ["an inline helper call"] = "HeadlessButton().ShowButton();",
                     ["an unassigned name (a parameter or a field)"] = "button.ShowButton();",
                     ["a variable reassigned to a real window"] = "var button = HeadlessButton();\n button = new LagMarkWindow(dwellMs: 700);\n button.ShowButton();",
                     ["a member access receiver"] = "var holder = HeadlessButton();\n holder.Inner.ShowButton();",
                     ["a cast receiver"] = "var button = HeadlessButton();\n ((LagMarkWindow)button).ShowButton();",
                     ["a null-conditional receiver"] = "var button = HeadlessButton();\n button?.ShowButton();",
                     ["a helper that does NOT pass present:"] = "var button = RealButton();\n button.ShowButton();",
                     ["Show() on a present receiver"] = "var button = HeadlessButton();\n button.Show();",
                     ["ShowNotice on a LagMark receiver"] = "var button = HeadlessButton();\n button.ShowNotice(\"t\", \"ok\", s);",
                 })
            Check.WithCustomMessage(name).That(FindViolations(LagMarkHeadless, HeadlessFile(
                ExpressionHelper + "    private static LagMarkWindow RealButton() => new(dwellMs: 700);\n" + TestMember(body)))).Not.IsEmpty();

        // the same for the notice
        const string noticeHelper = "    private static RestartNoticeWindow HeadlessNotice(IMessenger m) =>\n        new(dwellMs: 25, m, null, present: c => { });\n";
        Check.That(FindViolations(RestartHeadless, HeadlessFile(noticeHelper + TestMember("var card = HeadlessNotice(m);\n card.ShowNotice(\"t\", \"ok\", s);")))).IsEmpty();
        Check.That(FindViolations(RestartHeadless, HeadlessFile(noticeHelper + TestMember("var real = new RestartNoticeWindow();\n real.ShowNotice(\"t\", \"ok\", s);")))).Not.IsEmpty();
    }

    [Fact]
    public void AConditionalVisibleWrite_IsAVisibleWrite_InAHeadlessFile_AndForANamedWindow()
    {
        // T2: `x.Visibility = c ? Visibility.Visible : Collapsed` (and ?? and a switch expression) makes something Visible.
        const string headless = "Overlays/ModesMenuFitTests.cs";
        foreach (var statement in new[]
                 {
                     "w.Visibility = c ? Visibility.Visible : Visibility.Collapsed;",
                     "w.Visibility = c ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;",
                     "w.Visibility = a ?? Visibility.Visible;",
                     "w.Visibility = k switch { 1 => Visibility.Visible, _ => Visibility.Collapsed };",
                     "w.Visibility = shown.Flag || other ? Visibility.Visible : Visibility.Hidden;",
                     "w.SetValue(UIElement.VisibilityProperty, c ? Visibility.Visible : Visibility.Collapsed);",
                     "w.SetCurrentValue(Window.VisibilityProperty, a ?? Visibility.Visible);",
                     "w.Visibility = (Visibility)0;",
                     "w.Visibility = (System.Windows.Visibility)0;",
                     "w.Visibility = (Visibility)(0);",
                     "w.Visibility = default(Visibility);",
                     "w.Visibility = c ? (Visibility)0 : Visibility.Collapsed;",
                 })
        {
            Check.WithCustomMessage("headless: " + statement)
                .That(FindViolations(headless, HeadlessFile(TestMember("var w = Get();\n " + statement)))).Not.IsEmpty();
        }
        foreach (var statement in new[]
                 {
                     "w.Visibility = c ? Visibility.Visible : Visibility.Collapsed;", "w.Visibility = a ?? Visibility.Visible;",
                     "w.Visibility = k switch { 1 => Visibility.Visible, _ => Visibility.Collapsed };", "w.Visibility = (Visibility)0;",
                     "w.SetValue(UIElement.VisibilityProperty, c ? Visibility.Visible : Visibility.Collapsed);", "w.Visibility = default(Visibility);",
                 })
            Check.WithCustomMessage("named, not prepared: " + statement)
                .That(Scan(TestMember("var w = new ToolbarWindow();\n " + statement))).Not.IsEmpty();

        // The named-window rule (B) on its own: a window prepared with PrepareOffScreen is covered only for ShowBackdrop, so a conditional write
        // to its visibility is the ONLY thing that can flag it (rule A is satisfied by the preparation).
        foreach (var statement in new[]
                 {
                     "w.Visibility = c ? Visibility.Visible : Visibility.Collapsed;", "w.Visibility = a ?? Visibility.Visible;",
                     "w.Visibility = k switch { 1 => Visibility.Visible, _ => Visibility.Collapsed };", "w.Visibility = (Visibility)0;",
                     "w.SetValue(UIElement.VisibilityProperty, c ? Visibility.Visible : Visibility.Collapsed);",
                     "w.SetCurrentValue(Window.VisibilityProperty, a ?? Visibility.Visible);", "w.Visibility = default(Visibility);",
                 })
            Check.WithCustomMessage("PrepareOffScreen window: " + statement)
                .That(Scan(TestMember("var w = InvisibleTestWindow.PrepareOffScreen(new GlassBackdropWindow());\n " + statement))).Not.IsEmpty();
        Check.That(Scan(TestMember("var w = InvisibleTestWindow.PrepareOffScreen(new GlassBackdropWindow());\n w.Visibility = Visibility.Collapsed;"))).IsEmpty();

        // what is NOT a Visible write stays clean
        foreach (var fine in new[]
                 {
                     "w.Visibility = Visibility.Collapsed;", "w.Visibility = c ? Visibility.Collapsed : Visibility.Hidden;",
                     "var v = w.Visibility == Visibility.Visible;", "if (w.Visibility != Visibility.Visible) { }", "w.Visibility = (Visibility)1;",
                 })
            Check.WithCustomMessage(fine).That(FindViolations(headless, HeadlessFile(TestMember("var w = Get();\n " + fine)))).IsEmpty();
    }

    [Fact]
    public void AVisibleWriteWaiver_ExcusesOnlyInnerElementReceivers_NeverTheWindowItself()
    {
        // T3: the waived member of GermanGateTests writes inner elements; the same member writing the window's own visibility is still a violation.
        const string file = "Localization/Fit/GermanGateTests.cs";
        static List<string> InMember(string statement) => FindViolations(file, HeadlessFile(
            "    public void CalibrationButton()\n    {\n        var window = new CalibrationWindow();\n        " + statement + "\n    }\n"));

        foreach (var inner in new[]
                 {
                     "window.CompletionCard.Visibility = System.Windows.Visibility.Visible;",
                     "window.RetryPanel.Visibility = c ? Visibility.Visible : Visibility.Collapsed;",
                     "window.Backdrop.Child.Visibility = Visibility.Visible;",
                     "window.Backdrop.SetValue(UIElement.VisibilityProperty, Visibility.Visible);",
                     "this.CompletionCard.Visibility = Visibility.Visible;",
                 })
            Check.WithCustomMessage("inner element: " + inner).That(InMember(inner)).IsEmpty();

        foreach (var own in new[]
                 {
                     "window.Visibility = Visibility.Visible;",
                     "window.Visibility = c ? Visibility.Visible : Visibility.Collapsed;",
                     "Visibility = Visibility.Visible;",
                     "this.Visibility = Visibility.Visible;",
                     "window.SetValue(UIElement.VisibilityProperty, Visibility.Visible);",
                     "window.SetCurrentValue(Window.VisibilityProperty, Visibility.Visible);",
                     "SetValue(UIElement.VisibilityProperty, Visibility.Visible);",
                     "((Window)window).Visibility = Visibility.Visible;",
                     "Get().Visibility = Visibility.Visible;",
                     "windows[0].Visibility = Visibility.Visible;",
                     "window.Visibility = (Visibility)0;",
                 })
            Check.WithCustomMessage("the window itself: " + own).That(InMember(own)).Not.IsEmpty();

        // a mix: the inner write is excused, the window's own write is not
        Check.That(InMember("window.CompletionCard.Visibility = Visibility.Visible;\n        window.Visibility = Visibility.Visible;")).HasSize(1);
    }

    [Fact]
    public void AnOffScreenPreparedWindow_ShownThroughACastOrANullConditional_IsAViolation()
    {
        // R1 (#260 review): PrepareOffScreen covers ShowBackdrop only (it goes through the PositionForShow seam); any other show lands on a monitor.
        // The receiver may be cast, `?.`, `!.` or parenthesised: rule B follows the window through all of them.
        const string prepared = "var b = InvisibleTestWindow.PrepareOffScreen(new GlassBackdropWindow());\n ";
        foreach (var statement in new[]
                 {
                     "b.Show();", "((Window)b).Show();", "b?.Show();", "b!.Show();", "(b as Window).Show();", "((GlassBackdropWindow)b).Show();",
                     "((Window)b)?.ShowDialog();", "(b).Show();", "((Window)b)!.Show();", "(b as Window)?.Show();",
                     "((Window)b).Visibility = Visibility.Visible;", "(b as Window).Visibility = c ? Visibility.Visible : Visibility.Collapsed;",
                     "((UIElement)b).SetValue(UIElement.VisibilityProperty, Visibility.Visible);",
                     "((Window)b).SetCurrentValue(Window.VisibilityProperty, Visibility.Visible);",
                 })
            Check.WithCustomMessage(statement).That(Scan(TestMember(prepared + statement))).Not.IsEmpty();

        // the seam-honouring show, and everything that is not a show, stay clean (also through a cast or `?.`)
        foreach (var fine in new[]
                 {
                     "b.ShowBackdrop(1, 2);", "((GlassBackdropWindow)b).ShowBackdrop(1, 2);", "b?.ShowBackdrop(1, 2);", "(b as GlassBackdropWindow).ShowBackdrop(1, 2);",
                     "b.Measure(s);", "((Window)b).Measure(s);", "b?.Close();", "((Window)b).Visibility = Visibility.Collapsed;",
                 })
            Check.WithCustomMessage(fine).That(Scan(TestMember(prepared + fine))).IsEmpty();

        // a receiver that merely ENDS in the name is another variable
        Check.That(Scan(TestMember(prepared + "other.b.Measure(s);\n abb.Show();"))).IsEmpty();
    }

    [Fact]
    public void AWsVisibleConstant_IsFlaggedOnlyWhenItLooksLikeAWindowStyleOrFeedsAWindowStyle()
    {
        // R2 (#260 review): a sentinel or a tick count may have the 0x10000000 bit without being a window style
        foreach (var fine in new[]
                 {
                     "    private const int Max = 0x7FFFFFFF;\n", "    private const int TimeoutTicks = 300000000;\n",
                     "    private static readonly uint Sentinel = 0xFFFFFFFF;\n", "    private const int Mask = 0x10000000;\n",
                     "    private const int WS_POPUP = unchecked((int)0x80000000);\n", "    private const int WS_EX_NOACTIVATE = 0x08000000;\n",
                 })
            Check.WithCustomMessage(fine).That(FindViolations("Overlays/NotListedTests.cs", HeadlessFile(fine))).IsEmpty();

        // a constant that LOOKS like a window style and carries the bit is flagged on its own
        foreach (var bad in new[]
                 {
                     "    private const int WS_VISIBLE = 0x10000000;\n", "    private const int WsPopupVisible = unchecked((int)0x90000000);\n",
                     "    private static readonly int ProbeStyle = unchecked((int)0x94000000);\n", "    private const uint BackdropWindowStyle = 0x90000000;\n",
                 })
            Check.WithCustomMessage(bad).That(FindViolations("Overlays/NotListedTests.cs", HeadlessFile(bad))).Not.IsEmpty();

        // any constant (whatever its name) that FEEDS a WindowStyle assignment is judged by that use
        const string feeds = "    private const int Flags = unchecked((int)0x90000000);\n" +
                             "    public void M()\n    {\n        var p = new HwndSourceParameters(\"p\") { WindowStyle = Flags, Width = 1 };\n    }\n";
        Check.That(FindViolations("Overlays/NotListedTests.cs", HeadlessFile(feeds))).HasSize(1);
        Check.That(FindViolations("Overlays/NotListedTests.cs", HeadlessFile(feeds.Replace("WindowStyle = Flags", "WindowStyle = Flags | WsPopup")))).HasSize(1);
        // ... and the same constant that feeds nothing is not a violation
        Check.That(FindViolations("Overlays/NotListedTests.cs", HeadlessFile(
            "    private const int Flags = unchecked((int)0x90000000);\n    public void M()\n    {\n        var x = Flags;\n    }\n"))).IsEmpty();
        // a clean style stays clean
        Check.That(FindViolations("Overlays/NotListedTests.cs", HeadlessFile(
            "    private const int WsPopup = unchecked((int)0x80000000);\n    public void M()\n    {\n        var p = new HwndSourceParameters(\"p\") { WindowStyle = WsPopup };\n    }\n")))
            .IsEmpty();
    }

    [Fact]
    public void AnHwndSourceWithWsVisible_IsAViolation_ByNameOrByValue()
    {
        // T4: WS_VISIBLE (0x10000000) in an HwndSource's WindowStyle paints it at once. The probes use WS_POPUP only (0x80000000).
        foreach (var style in new[]
                 {
                     "WsPopup | WsVisible", "WS_POPUP | WS_VISIBLE", "unchecked((int)0x90000000)", "0x10000000", "unchecked((int)0x80000000) | 0x10000000",
                     "268435456", "0x94000000", "NativeMethods.WS_VISIBLE", "unchecked((int)(0x80000000 | 0x10000000))",
                 })
            foreach (var file in new[] { "Keyboard/KeyboardGateProbeTests.cs", "Overlays/NotListedTests.cs" })
                Check.WithCustomMessage($"{file}: {style}").That(FindViolations(file, HeadlessFile(TestMember(
                    "var source = new HwndSource(new HwndSourceParameters(\"p\") { WindowStyle = " + style + ", Width = 1, Height = 1 });")))).Not.IsEmpty();

        // the form with a separate assignment, and an int constant carrying the bit
        Check.That(FindViolations("Overlays/NotListedTests.cs", HeadlessFile(TestMember(
            "var p = new HwndSourceParameters(\"p\");\n p.WindowStyle = unchecked((int)0x90000000);")))).Not.IsEmpty();
        Check.That(FindViolations("Overlays/NotListedTests.cs", HeadlessFile("    private const int Style = unchecked((int)0x90000000);\n"))).Not.IsEmpty();

        // the shipped shape stays clean: WS_POPUP only, and the WPF WindowStyle enum
        Check.That(FindViolations("Keyboard/KeyboardGateProbeTests.cs", HeadlessFile(TestMember(
            "var source = new HwndSource(new HwndSourceParameters(\"p\") { WindowStyle = WsPopup, PositionX = -30000, PositionY = -30000, Width = 1, Height = 1 });")
            + "    private const int WsPopup = unchecked((int)0x80000000);\n"))).IsEmpty();
        Check.That(Scan(TestMember("var w = InvisibleTestWindow.Prepare(new ToolbarWindow());\n w.WindowStyle = WindowStyle.None;"))).IsEmpty();
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

    // A window built with a headless `present:` callback (LagMarkWindow, RestartNoticeWindow) is put on screen ONLY through
    // that callback; ShowButton / ShowNotice go through it. Every other showing API on it (a plain Show()) is a real show.
    private static readonly string ApiAlternationWithoutPresentApis =
        string.Join("|", ShowingApis.Where(a => a is not ("ShowButton" or "ShowNotice")));

    private static string ApisFor(string windowType, bool presentSeam = false) =>
        presentSeam ? ApiAlternationWithoutPresentApis
        : windowType == "ActivationButtonWindow" ? ApiAlternationWithoutShowAt
        : ApiAlternation;

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
        var ruleAWaived = HeadlessConstruction.Keys.Any(key => relative.EndsWith(key, StringComparison.Ordinal));
        var presentSeams = ruleAWaived ? FindPresentSeams(stripped) : NoPresentSeams;
        var wsVisibleConstants = FindWsVisibleConstants(stripped);

        foreach (var member in Members(stripped))
        {
            if (AllowList.ContainsKey($"{relative}#{member.Name}")) continue;
            if (OptInGateRegexes.Any(g => g.IsMatch(member.Body))) continue;
            var visibleWriteWaived = HeadlessVisibleWriteWaivers.ContainsKey($"{relative}#{member.Name}");
            foreach (var problem in MemberProblems(member.Body, ruleAWaived, presentSeams, visibleWriteWaived, wsVisibleConstants))
                violations.Add($"{relative}:{LineOf(stripped, member.Start)} {member.Name}: {problem}");
        }
        return violations;
    }

    private sealed record WindowUse(string Type, string? Name, bool Prepared, bool OffScreen, bool InlineShown, bool PresentSeam);

    // The coarse rule bans any `.Show` / `.ShowDialog` / … (a call or a method group) on whatever expression: `ws[0]`, `(Window)w`, `w?`, `Open(x)`, `Get().`

    private static readonly Regex PresentSeamArgument = new(@"\bpresent\s*:", RegexOptions.Compiled);

    /// <summary>
    /// What a headless file builds through a <c>present:</c> callback: the same-file HELPERS whose body passes <c>present:</c> to a <c>new</c>
    /// (expression- or block-bodied), and the window TYPES of <see cref="PresentCallbackApis"/> the file names while passing <c>present:</c>.
    /// </summary>
    private sealed record PresentSeams(HashSet<string> Helpers, HashSet<string> Types);

    private static readonly PresentSeams NoPresentSeams = new(new HashSet<string>(), new HashSet<string>());

    private static readonly Regex[] PresentHelperDeclarations =
    [
        new(@"(?<name>[A-Za-z_]\w*)\s*\([^;{}]*?\)\s*=>\s*new\b[^;]*?\bpresent\s*:", RegexOptions.Compiled),
        new(@"(?<name>[A-Za-z_]\w*)\s*\([^;{}]*?\)\s*\{\s*(?:return\s+)?new\b[^;]*?\bpresent\s*:", RegexOptions.Compiled),
    ];

    private static PresentSeams FindPresentSeams(string strippedFile)
    {
        var helpers = new HashSet<string>();
        var types = new HashSet<string>();
        if (!PresentSeamArgument.IsMatch(strippedFile)) return NoPresentSeams;
        foreach (var declaration in PresentHelperDeclarations)
            foreach (Match m in declaration.Matches(strippedFile))
                helpers.Add(m.Groups["name"].Value);
        foreach (var type in PresentCallbackApis.Values)
            if (Regex.IsMatch(strippedFile, @"\b" + type + @"\b")) types.Add(type);
        return new PresentSeams(helpers, types);
    }

    /// <summary>The variables a member assigns from a <c>present:</c> construction (<c>x = new T(…, present: …)</c>) or from a present-building helper (<c>x = Helper(…)</c>).</summary>
    private static HashSet<string> PresentReceivers(string body, PresentSeams seams)
    {
        var from = seams.Helpers.Count == 0
            ? @"new\b[^;]*?\bpresent\s*:"
            : @"(?:new\b[^;]*?\bpresent\s*:|(?:" + string.Join("|", seams.Helpers.Select(Regex.Escape)) + @")\s*\()";
        // EVERY assignment to the variable must be a present: one: a variable that is also assigned a real window is not exempt.
        var receivers = new HashSet<string>();
        var others = new HashSet<string>();
        foreach (Match m in Regex.Matches(body, @"(?<![\w.])(?<v>[A-Za-z_]\w*)\s*=(?![=>])\s*(?<rhs>[^;]*)"))
            (Regex.IsMatch(m.Groups["rhs"].Value, @"^" + from) ? receivers : others).Add(m.Groups["v"].Value);
        receivers.ExceptWith(others);
        return receivers;
    }

    /// <summary>
    /// A token that MAKES something Visible on the right-hand side of a Visibility write: <c>Visibility.Visible</c> (qualified or not) anywhere in the
    /// expression (a ternary, <c>??</c>, a switch expression), a numeric cast <c>(Visibility)0</c>, or <c>default(Visibility)</c> (both are Visible, value 0).
    /// </summary>
    internal const string VisibleToken =
        @"(?:\bVisibility\s*\.\s*Visible\b|\(\s*(?:[\w.]*\.)?Visibility\s*\)\s*\(?\s*0\b|\bdefault\s*\(\s*(?:[\w.]*\.)?Visibility\s*\))";

    /// <summary>
    /// Any write that makes something Visible, with the text right before it as <c>pre</c> (the receiver chain with its trailing dot):
    /// <c>x.Visibility = …Visible…</c> and <c>SetCurrentValue/SetValue(VisibilityProperty, …Visible…)</c>.
    /// </summary>
    private static readonly Regex AnyVisibleWrite = new(
        @"(?<pre>[^\s;{}(),=]*)\bVisibility\s*=(?!=)[^;]*?" + VisibleToken +
        @"|(?<pre>[^\s;{}(),=]*)\bSet(?:CurrentValue|Value)\s*\(\s*(?:[\w.]*\.)?VisibilityProperty\s*,[^;]*?" + VisibleToken,
        RegexOptions.Compiled);

    /// <summary>A receiver chain of INNER ELEMENTS: at least two simple identifiers (<c>window.CompletionCard.</c>), never the window variable alone, <c>this.</c> or nothing.</summary>
    private static readonly Regex InnerElementReceiver = new(@"^\w+(?:\.\w+)+\.$", RegexOptions.Compiled);

    /// <summary>Spellings that put WS_VISIBLE into an HwndSource: the name, or a numeric literal with the 0x10000000 bit in a WindowStyle assignment / an int constant.</summary>
    private static readonly Regex WsVisibleName = new(@"\b(?:WS_VISIBLE|Ws_?Visible)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WindowStyleAssignment = new(@"\bWindowStyle\s*=(?!=)\s*(?<rhs>[^;,}]*)", RegexOptions.Compiled);

    private static readonly Regex IntConstant = new(@"\b(?:const|static\s+readonly)\s+(?:u?int)\s+(?<name>\w+)\s*=\s*(?<rhs>[^;]*)", RegexOptions.Compiled);

    /// <summary>A constant whose NAME says it is a window style: <c>WS_*</c> / <c>Ws*</c> or anything with <c>Style</c> in it. A sentinel such as <c>Max = 0x7FFFFFFF</c> is not.</summary>
    private static readonly Regex StyleLookingName = new(@"^(?:ws_?\w*|\w*style\w*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The names of every int constant in the file whose value carries WS_VISIBLE (a use in a <c>WindowStyle</c> assignment is judged by the assignment).</summary>
    internal static HashSet<string> FindWsVisibleConstants(string strippedFile) =>
        IntConstant.Matches(strippedFile).Where(m => CarriesWsVisible(m.Groups["rhs"].Value)).Select(m => m.Groups["name"].Value).ToHashSet();

    private static readonly Regex IntegerLiteral = new(@"\b(?:0[xX](?<hex>[0-9A-Fa-f]+)|(?<dec>\d+))[uUlL]*\b", RegexOptions.Compiled);

    private const long WsVisibleBit = 0x10000000;

    private static bool CarriesWsVisible(string expression)
    {
        foreach (Match m in IntegerLiteral.Matches(expression))
        {
            var ok = m.Groups["hex"].Success
                ? long.TryParse(m.Groups["hex"].Value, System.Globalization.NumberStyles.HexNumber, null, out var h) ? (h & WsVisibleBit) != 0 : false
                : long.TryParse(m.Groups["dec"].Value, out var d) && (d & WsVisibleBit) != 0;
            if (ok) return true;
        }
        return WsVisibleName.IsMatch(expression);
    }

    /// <summary>T4: an HwndSource made WS_VISIBLE would paint at once. By name or by value, in a WindowStyle assignment or an int constant.</summary>
    private static IEnumerable<string> WsVisibleProblems(string body, IReadOnlySet<string>? wsVisibleConstants = null)
    {
        // a WindowStyle assignment is judged by what it assigns: a literal or a name that carries the bit, or a constant of the file that does
        foreach (Match m in WindowStyleAssignment.Matches(body))
        {
            var rhs = m.Groups["rhs"].Value;
            if (CarriesWsVisible(rhs) || (wsVisibleConstants is not null && Regex.Matches(rhs, @"\b[A-Za-z_]\w*\b").Any(i => wsVisibleConstants.Contains(i.Value))))
                yield return "a WindowStyle that includes WS_VISIBLE (0x10000000) makes an HwndSource visible: use WS_POPUP only, parked off-screen";
        }
        // a constant is flagged on its own only when it LOOKS like a window style (an unrelated `Max = 0x7FFFFFFF` is not WS_VISIBLE)
        foreach (Match m in IntConstant.Matches(body))
            if (StyleLookingName.IsMatch(m.Groups["name"].Value) && CarriesWsVisible(m.Groups["rhs"].Value))
                yield return "a window-style constant carrying WS_VISIBLE (0x10000000): an HwndSource built with it is visible";
    }

    /// <summary>Any real overlay container built in the member (a Fake/Recording/Stub/Test double is not): production code under test would show what it is handed.</summary>
    private static readonly Regex AnyOverlayContainer = new(
        @"\bnew\s+(?!Fake|Recording|Stub|Test)\w*(?:Overlays|OverlayManager)\w*\s*[({]", RegexOptions.Compiled);

    /// <summary>
    /// The COARSE rule for a <see cref="HeadlessConstruction"/> file, where rule A is waived: such a file by definition never shows a window, so
    /// it may not call ANY showing API, write ANY <c>Visibility = Visible</c>, or build ANY real overlay container — whatever the receiver
    /// expression. That closes the shapes the name heuristic of rule B can miss (alias, array, tuple, ternary, cast, <c>?.</c>, <c>!.</c>, a
    /// helper's return value). A file that genuinely needs one leaves the headless list and prepares its windows.
    /// </summary>
    private static IEnumerable<string> HeadlessFileProblems(string body, PresentSeams seams, bool visibleWriteWaived)
    {
        // A headless file that builds a `present:` window of a type (LagMarkWindow / RestartNoticeWindow with a measuring callback) may call that
        // type's ShowButton / ShowNotice ON A RECEIVER that was built through a present: construction (a variable assigned from such a `new`, or
        // from a same-file helper whose body passes present:). Those go through the callback and put nothing on screen. A real window of the same
        // type, a parameter, a field or an inline expression gets no exemption, and every other showing API still counts.
        var presentReceivers = seams.Types.Count == 0 ? new HashSet<string>() : PresentReceivers(body, seams);
        foreach (Match m in AnyShowingApi.Matches(body))
        {
            var api = m.Groups["api"].Value;
            var receiver = Regex.Match(body[..m.Index], @"(?<![\w.\])!?])(?<recv>[A-Za-z_]\w*)\s*$");
            var exempt = PresentCallbackApis.TryGetValue(api, out var type) && seams.Types.Contains(type)
                && receiver.Success && presentReceivers.Contains(receiver.Groups["recv"].Value);
            if (exempt) continue;
            yield return $"a headless-only file calls a showing API (.{api}): a file that shows a window leaves HeadlessConstruction and prepares it";
            break;
        }
        // A Visible write counts whatever its shape (a ternary, `??`, a switch, a numeric cast, default(Visibility)). A waived member excuses only
        // INNER ELEMENTS (a receiver chain of two or more identifiers): never the window itself, `this`, a bare `Visibility = …` or SetValue on the window.
        foreach (Match m in AnyVisibleWrite.Matches(body))
        {
            if (visibleWriteWaived && InnerElementReceiver.IsMatch(m.Groups["pre"].Value)) continue;
            yield return "a headless-only file makes something Visible (Visibility = Visible): a file that shows a window leaves HeadlessConstruction and prepares it";
            break;
        }
        var container = AnyOverlayContainer.Match(body);
        if (container.Success)
            yield return $"a headless-only file builds an overlay container ({container.Value.Trim()}): production shows what it is handed; " +
                         "a file that does this leaves HeadlessConstruction and prepares its windows";
    }

    private static readonly Regex AnyShowingApi = new(@"\.\s*(?<api>" + ApiAlternation + @")\b", RegexOptions.Compiled);

    /// <summary>
    /// The pattern for a named window used as a receiver, up to (not including) the dot: <c>name</c>, <c>name?</c>, <c>name!</c>, <c>(name)</c>,
    /// <c>((Window)name)</c>, <c>((GlassBackdropWindow)name)</c>, <c>(name as Window)</c> (and the same followed by <c>?</c> / <c>!</c>). A cast or opening
    /// parenthesis BEFORE the name needs no pattern: the name only has to start a token (not follow a letter or a dot).
    /// </summary>
    internal static string ReceiverOf(string escapedName) =>
        @"(?<![\w.])" + escapedName + @"(?:\s+as\s+[\w.]+)?(?:\s*\))*\s*[?!]?";

    private static IEnumerable<string> MemberProblems(string body, bool ruleAWaived, PresentSeams? presentSeams = null, bool visibleWriteWaived = false,
        IReadOnlySet<string>? wsVisibleConstants = null)
    {
        // A bare P/Invoke ShowWindow(...) call (the extern declaration itself is not a call).
        if (BareShowWindow.IsMatch(body) && !Regex.IsMatch(body, @"\bextern\b"))
            yield return "a bare ShowWindow(...) call shows a window the guard cannot name";

        foreach (var problem in WsVisibleProblems(body, wsVisibleConstants)) yield return problem;

        if (ruleAWaived)
        {
            // Rule A is waived for the file; the coarse rule replaces the per-window rule B (it is a superset of it).
            foreach (var problem in HeadlessFileProblems(body, presentSeams ?? NoPresentSeams, visibleWriteWaived)) yield return problem;
            yield break;
        }

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

            // RULE A, per CONSTRUCTION: every real window a test builds is prepared (wrapped, or prepared by name later), unless it is
            // exempt: a headless `present:` window (it never shows itself), or an ActivationButtonWindow (its ShowAt only positions and
            // fades it; its Show() is still caught by rule B below). A member behind an opt-in gate or an AllowList entry never gets here.
            // This does not depend on following the window to wherever it is shown (alias, array, tuple, ternary, ?., !., a cast ...).
            var exempt = use.PresentSeam || use.Type == "ActivationButtonWindow";
            var needsPreparation = !prepared && !offScreen && !exempt;
            if (use.Name is null)
            {
                if (needsPreparation)
                    yield return $"{use.Type} is constructed without InvisibleTestWindow.Prepare / PrepareCloaked / PrepareOffScreen " +
                                 "(prepare it, or list the test file as headless-only with a reason)";
                continue;
            }

            var name = Regex.Escape(use.Name);
            var receiver = ReceiverOf(name);
            var shownBy = Regex.Matches(body, $@"{receiver}\s*\.\s*(?<api>{ApisFor(use.Type, use.PresentSeam)})\b")
                .Select(m => m.Groups["api"].Value).ToList();
            // `w.Visibility = Visibility.Visible` (also fully qualified), `w.SetCurrentValue(VisibilityProperty, ...Visible)`, `w.SetValue(...)`
            var madeVisible = Regex.IsMatch(body, $@"{receiver}\s*\.\s*Visibility\s*=(?!=)[^;]*?{VisibleToken}")
                || Regex.IsMatch(body, $@"{receiver}\s*\.\s*Set(?:CurrentValue|Value)\s*\(\s*(?:[\w.]*\.)?VisibilityProperty\s*,[^;]*?{VisibleToken}");
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
            else if (needsPreparation)
                yield return $"{use.Type} '{use.Name}' is constructed without InvisibleTestWindow.Prepare / PrepareCloaked / PrepareOffScreen " +
                             "(prepare it, or list the test file as headless-only with a reason)";
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
                // a test that passes one (it measures and arranges, nothing more) does not show it through ShowButton/ShowNotice.
                var presentSeam = Regex.IsMatch(statement[m.Index..], @"^new\s+\w+\s*\((?:[^()]|\([^()]*\))*\bpresent\s*:");
                var before = statement[..m.Index];
                var wrapped = Regex.Match(before, @"InvisibleTestWindow\s*\.\s*(?<helper>Prepare\w*)\s*\(\s*$");
                var prepared = wrapped.Success;
                var offScreen = wrapped.Success && wrapped.Groups["helper"].Value == "PrepareOffScreen";

                // `name =` (or `Type name =` / `var name =`) directly before the construction or its wrapper
                var assigned = Regex.Match(before, @"(?<name>[A-Za-z_]\w*)\s*=\s*(?!=)(?:[\w.]*InvisibleTestWindow\s*\.\s*Prepare\w*\s*\(\s*)?$");
                var name = assigned.Success ? assigned.Groups["name"].Value : null;

                // `new Window(...).Show()` inline
                var inlineShown = Regex.IsMatch(statement[m.Index..],
                    $@"^new\s+\w+\s*(?:\([^()]*\))?\s*(?:\{{[^{{}}]*\}})?\s*(?:\))?\s*\.\s*(?:{ApisFor(m.Groups["type"].Value, presentSeam)})\b");
                yield return new WindowUse(m.Groups["type"].Value, name, prepared, offScreen, inlineShown, presentSeam);
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
