namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Browse;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Browse;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// The browse coordinator's wiring contract, headless end to end: summon shows the window and
/// loads the start page once; a page element's dwell activation is exactly one OS click at the
/// element's mapped center; all six chrome ids route (navigation, home re-load, reload, close
/// teardown, address focus); a failed web source degrades (address-bar notice, close, dispose)
/// instead of crashing; a settled navigation clears the page targets; the ScrollMode state flip
/// suspends and resumes dwell — including an edge delivered from a NON-UI thread and landing
/// through the coordinator's BeginInvoke; and a visible toolbar is re-asserted above the
/// browser at summon while a hidden one is left alone.
/// </summary>
public class BrowseCoordinatorTests
{
    private const int DwellMs = 700; // ToolbarButtonDwellMs's default

    // ── fakes ──

    private sealed class FakeWebSource : IBrowseWebSource
    {
        public event Action<string>? ElementMessageReceived;
        public event Action<string, bool>? NavigationCompleted;
        public event Action<bool>? BackPossibilityChanged;
        public event Action<bool>? ForwardPossibilityChanged;
        public event Action? Ready;
        public event Action<string>? Failed;

        public List<Uri> Navigated { get; } = new();
        public List<string> NavigatedToString { get; } = new();
        public List<string> Posted { get; } = new();
        public int BackCalls;
        public int ForwardCalls;
        public int ReloadCalls;
        public int DisposeCalls;

        public void Navigate(Uri uri) => Navigated.Add(uri);
        public void NavigateToString(string html) => NavigatedToString.Add(html);
        public void GoBack() => BackCalls++;
        public void GoForward() => ForwardCalls++;
        public void Reload() => ReloadCalls++;
        public void PostToPage(string json) => Posted.Add(json);
        public void Dispose() => DisposeCalls++;

        public void FireReady() => Ready?.Invoke();
        public void FireFailed(string reason) => Failed?.Invoke(reason);
        public void FireElement(string json) => ElementMessageReceived?.Invoke(json);
        public void FireNavigation(string url, bool ok) => NavigationCompleted?.Invoke(url, ok);
        public void FireBackPossibility(bool possible) => BackPossibilityChanged?.Invoke(possible);
    }

    private sealed class FakeSurface : IBrowseWindowSurface
    {
        public BrowseViewModel? ShownVm;
        public int FocusAddressCalls;
        public int ActivateToFrontCalls;
        public int CloseCalls;
        public int ShowAddressErrorCalls;
        public string? LastAddressError;
        public PhysicalPoint Origin { get; set; } = new(100, 50);
        public bool OriginAvailable { get; set; } = true;
        public List<(string Id, PhysicalRect Rect)> ChromeRects { get; set; } = new()
        {
            (BrowseViewModel.ChromeBackId, new PhysicalRect(500, 100, 48, 48)),
            (BrowseViewModel.ChromeForwardId, new PhysicalRect(556, 100, 48, 48)),
            (BrowseViewModel.ChromeHomeId, new PhysicalRect(612, 100, 48, 48)),
            (BrowseViewModel.ChromeReloadId, new PhysicalRect(668, 100, 48, 48)),
            (BrowseViewModel.ChromeAddressId, new PhysicalRect(724, 100, 168, 48)),
            (BrowseViewModel.ChromeCloseId, new PhysicalRect(900, 100, 48, 48)),
        };
        public List<(string? Id, double Progress)> ChromeProgress { get; } = new();
        public bool ChromeRectsAvailable { get; set; } = true;

        public event Action<Uri>? NavigateRequested;
        public event Action? SurfaceClosed;

        public void Show(BrowseViewModel vm) => ShownVm = vm;
        public void Close()
        {
            CloseCalls++;
            SurfaceClosed?.Invoke();
        }

        public void FocusAddressBox() => FocusAddressCalls++;
        public void ActivateToFront() => ActivateToFrontCalls++;

