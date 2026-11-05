namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using EyeCtrl.App.Browse;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls.Theme;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// Headless structural pins for the gaze browser's window. The window is CONSTRUCTED — never
/// shown — under <see cref="InvisibleTestWindow.Prepare"/> (the NoVisibleTestWindowGuardTests
/// rule), which is safe because the WebView2 host is created lazily on first Show: a headless
/// construction touches no Microsoft.Web.WebView2 type, the seam's whole point.
/// <para>
/// Pinned here: the base-class font, the conscious NOT-click-through stance, the chrome row's
/// automation ids (the names the dwell targets are keyed by), the lazy-host contract, and the
/// SOURCE SHAPE of the post-Show focusability flip — the CalibrationWindow precedent
/// (<c>SetOverlayInteraction(clickThrough: false, focusable: true); Activate();</c> after
/// <c>Show()</c>, because OverlayWindow.OnSourceInitialized hard-codes focusable:false and an
/// earlier call no-ops on the first run). A refactor that reorders the flip before Show or
/// drops the focusable argument fails here by name instead of silently leaving the first
/// session's address box dead to the physical keyboard.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class BrowseWindowHeadlessTests
{
    private static BrowseWindow ConstructWindow()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        return InvisibleTestWindow.Prepare(new BrowseWindow());
    }

    [StaFact]
    public void Window_InheritsTheOverlayFont_AndIsNotClickThrough()
    {
        var window = ConstructWindow();

        // OverlayWindow's ctor sets the themed font for every overlay (design spec §8.8); a
        // XAML FontFamily would win, and its absence here must not silently regress it.
        Check.That(window.FontFamily).IsEqualTo(Tokens.FontStandard);

        // The conscious override: the browser receives mouse input and OS keyboard focus —
        // a click-through browser would feed every gaze click to whatever is beneath.
        Check.That(window.IsClickThrough).IsFalse();
    }

    [StaFact]
    public void Chrome_Elements_CarryTheDwellTargetAutomationIds()
    {
        var window = ConstructWindow();

        Check.That(AutomationId(window, "ChromeBack")).IsEqualTo(BrowseViewModel.ChromeBackId);
        Check.That(AutomationId(window, "ChromeForward")).IsEqualTo(BrowseViewModel.ChromeForwardId);
        Check.That(AutomationId(window, "ChromeHome")).IsEqualTo(BrowseViewModel.ChromeHomeId);
        Check.That(AutomationId(window, "ChromeReload")).IsEqualTo(BrowseViewModel.ChromeReloadId);
        Check.That(AutomationId(window, "ChromeClose")).IsEqualTo(BrowseViewModel.ChromeCloseId);
        Check.That(AutomationId(window, "AddressBox")).IsEqualTo(BrowseViewModel.ChromeAddressId);
    }

    [StaFact]
    public void Window_IsConstructedWithoutAWebSource_TheHostIsLazilyCreatedOnShow()
    {
        // "Never constructed in unit tests" for the runtime: constructing the window must not
        // build the WebView2 host (its managed control loads no native code, but the seam's
        // contract keeps the runtime out of every test path).
        var window = ConstructWindow();

        Check.That(window.WebSource).IsNull();
    }

    [Fact]
    public void TheFocusabilityFlip_IsPinnedInTheSource_AfterShow()
    {
        // The CalibrationWindow precedent is load-bearing (the comment there records the first
        // run it fixed): OverlayWindow.OnSourceInitialized applies focusable:false at HWND
        // creation, SetOverlayInteraction no-ops before Show(), and Activate() is blocked by
        // WS_EX_NOACTIVATE until the flip runs. So the flip must (a) exist with exactly these
        // arguments and (b) come AFTER the Show() call in Show(BrowseViewModel). A source scan
        // — not reflection — because the ordering is the contract.
        var source = File.ReadAllText(SourcePath("Overlays", "BrowseWindow.xaml.cs"));

        var showCall = source.IndexOf("Show();", StringComparison.Ordinal);
        // Searching FROM the Show() call: the class doc quotes the literal, and the contract is
        // about the executable order, not the prose.
        var interactionCall = source.IndexOf(
            "SetOverlayInteraction(clickThrough: false, focusable: true);", showCall, StringComparison.Ordinal);
        var activateCall = source.IndexOf("Activate();", interactionCall, StringComparison.Ordinal);

        Check.WithCustomMessage("BrowseWindow.Show must call the base Show() — the flip depends on the HWND existing")
            .That(showCall).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "BrowseWindow must re-enable focusability after Show with exactly " +
                "SetOverlayInteraction(clickThrough: false, focusable: true) — the calibration " +
                "precedent; dropping it leaves the first session's address box dead to the keyboard.")
            .That(interactionCall).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("the focusability flip must come after Show() (a pre-Show call no-ops)")
            .That(interactionCall).IsStrictlyGreaterThan(showCall);
        Check.WithCustomMessage("Activate() must follow the focusability flip, or WS_EX_NOACTIVATE still blocks it")
            .That(activateCall).IsStrictlyGreaterThan(interactionCall);
    }

    [StaFact]
    public void TheChromeMap_CoversAllSixDwellIds_IncludingTheAddressBox()
    {
        var window = ConstructWindow();

        // Review R1's pin: all SIX chrome dwell ids — the five buttons AND the address box —
        // feed TryGetChromeRects; a dropped address box would leave the one gaze-reachable way
        // to type an address unregistered. The map is pinned by id (a shown window is not
        // available headlessly for the measured rects; the before-layout failure case is pinned
        // by TryGetWebOrigin_FailsBeforeLayout_AndChromeRectsDoToo).
        var ids = window.ChromeIdsForTest;
        Check.That(ids).Contains(BrowseViewModel.ChromeBackId);
        Check.That(ids).Contains(BrowseViewModel.ChromeForwardId);
        Check.That(ids).Contains(BrowseViewModel.ChromeHomeId);
        Check.That(ids).Contains(BrowseViewModel.ChromeReloadId);
        Check.That(ids).Contains(BrowseViewModel.ChromeCloseId);
        Check.That(ids).Contains(BrowseViewModel.ChromeAddressId);
        Check.That(ids).HasSize(6);
    }

    [StaFact]
    public void TheChromeMap_PairsEachIdWithItsOwnElement_IncludingTheAddressBox()
    {
        // Review M9: the id pin above cannot catch a REMAPPED id (chrome_address → ChromeClose
        // survives it), and TryGetChromeRects reads the values — so the pairing itself is the
        // pin: the map's element for the address id must be the address box field.
        var window = ConstructWindow();
        var addressBox = (System.Windows.UIElement)typeof(BrowseWindow).GetField("AddressBox",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(window)!;

        Check.That(window.ChromeButtonsForTest[BrowseViewModel.ChromeAddressId]).IsSameReferenceAs(addressBox);
        Check.That(window.ChromeButtonsForTest[BrowseViewModel.ChromeCloseId])
            .IsSameReferenceAs(typeof(BrowseWindow).GetField("ChromeClose",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(window)!);
    }

    [StaFact]
    public void TryGetWebOrigin_FailsBeforeLayout_AndChromeRectsDoToo()
    {
        var window = ConstructWindow();

        // A constructed-but-never-shown window has no layout and no honest origin: both
        // geometry reads must report failure rather than a rect at the origin (the phantom-
        // rect rule the inventory's sources share). The review-round seams behave the same:
        // the web bounds (the trust clip), the window bounds (the note occluder) and the
        // under-point probe (no HWND yet) all answer conservatively before Show.
        Check.That(window.TryGetWebOrigin(out _)).IsFalse();
        Check.That(window.TryGetChromeRects(out var rects)).IsFalse();
        Check.That(rects).IsEmpty();
        Check.That(window.TryGetWebBounds(out _)).IsFalse();
        Check.That(window.TryGetWindowBounds(out _)).IsFalse();
        Check.That(window.IsUnderPoint(new EyeCtrl.Core.Models.PhysicalPoint(100, 100))).IsFalse();
    }

    [Fact]
    public void TheBridgeHost_IsPinnedInTheSource_JsonAccessorSenderCheckNewWindowRedirect()
    {
        // Reviews C1/C3c/I5, pinned at the source (the runtime cannot be constructed
        // headlessly): the host must read bridge messages with WebMessageAsJson (the script
        // posts a JS OBJECT — the string accessor throws for every snapshot), must gate the
        // sender on the current top-level document, must raise NavigationStarting (review I1),
        // and must handle NewWindowRequested by marking it Handled (an unhandled one opens an
        // unmanaged popup). The M8 pin: initialization failure reports through the awaiting
        // command only — the InitializeCoreAsync catch does not raise Failed itself.
        var source = File.ReadAllText(SourcePath("Browse", "WebView2BrowseHost.cs"));

        Check.WithCustomMessage("the host must read object envelopes with WebMessageAsJson (review C1)")
            .That(source.Contains("e.WebMessageAsJson", StringComparison.Ordinal)).IsTrue();
        Check.WithCustomMessage("TryGetWebMessageAsString throws for the script's object posts (review C1)")
            .That(source.Contains("TryGetWebMessageAsString", StringComparison.Ordinal)).IsFalse();
        Check.WithCustomMessage("the sender must be checked against the current document (review C3c)")
            .That(source.Contains("!string.Equals(senderUri, current", StringComparison.Ordinal)).IsTrue();
        Check.WithCustomMessage("NavigationStarting must reach the coordinator (review I1)")
            .That(source.Contains("NavigationStarting?.Invoke()", StringComparison.Ordinal)).IsTrue();
        Check.WithCustomMessage("NewWindowRequested must be handled — an unhandled one opens an unmanaged popup (review I5)")
            .That(source.Contains("e.Handled = true", StringComparison.Ordinal)).IsTrue();
        Check.WithCustomMessage(
                "initialization failure reports once, through the awaiting command (review M8): exactly the " +
                "two command catches raise Failed — a third raise in InitializeCoreAsync doubles the notice")
            .That(System.Text.RegularExpressions.Regex.Matches(
                source, System.Text.RegularExpressions.Regex.Escape("Failed?.Invoke(Describe(ex));")).Count)
            .IsEqualTo(2);
    }

    [StaFact]
    public void AddressInput_NormalizesBareHosts_AndRejectsGarbage()
    {
        // The address seam feeds NavigateRequested with an absolute http(s) URI — the web
        // source's Navigate(Uri) contract. Pinned headlessly against the same normalization
        // the Enter handler runs, without showing the window or touching the web view.
        var window = ConstructWindow();
        Uri? requested = null;
        window.NavigateRequested += uri => requested = uri;

        Check.That(window.TryNavigate("example.com/path")).IsTrue();
        Check.That(requested).IsNotNull();
        Check.That(requested!.Scheme).IsEqualTo(Uri.UriSchemeHttps);
        Check.That(requested.Host).IsEqualTo("example.com");
        Check.That(requested.AbsolutePath).IsEqualTo("/path");

        requested = null;
        Check.That(window.TryNavigate("   ")).IsFalse();
        Check.That(window.TryNavigate("not a uri \u0001")).IsFalse();
        Check.That(requested).IsNull();
    }

    [Fact]
    public void TheChromeRow_IsSizedForDwelling_AtTheToolbarSProvenButtonSize()
    {
        // Owner feedback from the first live smoke (2026-11-05): dwelling the 48-DIP chrome
        // buttons was too difficult — the controls must be bigger. The chrome row now uses the
        // toolbar's proven mis-dwell-fix size (120 DIP buttons, themed display-size glyphs, 16
        // DIP inter-button separation). Pinned at the source: a shown window is not available
        // headlessly for the measured rects, and the size is the contract.
        var xaml = File.ReadAllText(SourcePath("Overlays", "BrowseWindow.xaml"));

        Check.WithCustomMessage("all five chrome buttons must be 120 DIP (owner feedback 2026-11-05)")
            .That(System.Text.RegularExpressions.Regex.Matches(
                xaml, System.Text.RegularExpressions.Regex.Escape("Width=\"120\" Height=\"120\"")).Count)
            .IsEqualTo(5);
        Check.WithCustomMessage("the address box shares the row's dwell height")
            .That(xaml.Contains("Height=\"120\">", StringComparison.Ordinal)).IsTrue();
        Check.WithCustomMessage("the glyphs and the address text scale with the row (the themed display size; owner feedback 2026-11-05: the URL font was too small)")
            .That(System.Text.RegularExpressions.Regex.Matches(
                xaml, System.Text.RegularExpressions.Regex.Escape("FontSize=\"{StaticResource FontSizeDisplay}\"")).Count)
            .IsEqualTo(6);
        Check.WithCustomMessage("inter-button separation widened with the buttons")
            .That(System.Text.RegularExpressions.Regex.Matches(
                xaml, System.Text.RegularExpressions.Regex.Escape("Margin=\"0,0,16,0\"")).Count)
            .IsEqualTo(4);
    }

    private static string? AutomationId(BrowseWindow window, string elementName) =>
        AutomationProperties.GetAutomationId(
            (UIElement)typeof(BrowseWindow).GetField(elementName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(window)!);

    private static string SourcePath(params string[] segmentsUnderApp)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent;
        Check.That(dir).IsNotNull();
        return Path.Combine(new[] { dir!.FullName, "src", "EyeCtrl.App" }.Concat(segmentsUnderApp).ToArray());
    }
}
