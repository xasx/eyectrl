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
/// holds a lie): dwell on a page element completes and carries the element's rect; a paused
/// sample keeps chrome navigation and close live while suppressing the page click and the
/// address bar; the coordinator's scroll suspension gates accumulation; ApplySnapshot reconciles
/// through the Core reconciler (move resets, drift preserves); and every structural mutation
/// announces exactly one BrowseTargetsChangedMessage for the D17 inventory re-check.
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
        vm.WebElementActivated += (id, r) => activated = (id, r);

        vm.Show();
        vm.ApplySnapshot(Snap(1, El("web_1", rect)));

        DwellDrive.Hold(messenger, rect.Center, 0, 300_000 + 1);

        Check.That(activated).IsNotNull();
        Check.That(activated!.Value.Id).IsEqualTo("web_1");
        Check.That(activated.Value.Rect).IsEqualTo(rect);
    }

    // ---- pause semantics (the task-switcher rule) ------------------------------

    [Fact]
    public void PausedSamples_ChromeNavigationActivates_WhileWebAndAddressStaySuppressed()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = NewVm(messenger);
        var backRect = new PhysicalRect(500, 100, 60, 60);
        var addressRect = new PhysicalRect(700, 100, 400, 60);
        var webRect = new PhysicalRect(500, 400, 200, 40);
        var chrome = new List<string>();
        var web = new List<string>();
        vm.ChromeActivated += id => chrome.Add(id);
        vm.WebElementActivated += (id, _) => web.Add(id);

        vm.Show();
        vm.SetChromeTargets(new (string, PhysicalRect)[]
        {
            (BrowseViewModel.ChromeBackId, backRect),
            (BrowseViewModel.ChromeAddressId, addressRect),
        });
        vm.ApplySnapshot(Snap(1, El("web_1", webRect)));

        // Paused dwell on Back: navigation is window-internal — it fires.
        DwellDrive.Hold(0, 301_000,
            t => messenger.Send(new GazeCursorMovedMessage(backRect.Center, t) { IsPaused = true }));
        Check.That(chrome).ContainsExactly(BrowseViewModel.ChromeBackId);

        // Paused dwell on the address bar: OS keyboard focus — suppressed.
        DwellDrive.Hold(301_000, 700_000,
            t => messenger.Send(new GazeCursorMovedMessage(addressRect.Center, t) { IsPaused = true }));
        Check.That(chrome).HasSize(1);

        // Paused dwell on the page element: an OS click into the foreground — suppressed.
        DwellDrive.Hold(700_000, 1_100_000,
            t => messenger.Send(new GazeCursorMovedMessage(webRect.Center, t) { IsPaused = true }));
        Check.That(web).IsEmpty();

        // The suppression was the pause, not the registration: unpaused gaze on the same
        // element completes its dwell normally.
        DwellDrive.Hold(1_100_000, 1_401_000,
            t => messenger.Send(new GazeCursorMovedMessage(webRect.Center, t)));
        Check.That(web).ContainsExactly("web_1");
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
        vm.WebElementActivated += (_, _) => activated = true;
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
        vm.WebElementActivated += (_, _) => activated = true;

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
        vm.WebElementActivated += (id, _) => web.Add(id);
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
        vm.WebElementActivated += (_, _) => activated = true;

        vm.Show();
        vm.ApplySnapshot(Snap(1, El("web_1", rect)));
        vm.Hide();

        DwellDrive.Hold(messenger, rect.Center, 0, 301_000);
        Check.That(activated).IsFalse();
        Check.That(vm.IsVisible).IsFalse();
    }
}