        public bool TryGetWebOrigin(out PhysicalPoint origin)
        {
            origin = Origin;
            return OriginAvailable;
        }

        public bool TryGetChromeRects(out IReadOnlyList<(string Id, PhysicalRect Rect)> rects)
        {
            rects = ChromeRects;
            return ChromeRectsAvailable;
        }

        public void ShowAddressError(string message)
        {
            ShowAddressErrorCalls++;
            LastAddressError = message;
        }

        public void ShowChromeProgress(string? id, double progress) => ChromeProgress.Add((id, progress));

        public void FireNavigateRequested(Uri uri) => NavigateRequested?.Invoke(uri);
        public void FireSurfaceClosed() => SurfaceClosed?.Invoke();
    }

    private sealed class FakeToolbar : IToolbarOverlay
    {
        public bool IsVisibleValue { get; set; }
        public bool RaiseToTopCalled;

        public bool IsVisible => IsVisibleValue;
        public bool TargetsInitialized => true;
        public bool IsSliding => false;
        public bool IsOnScreen => IsVisibleValue;
        public double ExtentDip => 140;

        public void Show() => IsVisibleValue = true;
        public void Hide() => IsVisibleValue = false;
        public void RaiseToTop() => RaiseToTopCalled = true;

        public void Initialize(double buttonSize, int dwellTimeMs, bool includeReset) { }
        public void SetMode(bool isDocked, string edge) { }
        public void SetSelectionModeState(SelectionMode mode) { }
        public void SetPausedState(bool isPaused) { }
        public void UpdateGaze(PhysicalPoint screenPoint, long timestampMicroseconds) { }
        public void BindToViewModel() { }
        public void ShowAtRest() { }
        public void ShowThenSlideIn() { }
        public void SlideOutThenHide(Action onHidden) { }
        public PhysicalRect GetPhysicalBounds() => default;
    }

    // ── the kit ──

    private sealed class Kit
    {
        public readonly TestFakes.FakeInputSimulator Input = new();
        public readonly FakeToolbar Toolbar = new();
        public readonly InteractionManager Im = new();
        public readonly WeakReferenceMessenger Messenger = new();
        public readonly Dispatcher Dispatcher = Dispatcher.CurrentDispatcher;
        public readonly EyeCtrlSettings Settings = new();
        public readonly FakeSurface Surface = new();
        public readonly FakeWebSource Web = new();
        public readonly BrowseViewModel Vm;
        public BrowseCoordinator Coordinator = null!;
        public long NextUs;

        public Kit() => Vm = new BrowseViewModel(Settings, Messenger);

        public EventCoordinationContext BuildContext()
        {
            var baseContext = TestFakes.MakeContext(Input);
            return baseContext with
            {
                OverlayManager = TestFakes.MakeOverlayManager(toolbar: Toolbar),
                InteractionManager = Im,
                Dispatcher = Dispatcher,
                Messenger = Messenger,
                Settings = Settings,
                BrowseVM = Vm,
            };
        }

        public void Initialize()
        {
            Coordinator = new BrowseCoordinator(() => Surface, () => Web);
            Coordinator.Initialize(BuildContext());
        }

        public void SendShow()
        {
            Messenger.Send(new BrowseShowRequestedMessage());
            Pump();
        }

        public void Pump() => Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    // ── sample feeding (the DwellDrive convention; DwellDrive itself lives in ViewModels.Tests) ──

    private static void Hold(WeakReferenceMessenger messenger, PhysicalPoint at, long fromUs, long toUs)
    {
        for (var t = fromUs; t < toUs; t += 30_000)
            messenger.Send(new GazeCursorMovedMessage(at, t));
        messenger.Send(new GazeCursorMovedMessage(at, toUs));
    }

    // ── tests ──



