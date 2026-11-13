namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Browse;
using EyeCtrl.App.Overlays;
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
[Collection("WpfApplication")]
public class BrowseCoordinatorTests
{
    private const int DwellMs = 700; // ToolbarButtonDwellMs's default

    /// <summary>The page→app envelopes the tests fire carry the fake host's secret (review
    /// C3c) — an envelope without it fails the parse and registers nothing.</summary>
    private const string Secret = "test-bridge-secret";

    // ── fakes ──

    private sealed class FakeWebSource : IBrowseWebSource
    {
        public event Action<string>? ElementMessageReceived;
        public event Action? NavigationStarting;
        public event Action<string, bool>? NavigationCompleted;
        public event Action<bool>? BackPossibilityChanged;
        public event Action<bool>? ForwardPossibilityChanged;
        public event Action? Ready;
        public event Action<string>? Failed;

        public string BridgeSecret { get; } = "test-bridge-secret";

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
        public void FireNavigationStarting() => NavigationStarting?.Invoke();
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
        public PhysicalRect WebBounds { get; set; } = new(100, 50, 800, 600);
        public bool WebBoundsAvailable { get; set; } = true;
        public PhysicalRect WindowBounds { get; set; } = new(0, 0, 1920, 1040);
        public bool UnderPoint { get; set; } = true;

        /// <summary>r3 m6: the address dwell summons the gaze keyboard only when the surface
        /// holds the OS foreground — the pin flips this to false.</summary>
        public bool IsActive { get; set; } = true;

        /// <summary>Per-point override for the r2 M2 scenarios (the gaze point under the
        /// browser, the element's center under another surface).</summary>
        public Func<PhysicalPoint, bool>? UnderPointCheck { get; set; }
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

        public IBrowseWebSource? WebSource { get; set; }

        public (double Left, double Top, double Right, double Bottom)? Inset { get; private set; }

        public void InsetForDockedToolbar(double left, double top, double right, double bottom) =>
            Inset = (left, top, right, bottom);

        public event Action<Uri>? NavigateRequested;
        public event Action? SurfaceClosed;
        public event Action? LayoutChanged;

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

        public bool TryGetWebBounds(out PhysicalRect bounds)
        {
            bounds = WebBounds;
            return WebBoundsAvailable;
        }

        public bool TryGetWindowBounds(out PhysicalRect bounds)
        {
            bounds = WindowBounds;
            return true;
        }

        public bool IsUnderPoint(PhysicalPoint screenPoint) =>
            UnderPointCheck is { } check ? check(screenPoint) : UnderPoint;

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
        public void FireLayoutChanged() => LayoutChanged?.Invoke();
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
        public readonly ModalInteractionState ModalState = new();
        public readonly TestFakes.FakeCursorCircleOverlay Ring = new();
        public readonly BrowseViewModel Vm;
        public BrowseCoordinator Coordinator = null!;
        public long NextUs;

        public Kit() => Vm = new BrowseViewModel(Settings, Messenger);

        public EventCoordinationContext BuildContext()
        {
            var baseContext = TestFakes.MakeContext(Input);
            return baseContext with
            {
                OverlayManager = TestFakes.MakeOverlayManager(toolbar: Toolbar, cursorCircle: Ring),
                InteractionManager = Im,
                Dispatcher = Dispatcher,
                Messenger = Messenger,
                Settings = Settings,
                BrowseVM = Vm,
            };
        }

