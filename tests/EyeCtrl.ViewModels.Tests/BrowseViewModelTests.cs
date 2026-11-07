namespace EyeCtrl.ViewModels.Tests;

using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Browse;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The browse surface's dwell contract, fed through the real message path
/// (DwellDrive.Hold's 30 ms cadence — the DwellDetector's 150 ms restart bound makes single-step
/// holds a lie): dwell on a page element completes and carries the element's rect; while paused
/// ONLY Close stays effectful (external review M11) and suppressed activations stay silent
/// (review M10); the coordinator's scroll suspension and under-point gate (review I3) gate
/// accumulation; ApplySnapshot reconciles through the Core reconciler (move resets, drift
/// preserves); the window bounds feed the note occluder (review I4); and every structural
/// mutation announces exactly one BrowseTargetsChangedMessage for the D17 inventory re-check.
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class BrowseViewModelTests
{
    private static WebElementInfo El(string id, PhysicalRect rect)
        => new(id, WebElementKind.Button, rect);

    private static WebElementSnapshot Snap(int generation, params WebElementInfo[] elements)
        => new(generation, elements);

    private static BrowseViewModel NewVm(WeakReferenceMessenger messenger, EyeCtrlSettings? settings = null)
    {
        settings ??= new EyeCtrlSettings();
        settings.Dwell.ToolbarButtonDwellMs = 300; // fast dwell; the production default is 700
        settings.Dwell.DwellCooldownMs = 100;      // the min: keeps post-activation windows explicit
        return new BrowseViewModel(settings, messenger);
    }

    // ---- the dwell path -------------------------------------------------------

    [Fact]
    public void Dwell_OnAPageElement_CompletesAndFiresWebElementActivated_WithTheRect()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = NewVm(messenger);
        var rect = new PhysicalRect(500, 400, 200, 40);
        (string Id, PhysicalRect Rect)? activated = null;
        vm.WebElementActivated += (id, r, _) => activated = (id, r);

        vm.Show();
        vm.ApplySnapshot(Snap(1, El("web_1", rect)));

        DwellDrive.Hold(messenger, rect.Center, 0, 300_000 + 1);

        Check.That(activated).IsNotNull();
        Check.That(activated!.Value.Id).IsEqualTo("web_1");
        Check.That(activated.Value.Rect).IsEqualTo(rect);
    }

    // ---- pause semantics (the task-switcher rule) ------------------------------

    [Fact]
    public void PausedSamples_OnlyCloseActivates_WhileWebAddressAndNavigationStaySuppressed()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = NewVm(messenger);
        var backRect = new PhysicalRect(500, 100, 60, 60);
        var closeRect = new PhysicalRect(1200, 100, 60, 60);
        var addressRect = new PhysicalRect(700, 100, 400, 60);
        var webRect = new PhysicalRect(500, 400, 200, 40);
        var chrome = new List<string>();
        var web = new List<string>();
        var activations = 0;
        vm.ChromeActivated += id => chrome.Add(id);
        vm.WebElementActivated += (id, _, _) => web.Add(id);
        messenger.Register<BrowseViewModelTests, DwellActivationMessage>(this, (_, _) => activations++);

        vm.Show();
        vm.SetChromeTargets(new (string, PhysicalRect)[]
        {
            (BrowseViewModel.ChromeBackId, backRect),
            (BrowseViewModel.ChromeAddressId, addressRect),
            (BrowseViewModel.ChromeCloseId, closeRect),
        });
        vm.ApplySnapshot(Snap(1, El("web_1", webRect)));

        // Paused dwell on Back: navigation acts on a paused gaze (a reload can drop form
        // input) — suppressed (review M11 + the base rule: the opt-in is for the way OUT).
        DwellDrive.Hold(0, 301_000,
            t => messenger.Send(new GazeCursorMovedMessage(backRect.Center, t) { IsPaused = true }));
        Check.That(chrome).IsEmpty();
        Check.That(activations).IsEqualTo(0); // review M10: suppressed activations are silent

        // Paused dwell on the address bar: OS keyboard focus — suppressed.
        DwellDrive.Hold(301_000, 700_000,
            t => messenger.Send(new GazeCursorMovedMessage(addressRect.Center, t) { IsPaused = true }));
        Check.That(chrome).IsEmpty();

        // Paused dwell on the page element: an OS click into the foreground — suppressed.
        DwellDrive.Hold(700_000, 1_100_000,
            t => messenger.Send(new GazeCursorMovedMessage(webRect.Center, t) { IsPaused = true }));
        Check.That(web).IsEmpty();

        // Paused dwell on Close: the way out — it fires, and the completion cue publishes
        // (a real effect ran; review M10).
        DwellDrive.Hold(1_100_000, 1_401_000,
            t => messenger.Send(new GazeCursorMovedMessage(closeRect.Center, t) { IsPaused = true }));
        Check.That(chrome).ContainsExactly(BrowseViewModel.ChromeCloseId);
        Check.That(activations).IsEqualTo(1);

        // The suppression was the pause, not the registration: unpaused gaze on the same
        // element completes its dwell normally (after Close's activation cooldown lapses).
        DwellDrive.Hold(1_501_000, 1_802_000,
            t => messenger.Send(new GazeCursorMovedMessage(webRect.Center, t)));
        Check.That(web).ContainsExactly("web_1");
        Check.That(activations).IsEqualTo(2);
    }

    [Fact]
    public void PausedPageDwell_PublishesNoJsCue_UntilUnpaused()
    {
        // Review M10: the page outline must not advertise a dwell whose every effect is
        // suppressed; the chrome WPF strip (window-internal feedback) keeps drawing.
        var messenger = new WeakReferenceMessenger();
        using var vm = NewVm(messenger);
        var rect = new PhysicalRect(500, 400, 200, 40);
        var webCues = new List<double>();
        var chromeCues = new List<double>();
        vm.DwellProgressChanged += (id, p) =>
        {
            if (id is not null && id.StartsWith(BrowseViewModel.ChromePrefix, System.StringComparison.Ordinal))
                chromeCues.Add(p);
            else
                webCues.Add(p);
        };

        vm.Show();
        vm.ApplySnapshot(Snap(1, El("web_1", rect)));
        vm.SetChromeTargets(new (string, PhysicalRect)[] { (BrowseViewModel.ChromeCloseId, new PhysicalRect(500, 100, 60, 60)) });

        DwellDrive.Hold(0, 151_000,
            t => messenger.Send(new GazeCursorMovedMessage(rect.Center, t) { IsPaused = true }));
        Check.That(webCues).IsEmpty();
        Check.That(chromeCues).IsEmpty();

        DwellDrive.Hold(151_000, 181_000,
            t => messenger.Send(new GazeCursorMovedMessage(new PhysicalRect(500, 100, 60, 60).Center, t) { IsPaused = true }));
        Check.That(chromeCues).Not.IsEmpty();

        // Unpaused gaze on the page element: its cue flows again.
        DwellDrive.Hold(181_000, 211_000, t => messenger.Send(new GazeCursorMovedMessage(rect.Center, t)));
        Check.That(webCues).Not.IsEmpty();
    }

    // ---- the re-arm rule (external review r2 I2) --------------------------------

    [Fact]
    public void ARestingGaze_YieldsExactlyOneActivation_UntilItLeavesTheTarget()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = NewVm(messenger);
        var rect = new PhysicalRect(500, 400, 200, 40);
        var activations = 0;
        vm.WebElementActivated += (_, _, _) => activations++;

        vm.Show();
        vm.ApplySnapshot(Snap(1, El("web_1", rect)));

        // Three dwell+cooldown cycles under a resting gaze: without the leave-before-re-arm
        // rule this re-fired every ~1.1 s (on the address box it re-ran SelectAll and the next
        // key replaced everything typed).
        DwellDrive.Hold(messenger, rect.Center, 0, 1_301_000);
        Check.That(activations).IsEqualTo(1);

        // The gaze leaves the target: the latch releases and the target can fire again.
        DwellDrive.Hold(messenger, new PhysicalPoint(500, 100), 1_301_000, 1_401_000);
        DwellDrive.Hold(messenger, rect.Center, 1_401_000, 1_701_000);
        Check.That(activations).IsEqualTo(2);
    }

    [Fact]
    public void ARestingGaze_KeepsTheLatchWhenItsTargetVanishesAndReturns()
    {
        // r3 I2: r2 M4's disabled filter drops a disable-on-submit button from the snapshot
        // while the form posts, and the page re-adds the SAME id when it re-enables. The
        // latch must survive the disappearance — it follows the fired target's last-known
        // rect — and release only on gaze exit; a re-added target starts with no accumulated
        // dwell, so without the latch the resting gaze re-submits one dwell later.
        var messenger = new WeakReferenceMessenger();
        using var vm = NewVm(messenger);
        var rect = new PhysicalRect(500, 400, 200, 40);
        var activations = 0;
        vm.WebElementActivated += (_, _, _) => activations++;

        vm.Show();
        vm.ApplySnapshot(Snap(1, El("web_1", rect)));

        DwellDrive.Hold(messenger, rect.Center, 0, 801_000);
        Check.That(activations).IsEqualTo(1);

        // The page disables Send: the next snapshot drops it, and the gaze keeps sampling
        // THROUGH the gone-window — the latch must NOT release here (the old logic cleared it
        // the moment TryGetLatchedRect failed).
        vm.ApplySnapshot(Snap(2));
        DwellDrive.Hold(messenger, rect.Center, 801_000, 901_000);

        // The page re-enables Send: the same id registers fresh (a re-added target starts
        // with no accumulated dwell — DwellDetector's re-add path).
        vm.ApplySnapshot(Snap(3, El("web_1", rect)));

        DwellDrive.Hold(messenger, rect.Center, 901_000, 2_201_000);
        Check.That(activations).IsEqualTo(1);

        // The gaze leaves the fired area: the latch releases and the target can fire again.
        DwellDrive.Hold(messenger, new PhysicalPoint(500, 100), 2_101_000, 2_201_000);
        DwellDrive.Hold(messenger, rect.Center, 2_201_000, 2_501_000);
        Check.That(activations).IsEqualTo(2);
    }

    [Fact]
    public void TheChromeLatch_HoldsUnderARestingGaze_AndReleasesOnGazeExit()
    {
        // r3 m3: the chrome path shares the latch — the address box was r2 I2's motivating
        // case (its re-dwell re-ran SelectAll and the next key wiped the typed text). The
        // chrome latch is the same last-known-rect latch, so a resting gaze yields one
        // activation and gaze exit re-arms.
        var messenger = new WeakReferenceMessenger();
        using var vm = NewVm(messenger);
        var closeRect = new PhysicalRect(900, 100, 48, 48);
        var chromeActivations = 0;
        vm.ChromeActivated += _ => chromeActivations++;

        vm.Show();
        vm.SetChromeTargets(new (string, PhysicalRect)[] { (BrowseViewModel.ChromeCloseId, closeRect) });

        DwellDrive.Hold(messenger, closeRect.Center, 0, 801_000);
        Check.That(chromeActivations).IsEqualTo(1);

        DwellDrive.Hold(messenger, closeRect.Center, 801_000, 2_101_000);
        Check.That(chromeActivations).IsEqualTo(1);

        DwellDrive.Hold(messenger, new PhysicalPoint(500, 500), 2_101_000, 2_201_000);
        DwellDrive.Hold(messenger, closeRect.Center, 2_201_000, 2_501_000);
        Check.That(chromeActivations).IsEqualTo(2);
    }

    // ---- the coordinator's under-point gate (external review I3) ----------------

    [Fact]
    public void GazeUnderAnotherSurface_GatesAccumulation_UntilTheBrowserIsUnderThePoint()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = NewVm(messenger);
        var rect = new PhysicalRect(500, 400, 200, 40);
        bool activated = false;
        vm.WebElementActivated += (_, _, _) => activated = true;

        vm.Show();
        vm.ApplySnapshot(Snap(1, El("web_1", rect)));

        vm.MayDwellAt = _ => false; // the keyboard/toolbar is the window under the gaze point
        DwellDrive.Hold(messenger, rect.Center, 0, 601_000);
        Check.That(activated).IsFalse();

        vm.MayDwellAt = _ => true;
        DwellDrive.Hold(messenger, rect.Center, 601_000, 901_000);
        Check.That(activated).IsTrue();
    }

    [Fact]
    public void NoGateInstalled_IsUngated()
    {
        // Headless consumers (and any seam consumer that has not installed the gate) keep the
        // old behavior — the production coordinator always installs it.
        var messenger = new WeakReferenceMessenger();
        using var vm = NewVm(messenger);
        var rect = new PhysicalRect(500, 400, 200, 40);
        bool activated = false;
        vm.WebElementActivated += (_, _, _) => activated = true;

        vm.Show();
        vm.ApplySnapshot(Snap(1, El("web_1", rect)));
        Check.That(vm.MayDwellAt).IsNull();

        DwellDrive.Hold(messenger, rect.Center, 0, 301_000);
        Check.That(activated).IsTrue();
    }

    // ---- the note-gate occluder source (external review I4) ----------------------

    [Fact]
    public void WindowBounds_FeedTheOccluder_AndClearOnHide()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = NewVm(messenger);
        var bounds = new PhysicalRect(0, 0, 1920, 1040);

        vm.Show();
        Check.That(vm.WindowOccluderRects).IsEmpty(); // nothing reported yet: fail-safe empty

        vm.ReportWindowBounds(bounds);
        Check.That(vm.WindowOccluderRects).Contains(bounds);

        vm.Hide();
        Check.That(vm.WindowOccluderRects).IsEmpty();
    }

    // ---- the coordinator's scroll gate -----------------------------------------

    [Fact]
    public void ScrollSuspended_GateAccumulatesNothing_ThenResumes()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = NewVm(messenger);
        var rect = new PhysicalRect(500, 400, 200, 40);
        bool activated = false;
        var progress = new List<double>();
        vm.WebElementActivated += (_, _, _) => activated = true;
        vm.DwellProgressChanged += (_, p) => progress.Add(p);

        vm.Show();
        vm.ApplySnapshot(Snap(1, El("web_1", rect)));
        vm.SetScrollSuspended(true);

        DwellDrive.Hold(messenger, rect.Center, 0, 301_000);

        Check.That(activated).IsFalse();
        Check.That(progress).IsEmpty();

        vm.SetScrollSuspended(false);
        DwellDrive.Hold(messenger, rect.Center, 301_000, 601_000 + 1);

        Check.That(activated).IsTrue();
    }

    // ---- reconciliation against the live detector ------------------------------

    [Fact]
    public void ApplySnapshot_ElementMovedBeyondThreshold_ReRegistersFresh()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = NewVm(messenger);
        var before = new PhysicalRect(500, 400, 200, 40);
        var after = new PhysicalRect(500, 800, 200, 40); // 400 px down: page scroll, not drift
        bool activated = false;
        vm.WebElementActivated += (_, _, _) => activated = true;

        vm.Show();
        vm.ApplySnapshot(Snap(1, El("web_1", before)));
        DwellDrive.Hold(messenger, before.Center, 0, 151_000); // half a dwell accumulated

        vm.ApplySnapshot(Snap(2, El("web_1", after))); // > MoveResetThresholdPx → re-add: reset

        // The remaining half of the ORIGINAL dwell must not complete anything: the target
        // re-registered fresh at the new rect, so the gaze needs a full dwell again.
        DwellDrive.Hold(messenger, after.Center, 151_000, 301_000);
        Check.That(activated).IsFalse();

        // A full fresh dwell on the new rect completes.
        DwellDrive.Hold(messenger, after.Center, 301_000, 611_000);
        Check.That(activated).IsTrue();
    }

    [Fact]
    public void ApplySnapshot_SmallDrift_RebasesInPlace_PreservingAccumulatedProgress()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = NewVm(messenger);
        var before = new PhysicalRect(500, 400, 200, 40);
        var drifted = new PhysicalRect(508, 400, 200, 40); // 8 px ≤ MoveResetThresholdPx
        double lastProgress = 0;
        vm.DwellProgressChanged += (_, p) => lastProgress = p;

        vm.Show();
        vm.ApplySnapshot(Snap(1, El("web_1", before)));
        DwellDrive.Hold(messenger, before.Center, 0, 151_000); // ~half the dwell accumulated
        double beforeDrift = lastProgress;

        vm.ApplySnapshot(Snap(2, El("web_1", drifted)));

        // One more step at the drifted rect: the rebase preserved the dwell's start, so the
        // progress CONTINUES from where it was instead of restarting near zero.
        DwellDrive.Hold(messenger, drifted.Center, 151_000, 181_000);
        Check.That(beforeDrift).IsStrictlyGreaterThan(0.1);
        Check.That(lastProgress).IsStrictlyGreaterThan(beforeDrift);
        Check.That(lastProgress).IsStrictlyLessThan(1.0);
    }

    // ---- the inventory trigger --------------------------------------------------

    [Fact]
    public void EachMutatingApi_RaisesExactlyOneTargetsChangedMessage()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = NewVm(messenger);
        var rect = new PhysicalRect(500, 400, 200, 40);
        var changes = 0;
        messenger.Register<BrowseViewModelTests, BrowseTargetsChangedMessage>(
            this, (_, _) => changes++);

        vm.Show();
        vm.ApplySnapshot(Snap(1, El("web_1", rect)));
        Check.That(changes).IsEqualTo(1);

        vm.ApplySnapshot(Snap(2, El("web_1", rect))); // unchanged content still announces
        Check.That(changes).IsEqualTo(2);

        vm.SetChromeTargets(new (string, PhysicalRect)[]
        {
            (BrowseViewModel.ChromeBackId, new PhysicalRect(500, 100, 60, 60)),
        });
        Check.That(changes).IsEqualTo(3);

        vm.ClearWebTargets();
        Check.That(changes).IsEqualTo(4);

        vm.Hide(); // the clear-on-hide path
        Check.That(changes).IsEqualTo(5);
    }

    [Fact]
    public void ClearWebTargets_ChromeSurvives_AndGoneWebTargetsStopDwelling()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = NewVm(messenger);
        var webRect = new PhysicalRect(500, 400, 200, 40);
        var backRect = new PhysicalRect(500, 100, 60, 60);
        var web = new List<string>();
        var chrome = new List<string>();
        vm.WebElementActivated += (id, _, _) => web.Add(id);
        vm.ChromeActivated += id => chrome.Add(id);

        vm.Show();
        vm.SetChromeTargets(new (string, PhysicalRect)[]
        {
            (BrowseViewModel.ChromeBackId, backRect),
        });
        vm.ApplySnapshot(Snap(1, El("web_1", webRect)));
        vm.ClearWebTargets();

        // The web target is gone: its rect dwells on nothing.
        DwellDrive.Hold(messenger, webRect.Center, 0, 301_000);
        Check.That(web).IsEmpty();
        Check.That(chrome).IsEmpty();

        // The chrome target survives the navigation: it still completes.
        DwellDrive.Hold(messenger, backRect.Center, 301_000, 611_000);
        Check.That(chrome).ContainsExactly(BrowseViewModel.ChromeBackId);
        Check.That(web).IsEmpty();
    }

    [Fact]
    public void Hide_StopsTheDwellSurface_AndFurtherSamplesDoNothing()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = NewVm(messenger);
        var rect = new PhysicalRect(500, 400, 200, 40);
        bool activated = false;
        vm.WebElementActivated += (_, _, _) => activated = true;

        vm.Show();
        vm.ApplySnapshot(Snap(1, El("web_1", rect)));
        vm.Hide();

        DwellDrive.Hold(messenger, rect.Center, 0, 301_000);
        Check.That(activated).IsFalse();
        Check.That(vm.IsVisible).IsFalse();
    }
}