    [Fact]
    public void Summon_ShowsTheWindow_BindsTheVm_AndLoadsTheStartPageOnce()
    {
        var kit = new Kit();
        kit.Initialize();
        kit.SendShow();

        Check.That(kit.Surface.ShownVm).IsSameReferenceAs(kit.Vm);
        Check_THAT_VM_VISIBLE(kit);
        Check.That(kit.Web.NavigatedToString).HasSize(1);
        Check.That(kit.Web.NavigatedToString[0]).IsEqualTo(BrowseCoordinator.StartPageHtml);

        // All six chrome rects the surface reported became the VM's dwell targets — including
        // the address box (review R1: without it the address bar is unreachable by gaze).
        foreach (var (_, rect) in kit.Surface.ChromeRects)
            Check.That(kit.Vm.LiveTargetRects).Contains(rect);
        Check.That(kit.Vm.LiveTargetRects).HasSize(6);
    }

    private static void Check_THAT_VM_VISIBLE(Kit kit) => Check.That(kit.Vm.IsVisible).IsTrue();

    [Fact]
    public void Summon_Twice_NeverHides_ActivatesToFrontInstead()
    {
        var kit = new Kit();
        kit.Initialize();
        kit.SendShow();
        kit.SendShow();

        Check.That(kit.Vm.IsVisible).IsTrue();
        Check.That(kit.Surface.ActivateToFrontCalls).IsEqualTo(1);
        Check.That(kit.Web.NavigatedToString).HasSize(1); // no second start-page load
    }

    [Fact]
    public void WebElementActivation_IssuesExactlyOneLeftClick_AtTheMappedCenter()
    {
        var kit = new Kit();
        kit.Initialize();
        kit.SendShow();

        // The REAL envelope path: the fake host fires the page's JSON; the coordinator parses
        // it against the surface's origin (100,50) and applies the snapshot. css [10,20,30,40]
        // → physical (110,70,30,40), center (125,90).
        kit.Web.FireElement("""{"type":"elements","gen":1,"dpr":1,"els":[{"i":"a1","k":"button","r":[10,20,30,40]}]}""");
        var center = new PhysicalPoint(125, 90);

        Hold(kit.Messenger, center, 0, DwellMs * 1000L + 1);

        Check.That(kit.Input.BareClickCount).IsEqualTo(1);
        Check.That(kit.Input.LastBareClick).IsEqualTo(center);
    }

    [Fact]
    public void ChromeIds_RouteToNavigationHomeReloadCloseAndAddress()
    {
        var kit = new Kit();
        kit.Initialize();
        kit.SendShow();

        // The chrome targets arrive through the real surface geometry path (the summon's
        // TryRegisterChromeRects over the fake's six rects) — no manual registration here, so a
        // seam that drops the address box (review R1) fails this test by name.
        long t = 0;
        // Each hold spans dwell + cooldown + slack: the previous activation's cooldown freezes
        // accumulation until it lapses, so the next dwell's 700 ms must start after it.
        void DwellOn(PhysicalPoint at)
        {
            var span = (DwellMs + 400 + 200) * 1000L;
            Hold(kit.Messenger, at, t, t + span);
            t += span + 100_000;
        }

        DwellOn(new PhysicalPoint(524, 124)); // back
        Check.That(kit.Web.BackCalls).IsEqualTo(1);

        DwellOn(new PhysicalPoint(580, 124)); // forward
        Check.That(kit.Web.ForwardCalls).IsEqualTo(1);

        DwellOn(new PhysicalPoint(636, 124)); // home → re-navigates the start page
        Check.That(kit.Web.NavigatedToString).HasSize(2);
        Check.That(kit.Web.NavigatedToString[1]).IsEqualTo(BrowseCoordinator.StartPageHtml);

        DwellOn(new PhysicalPoint(692, 124)); // reload
        Check.That(kit.Web.ReloadCalls).IsEqualTo(1);

        DwellOn(new PhysicalPoint(808, 124)); // address → focus on the surface seam
        Check.That(kit.Surface.FocusAddressCalls).IsEqualTo(1);

        // Close LAST: it tears the surface down.
        DwellOn(new PhysicalPoint(924, 124)); // close
        Check.That(kit.Surface.CloseCalls).IsEqualTo(1);
        Check.That(kit.Web.DisposeCalls).IsEqualTo(1);
        Check.That(kit.Vm.IsVisible).IsFalse();
    }