        public void Initialize()
        {
            Surface.WebSource = Web; // review C2: the surface's OWN host is the only one
            Coordinator = new BrowseCoordinator(() => Surface, ModalState);
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
        Check.That(kit.Web.NavigatedToString[0]).IsEqualTo(BrowseCoordinator.BuildStartPageHtml());

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
        kit.Web.FireElement($$"""{"type":"elements","n":"{{Secret}}","gen":1,"dpr":1,"els":[{"i":"a1","k":"button","r":[10,20,30,40]}]}""");
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
        Check.That(kit.Web.NavigatedToString[1]).IsEqualTo(BrowseCoordinator.BuildStartPageHtml());

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
    public void HostFailed_ShowsTheNotice_KeepsTheWindowOpen_AndDisposesTheHost()
    {
        var kit = new Kit();
        kit.Initialize();
        kit.SendShow();

        const string reason = "This PC is missing the browser component (WebView2). A helper can install it from Microsoft, then close and reopen the browser.";
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
    public void ReassertSurfacesAboveBrowser_RaisesToolbarAndRing_AfterSummonAndAfterTheAddressDwell()
    {
        // r3 I1: the address dwell's Activate() lifts the browser to the top of the z-order —
        // above a shown keyboard, its edge bar and the ring — exactly when the browser was NOT
        // the active window (the I5 case). The re-assertion must run on that path too, and the
        // ring raise must be observable (the kit now passes a recording ring fake; the
        // keyboard raise shares the toolbar's guard shape — the manager exposes the concrete
        // KeyboardWindow, so no recording fake exists for it without product churn).
        var kit = new Kit();
        kit.Toolbar.IsVisibleValue = true;
        kit.Initialize();
        kit.SendShow();
        var ringAfterSummon = kit.Ring.RaiseToTopCalls;
        Check.That(kit.Toolbar.RaiseToTopCalled).IsTrue();
        Check.That(ringAfterSummon).IsGreaterOrEqualThan(1);

        // The address dwell: FocusAddressBox Activate()s — the re-assertion follows it.
        kit.Toolbar.RaiseToTopCalled = false;
        var ringBefore = kit.Ring.RaiseToTopCalls;
        DwellOnChrome(kit, BrowseViewModel.ChromeAddressId);
        Check.That(kit.Toolbar.RaiseToTopCalled).IsTrue();
        Check.That(kit.Ring.RaiseToTopCalls).IsStrictlyGreaterThan(ringBefore);
    }

    [Fact]
    public void TheAddressDwell_SummonsTheKeyboardOnlyWhenTheSurfaceHoldsTheForeground()
    {
        // r3 m6: a refused Activate() leaves the OS foreground wherever it was — the gaze
        // keyboard's SendText would type into THAT app, unseen under the browser. The summon
        // runs only when the surface holds the foreground; the owner re-dwells.
        var kit = new Kit();
        var sources = new List<string>();
        kit.Messenger.Register<BrowserCoordinatorKeyboardProbe, KeyboardShowRequestedMessage>(
            new BrowserCoordinatorKeyboardProbe(), (_, m) => sources.Add(m.Source));
        kit.Initialize();
        kit.SendShow();

        kit.Surface.IsActive = false;
        DwellOnChrome(kit, BrowseViewModel.ChromeAddressId);
        Check.That(sources).IsEmpty();

        // Gaze exits the latched address box, so the re-dwell can fire (the leave-before-
        // re-arm rule).
        Hold(kit.Messenger, new PhysicalPoint(500, 500), kit.NextUs, kit.NextUs + 100_000);
        kit.NextUs += 150_000;

        kit.Surface.IsActive = true;
        DwellOnChrome(kit, BrowseViewModel.ChromeAddressId);
        Check.That(sources).ContainsExactly("browse-address");
    }

    [Fact]
    public void TheDockedToolbarInset_CoversAllFourEdges_AtBothCommonSizes_AndRestoresTheBaseWhenUndocked()
    {
        // r3 m3/m5: the inset was pinned only for Top/108. Every edge at both common sizes
        // reserves the same band arithmetic (Size + 2×ButtonMargin + 2×BandPaddingDip —
        // 108→140, 120→152), and an undocked toolbar restores the full work-area base.
        foreach (var size in new[] { 108.0, 120.0 })
        {
            var expected = size + 32;
            foreach (var (edge, l, t, r, b) in new[]
                     {
                         ("Top", 0.0, expected, 0.0, 0.0),
                         ("Bottom", 0.0, 0.0, 0.0, expected),
                         ("Left", expected, 0.0, 0.0, 0.0),
                         ("Right", 0.0, 0.0, expected, 0.0),
                     })
            {
                var kit = new Kit();
                kit.Settings.Toolbar.IsDocked = true;
                kit.Settings.Toolbar.DockedEdge = edge;
                kit.Settings.Toolbar.Size = size;
                kit.Initialize();
                kit.SendShow();
                Check.That(kit.Surface.Inset).IsEqualTo((l, t, r, b));
            }
        }

        var undocked = new Kit();
        undocked.Settings.Toolbar.IsDocked = false;
        undocked.Initialize();
        undocked.SendShow();
        Check.That(undocked.Surface.Inset).IsEqualTo((0.0, 0.0, 0.0, 0.0));
    }

    [Fact]
    public void AReSummon_AndALiveSettingsChange_ReDeriveTheInset()
    {
        // r3 m5: a dock change while the browser is up (Settings mid-browse) must not leave a
        // stale inset covering Close — the re-summon re-derives from the current settings,
        // and a live ToolbarSettings change re-applies while the browser sits up.
        var kit = new Kit();
        kit.Settings.Toolbar.IsDocked = true;
        kit.Settings.Toolbar.DockedEdge = "Top";
        kit.Settings.Toolbar.Size = 108;
        kit.Initialize();
        kit.SendShow();
        Check.That(kit.Surface.Inset).IsEqualTo((0.0, 140.0, 0.0, 0.0));

        // The live change: the coordinator re-applies on the settings' PropertyChanged.
        kit.Settings.Toolbar.DockedEdge = "Right";
        kit.Pump();
        Check.That(kit.Surface.Inset).IsEqualTo((0.0, 0.0, 140.0, 0.0));

        // The re-summon path re-derives too (idempotent from the window's stored base).
        kit.Settings.Toolbar.DockedEdge = "Left";
        kit.SendShow();
        Check.That(kit.Surface.Inset).IsEqualTo((140.0, 0.0, 0.0, 0.0));
    }

    [Fact]
    public void ALayoutEventAfterTeardown_IsANoOp()
    {
        // r3 m3 (M8's class): the closing window can still raise SizeChanged/LocationChanged —
        // Teardown detaches the surface's events, so a late LayoutChanged reaches nothing: no
        // re-registration, no occluder refill, no resurrection of the hidden VM.
        var kit = new Kit();
        kit.Initialize();
        kit.SendShow();
        DwellOnChrome(kit, BrowseViewModel.ChromeCloseId);

        Check.That(kit.Vm.IsVisible).IsFalse();
        var progressBefore = kit.Surface.ChromeProgress.Count;
        kit.Surface.FireLayoutChanged();
        Check.That(kit.Vm.IsVisible).IsFalse();
        Check.That(kit.Surface.ChromeProgress.Count).IsEqualTo(progressBefore);
    }

    [Fact]
    public void NavigationStarting_ClearsTheWebTargets_ChromeSurvives()
    {
        var kit = new Kit();
        kit.Initialize();
        kit.SendShow();

        kit.Web.FireElement($$"""{"type":"elements","n":"{{Secret}}","gen":1,"dpr":1,"els":[{"i":"a1","k":"button","r":[10,20,30,40]}]}""");
        Check.That(kit.Vm.LiveTargetRects).Contains(new PhysicalRect(110, 70, 30, 40));

        // Review I1: the clear rides NavigationStarting — NavigationCompleted fires after the
        // new page's first snapshot, and clearing there wiped it.
        kit.Web.FireNavigationStarting();

        Check.That(kit.Vm.LiveTargetRects).Not.Contains(new PhysicalRect(110, 70, 30, 40));
        // The chrome row survives the navigation.
        Check.That(kit.Vm.LiveTargetRects).Contains(new PhysicalRect(500, 100, 48, 48));

        // The new page's own first snapshot lands AFTER the clear and is NOT wiped by the
        // settled navigation (a static page's targets survive until the next navigation).
        kit.Web.FireElement($$"""{"type":"elements","n":"{{Secret}}","gen":2,"dpr":1,"els":[{"i":"a2","k":"button","r":[30,40,30,40]}]}""");
        kit.Web.FireNavigation("https://example.test/", ok: true);
        Check.That(kit.Vm.LiveTargetRects).Contains(new PhysicalRect(130, 90, 30, 40));
    }

    [Fact]
    public void ScrollStateFlip_SuspendsAndResumesDwell_IncludingFromANonUiThread()
    {
        var kit = new Kit();
        kit.Initialize();
        kit.SendShow();

        kit.Web.FireElement($$"""{"type":"elements","n":"{{Secret}}","gen":1,"dpr":1,"els":[{"i":"a1","k":"button","r":[10,20,30,40]}]}""");
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

        // Resume — a real edge again, again from the worker thread. Exiting ScrollMode returns
        // to the menu-visible state the mode was entered from; the shared suspension set (r2
        // I4) holds the browser there too, so the test dismisses the menu before the resume
        // hold — in production the user's gaze leaves the menu and the browser re-arms.
        worker = new Thread(() =>
        {
            kit.Im.ExitScrollMode();
            kit.Im.OnActionMenuDismissed();
        });
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

        kit.Web.FireElement($$"""{"type":"elements","n":"{{Secret}}","gen":1,"dpr":1,"els":[{"i":"web_1","k":"button","r":[10,20,30,40]}]}""");

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

    // ── external review round ──

    [Fact]
    public void AddressActivation_SummonsTheGazeKeyboard_OtherChromeDoesNot()
    {
        // Owner feedback 2026-11-05: dwelling the address box used to leave the keyboard hidden
        // — a second summon dwell before any typing. The address dwell now slides the keyboard
        // in with the focus (the SHOW message — never the toolbar's toggle, which would HIDE an
        // already-shown keyboard); no other chrome button drags the keyboard in.
        var kit = new Kit();
        var keyboardShows = 0;
        kit.Messenger.Register<BrowserCoordinatorKeyboardProbe, KeyboardShowRequestedMessage>(
            new BrowserCoordinatorKeyboardProbe(), (_, _) => keyboardShows++);
        kit.Initialize();
        kit.SendShow();

        DwellOnChrome(kit, BrowseViewModel.ChromeAddressId);
        Check.That(kit.Surface.FocusAddressCalls).IsEqualTo(1);
        Check.That(keyboardShows).IsEqualTo(1);

        DwellOnChrome(kit, BrowseViewModel.ChromeBackId);
        Check.That(keyboardShows).IsEqualTo(1); // navigation does not summon the keyboard
    }

    private sealed class BrowserCoordinatorKeyboardProbe
    {
    }

    // ── round-2 verdict (FIX-THEN-SHIP) ──

    [Fact]
    public void Summon_WithADockedToolbar_InsetsTheSurfaceByItsBand_AndANonDockedOneDoesNot()
    {
        // Review r2 I1: the docked toolbar's band and the chrome row share the work-area edge,
        // and the summon raises the toolbar above the browser — with the owner's settings
        // (top, size 108) Reload and most of the address field were covered. The surface is
        // inset by the band's thickness (size + the two 12-DIP margins + the 2×4 padding).
        var dockedKit = new Kit();
        dockedKit.Settings.Toolbar.IsDocked = true;
        dockedKit.Settings.Toolbar.DockedEdge = "Top";
        dockedKit.Settings.Toolbar.Size = 108;
        dockedKit.Initialize();
        dockedKit.SendShow();
        Check.That(dockedKit.Surface.Inset).IsEqualTo((0.0, 140.0, 0.0, 0.0));

        var floatingKit = new Kit();
        floatingKit.Settings.Toolbar.IsDocked = false;
        floatingKit.Initialize();
        floatingKit.SendShow();
        // r3 m5: undocked applies the zero inset — the window computes absolutely from its
        // work-area base, so the zero call is what restores the full base (and what un-docks
        // a live dock change back to).
        Check.That(floatingKit.Surface.Inset).IsEqualTo((0.0, 0.0, 0.0, 0.0));
    }

    [Fact]
    public void Resummon_ReassertsTheToolbarAboveTheBrowser()
    {
        // Review r2 I3: a resting gaze on the toolbar's Browse button re-summons about a
        // dwell+cooldown later, and the re-summon's ActivateToFront buried the toolbar — the
        // universal exit. Every summon re-asserts the surfaces that live above the browser.
        var kit = new Kit();
        kit.Toolbar.IsVisibleValue = true;
        kit.Initialize();
        kit.SendShow();
        Check.That(kit.Toolbar.RaiseToTopCalled).IsTrue();

        kit.Toolbar.RaiseToTopCalled = false;
        kit.SendShow();
        Check.WithCustomMessage("the re-summon buried the toolbar (r2 I3)")
            .That(kit.Toolbar.RaiseToTopCalled).IsTrue();
    }

    [Fact]
    public void PageActivation_ClicksAtTheGazePoint_WhenTheCenterIsUnderAnotherSurface()
    {
        // Review r2 M2: the clipped center can sit under another EyeCtrl surface (a tall
        // element partly under the keyboard) even though the gaze point does not — the click
        // falls back to the completing gaze point, which the under-point gate just proved is
        // the browser. The element's physical rect is (110,70,300,40): gaze (120,95) is inside
        // it, its center (260,90) is "under another surface" per the probe.
        var kit = new Kit();
        kit.Surface.UnderPointCheck = point => point.X < 200;
        kit.Initialize();
        kit.SendShow();

        kit.Web.FireElement($$"""{"type":"elements","n":"{{Secret}}","gen":1,"dpr":1,"els":[{"i":"a1","k":"button","r":[10,20,300,40]}]}""");
        Hold(kit.Messenger, new PhysicalPoint(120, 95), 0, DwellMs * 1000L + 1);

        Check.That(kit.Input.BareClickCount).IsEqualTo(1);
        Check.That(kit.Input.LastBareClick).IsEqualTo(new PhysicalPoint(120, 95));
    }

    [Fact]
    public void ManualSelectionState_SuspendsBrowseDwell()
    {
        // Review r2 I4: the under-point gate sees through click-through windows — the
        // ManualSelection magnifier is exactly that — so the states whose surfaces own the
        // screen gate the accumulation by the SAME predicate the note gate uses.
        var kit = new Kit();
        kit.Initialize();
        kit.SendShow();

        kit.Web.FireElement($$"""{"type":"elements","n":"{{Secret}}","gen":1,"dpr":1,"els":[{"i":"a1","k":"button","r":[10,20,30,40]}]}""");
        var center = new PhysicalPoint(125, 90);

        var worker = new Thread(() =>
        {
            kit.Im.SelectionMode = SelectionMode.ManualSelection;
            kit.Im.OnFixationStarted(new PhysicalPoint(100, 100));
            kit.Im.OnActivationButtonDwelled();
        });
        worker.Start();
        worker.Join();
        kit.Pump();
        Check.That(kit.Im.CurrentState).IsEqualTo(InteractionState.ManualSelectionActive);

        Hold(kit.Messenger, center, 0, DwellMs * 4 * 1000L + 1);
        Check.That(kit.Input.BareClickCount).IsEqualTo(0);
    }

    [Fact]
    public void AddressDwell_LabelsItsKeyboardSummon_BrowseAddress()
    {
        // Review r2 M10: the address dwell reuses the keyboard's show message; its E1 shown
        // line must record the browse-address source, not the ring's.
        var kit = new Kit();
        var sources = new List<string>();
        kit.Messenger.Register<BrowserCoordinatorKeyboardProbe, KeyboardShowRequestedMessage>(
            new BrowserCoordinatorKeyboardProbe(), (_, m) => sources.Add(m.Source));
        kit.Initialize();
        kit.SendShow();

        DwellOnChrome(kit, BrowseViewModel.ChromeAddressId);
        Check.That(sources).ContainsExactly("browse-address");
    }

    [Fact]
    public void TheCoordinatorWiring_TakesNoSecondWebSourceFactory()
    {
        // Review C2's regression class: the coordinator once built its OWN web source while the
        // window displayed another — the driven one was an orphan nobody could see. The ctor
        // takes no web-source factory any more; this pin fails the day one comes back.
        var coordinatorSource = File.ReadAllText(
            Path.Combine(SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Services", "EventCoordinator.cs"));
        Check.That(coordinatorSource.Contains("new BrowseCoordinator(")).IsTrue();
        Check.That(coordinatorSource.Contains("new WebView2BrowseHost(")).IsFalse();
    }

    [Fact]
    public void TheToolbarBrowseButton_SendsTheBrowseSummon()
    {
        // Owner feedback 2026-11-05: the action menu was not the right place for the browser's
        // summon — the toolbar is where Keyboard and TaskSwitcher live, and the Browse button
        // joined them there. This pin fails if the button's activation drifts away from the
        // same message the ring action sends (the coordinator's show-or-bring-to-front).
        var toolbarSource = File.ReadAllText(
            Path.Combine(SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.ViewModels", "ToolbarViewModel.cs"));
        Check.That(toolbarSource.Contains(
            "case \"Browse\": Messenger.Send(new BrowseShowRequestedMessage()); break;")).IsTrue();
    }

    [StaFact]
    public void TheKeyboardAndItsEdgeBar_NeverStealTheForegroundOnShow()
    {
        // Owner feedback 2026-11-05: summoning the keyboard un-focused the browser's address
        // box — WPF's Show() calls Activate() when ShowActivated is true, and that programmatic
        // activation ignores WS_EX_NOACTIVATE (the style every overlay already carries). The
        // keyboard types into whatever window IS foreground, so it must never BE the
        // foreground; its edge bar shows alongside it under the same contract. r3 m3: pinned
        // by CONSTRUCTION now (a `Contains` scan passes on a commented-out line) — both
        // windows set ShowActivated=false in their constructors and are built headlessly
        // here, never shown. The windows' XAML needs the app's token dictionary — the gate
        // merges it on demand (the InvisibleTestWindowTests precedent) — and the file carries
        // the WpfApplication collection (the isolation scan: the gate creates the process-wide
        // Application). The asserts run BEFORE InvisibleTestWindow.Prepare — Prepare itself
        // forces ShowActivated=false, so a post-Prepare assert would be vacuous.
        Fixtures.WpfApplicationGate.EnsureApplicationWithTokens();
        var keyboard = new KeyboardWindow();
        var edgeBar = new EdgeButtonBarWindow();
        Check.WithCustomMessage("the keyboard's Show must not activate — it types into the foreground window")
            .That(keyboard.ShowActivated).IsFalse();
        Check.WithCustomMessage("the keyboard's edge bar shows alongside it under the same no-activation contract")
            .That(edgeBar.ShowActivated).IsFalse();
        Fixtures.InvisibleTestWindow.Prepare(keyboard);
        Fixtures.InvisibleTestWindow.Prepare(edgeBar);
    }

    [Fact]
    public void ElementEnvelope_WithoutTheSecret_NeverRegisters()
    {
        // Review C3c: any page can post an elements envelope; without the host's per-summon
        // secret it fails the parse and no dwell target appears.
        var kit = new Kit();
        kit.Initialize();
        kit.SendShow();

        kit.Web.FireElement("""{"type":"elements","gen":1,"dpr":1,"els":[{"i":"evil","k":"button","r":[10,20,30,40]}]}""");

        Check.That(kit.Vm.LiveTargetRects).HasSize(6); // chrome only — the forged element is gone
    }

    [Fact]
    public void ElementEnvelope_BeyondTheCap_IsDroppedWholesale()
    {
        // Review C3b: AddTarget rebuilds the detector snapshot per add, so an uncapped
        // envelope is a UI-thread hang; the coordinator rejects beyond the script's cap.
        var kit = new Kit();
        kit.Initialize();
        kit.SendShow();

        var elements = string.Join(",", Enumerable.Range(1, BrowseElementScript.MaxElements + 1)
            .Select(i => $$"""{"i":"e{{i}}","k":"button","r":[10,20,30,40]}"""));
        kit.Web.FireElement($$"""{"type":"elements","n":"{{Secret}}","gen":1,"dpr":1,"els":[{{elements}}]}""");

        Check.That(kit.Vm.LiveTargetRects).HasSize(6); // chrome only
    }

    [Fact]
    public void ElementRects_OutsideTheWebBounds_AreDropped_AndPartialRects_AreClippedToTheWebView()
    {
        // Review C3a — the decisive half of the trust boundary: the ±100k clamp bounds
        // magnitudes, not locations, and the click is a real OS click at the rect's center.
        // Web bounds are (100,50,800,600), origin (100,50): css [700,20,200,40] straddles the
        // right edge → physical (800,70,200,40) clips to (800,70,100,40); css [2000,20,30,40]
        // is fully outside → dropped.
        var kit = new Kit();
        kit.Initialize();
        kit.SendShow();

        kit.Web.FireElement($$"""
            {"type":"elements","n":"{{Secret}}","gen":1,"dpr":1,"els":[
                {"i":"straddle","k":"button","r":[700,20,200,40]},
                {"i":"outside","k":"button","r":[2000,20,30,40]}]}
            """);

        Check.That(kit.Vm.LiveTargetRects).Contains(new PhysicalRect(800, 70, 100, 40));
        Check.That(kit.Vm.LiveTargetRects).Not.Contains(new PhysicalRect(2100, 70, 30, 40));

        // The click point is the CLIPPED rect's center, never the original one.
        Hold(kit.Messenger, new PhysicalPoint(850, 90), 0, DwellMs * 1000L + 1);
        Check.That(kit.Input.BareClickCount).IsEqualTo(1);
        Check.That(kit.Input.LastBareClick).IsEqualTo(new PhysicalPoint(850, 90));
    }

    [Fact]
    public void GazeUnderAnotherSurface_DoesNotAccumulate()
    {
        // Review I3: the keyboard/toolbar/ring can sit over the browser — a gaze point under
        // THOSE surfaces must not complete a page dwell (its click would land in the surface
        // on top).
        var kit = new Kit();
        kit.Initialize();
        kit.SendShow();

        kit.Web.FireElement($$"""{"type":"elements","n":"{{Secret}}","gen":1,"dpr":1,"els":[{"i":"a1","k":"button","r":[10,20,30,40]}]}""");
        var center = new PhysicalPoint(125, 90);

        kit.Surface.UnderPoint = false;
        Hold(kit.Messenger, center, 0, DwellMs * 4 * 1000L + 1);
        Check.That(kit.Input.BareClickCount).IsEqualTo(0);

        kit.Surface.UnderPoint = true;
        Hold(kit.Messenger, center, DwellMs * 4 * 1000L + 1, DwellMs * 6 * 1000L + 1);
        Check.That(kit.Input.BareClickCount).IsEqualTo(1);
    }

    [Fact]
    public void SurfaceLayoutChange_ReregistersTheChromeRow()
    {
        // Review M6: the chrome geometry was read once per summon — a moved/resized window
        // left stale rects until close and re-summon. A layout change now re-reads.
        var kit = new Kit();
        kit.Initialize();
        kit.SendShow();
        var oldCloseRect = kit.Surface.ChromeRects.Single(r => r.Id == BrowseViewModel.ChromeCloseId).Rect;

        kit.Surface.ChromeRects = kit.Surface.ChromeRects
            .Select(r => r.Id == BrowseViewModel.ChromeCloseId
                ? (r.Id, new PhysicalRect(940, 100, 48, 48))
                : r)
            .ToList();
        kit.Surface.FireLayoutChanged();

        Check.That(kit.Vm.LiveTargetRects).Contains(new PhysicalRect(940, 100, 48, 48));
        Check.That(kit.Vm.LiveTargetRects).Not.Contains(oldCloseRect);
    }

    [Fact]
    public void WindowBounds_FeedTheNoteOccluder_AndClearOnHide()
    {
        // Review I4: the note gate occludes with the window's WHOLE bounds, not the dwell
        // rects (a note outside the links would otherwise fire a click onto the page).
        var kit = new Kit();
        kit.Initialize();
        kit.SendShow();

        Check.That(kit.Vm.WindowOccluderRects).Contains(kit.Surface.WindowBounds);

        // Close tears everything down: the occluder empties with the window.
        DwellOnChrome(kit, BrowseViewModel.ChromeCloseId);
        Check.That(kit.Vm.WindowOccluderRects).IsEmpty();
    }
}