    [Fact]
    public void HostFailed_ShowsTheNotice_ClosesTheWindow_AndDisposesTheHost()
    {
        var kit = new Kit();
        kit.Initialize();
        kit.SendShow();

        const string reason = "The WebView2 Evergreen runtime is not installed.";
        kit.Web.FireFailed(reason);

        // Review R3: the notice must be READABLE — the window stays open with the address-bar
        // reason and the chrome row (whose Close dwell is the exit); only the dead host is disposed.
        Check.That(kit.Surface.LastAddressError).IsEqualTo(reason);
        Check.That(kit.Surface.CloseCalls).IsEqualTo(0);
        Check.That(kit.Web.DisposeCalls).IsEqualTo(1);
        Check.That(kit.Vm.IsVisible).IsTrue();

        // The chrome Close dwell still tears the surface down from here.
        DwellOnChrome(kit, BrowseViewModel.ChromeCloseId);
        Check.That(kit.Surface.CloseCalls).IsEqualTo(1);
        Check.That(kit.Vm.IsVisible).IsFalse();
    }

    /// <summary>Dwells one chrome target through the real sample path (700 ms, 30 ms cadence).</summary>
    private static void DwellOnChrome(Kit kit, string id)
    {
        var rect = kit.Surface.ChromeRects.Single(r => r.Id == id).Rect;
        Hold(kit.Messenger, rect.Center, kit.NextUs, kit.NextUs + (DwellMs * 1000L) + 1);
        kit.NextUs += (DwellMs + 500) * 1000L;
    }

    [Fact]
    public void NavigationCompleted_ClearsTheWebTargets_ChromeSurvives()
    {
        var kit = new Kit();
        kit.Initialize();
        kit.SendShow();

        kit.Web.FireElement("""{"type":"elements","gen":1,"dpr":1,"els":[{"i":"a1","k":"button","r":[10,20,30,40]}]}""");
        Check.That(kit.Vm.LiveTargetRects).Contains(new PhysicalRect(110, 70, 30, 40));

        kit.Web.FireNavigation("https://example.test/", ok: true);

        Check.That(kit.Vm.LiveTargetRects).Not.Contains(new PhysicalRect(110, 70, 30, 40));
        // The chrome row survives the navigation.
        Check.That(kit.Vm.LiveTargetRects).Contains(new PhysicalRect(500, 100, 48, 48));
    }

    [Fact]
    public void ScrollStateFlip_SuspendsAndResumesDwell_IncludingFromANonUiThread()
    {
        var kit = new Kit();
        kit.Initialize();
        kit.SendShow();

        kit.Web.FireElement("""{"type":"elements","gen":1,"dpr":1,"els":[{"i":"a1","k":"button","r":[10,20,30,40]}]}""");
        var center = new PhysicalPoint(125, 90);

        // A REAL state edge, written from a NON-UI thread — the ScrollModeController hazard the
        // coordinator marshals for. ScrollMode is entered through the action menu (OnActionSelected
        // needs a target point, i.e. the menu-visible state); the whole edge runs on the worker,
        // so StateChanged fires off the UI thread and the suspension lands through BeginInvoke.
        var worker = new Thread(() =>
        {
            kit.Im.OnFixationStarted(new PhysicalPoint(100, 100));
            kit.Im.OnActivationButtonDwelled();
            kit.Im.OnActionSelected(InteractionAction.ScrollMode);
        });
        worker.Start();
        worker.Join();
        kit.Pump();
        Check.That(kit.Im.CurrentState).IsEqualTo(InteractionState.ScrollMode);

        Hold(kit.Messenger, center, 0, (DwellMs * 2 * 1000L) + 1); // far past the dwell time
        Check.That(kit.Input.BareClickCount).IsEqualTo(0);

        // Resume — a real edge again, again from the worker thread.
        worker = new Thread(() => kit.Im.ExitScrollMode());
        worker.Start();
        worker.Join();
        kit.Pump();

        Hold(kit.Messenger, center, (DwellMs * 2 * 1000L) + 1, (DwellMs * 4 * 1000L) + 1);
        Check.That(kit.Input.BareClickCount).IsEqualTo(1);
    }

    [Fact]
    public void VisibleToolbar_AtSummon_ReceivesRaiseToTop_HiddenDoesNot()
    {
        var visibleKit = new Kit();
        visibleKit.Toolbar.IsVisibleValue = true;
        visibleKit.Initialize();
        visibleKit.SendShow();
        Check.That(visibleKit.Toolbar.RaiseToTopCalled).IsTrue();

        var hiddenKit = new Kit();
        hiddenKit.Initialize();
        hiddenKit.SendShow();
        Check.That(hiddenKit.Toolbar.RaiseToTopCalled).IsFalse();
    }

    // ── review R6: the chrome cue is WPF-side, the page cue is for web ids only ──

    [Fact]
    public void DwellProgress_ChromeIds_DrawOnTheSurface_WebIds_PostToThePage()
    {
        var kit = new Kit();
        kit.Initialize();
        kit.SendShow();

        kit.Web.FireElement("""{"type":"elements","gen":1,"dpr":1,"els":[{"i":"web_1","k":"button","r":[10,20,30,40]}]}""");

        kit.Vm.RaiseDwellProgressForTest(BrowseViewModel.ChromeBackId, 0.5);
        kit.Vm.RaiseDwellProgressForTest("web_1", 0.25);
        kit.Vm.RaiseDwellProgressForTest(null, 0);

        Check.That(kit.Surface.ChromeProgress).Contains((BrowseViewModel.ChromeBackId, 0.5));
        Check.That(kit.Surface.ChromeProgress).Contains(((string? Id, double Progress))(null, 0));
        Check.That(kit.Web.Posted).HasSize(1); // only web_1 reached the page
        Check.That(kit.Web.Posted[0]).Contains("\"id\":\"web_1\"");
    }

    // ── review R7: Back/Forward gate on the web source's possibility notifications ──

    [Fact]
    public void BackImpossible_UnregistersTheBackTarget_UntilItReturns()
    {
        var kit = new Kit();
        kit.Initialize();
        kit.SendShow();

        var backRect = kit.Surface.ChromeRects.Single(r => r.Id == BrowseViewModel.ChromeBackId).Rect;
        kit.Web.FireBackPossibility(false);

        // The back target is gone: dwelling its rect completes nothing.
        Hold(kit.Messenger, backRect.Center, 0, (DwellMs * 2 * 1000L) + 1);
        Check.That(kit.Web.BackCalls).IsEqualTo(0);

        // History returns: the target comes back and completes.
        kit.Web.FireBackPossibility(true);
        Hold(kit.Messenger, backRect.Center, (DwellMs * 2 * 1000L) + 1, (DwellMs * 4 * 1000L) + 1);
        Check.That(kit.Web.BackCalls).IsEqualTo(1);
    }

    // ── review R5: a failed first chrome-rect read retries on the web source's Ready ──

    [Fact]
    public void FailedChromeRead_AtSummon_RetriesOnReady()
    {
        var kit = new Kit();
        kit.Surface.ChromeRectsAvailable = false;
        kit.Initialize();
        kit.SendShow();

        // The first read failed: no chrome targets yet (and no crash).
        Check.That(kit.Vm.LiveTargetRects).IsEmpty();

        // The runtime comes up (post-layout in production): the retry lands the chrome row.
        kit.Surface.ChromeRectsAvailable = true;
        kit.Web.FireReady();
        Check.That(kit.Vm.LiveTargetRects).HasSize(6);
    }
}
