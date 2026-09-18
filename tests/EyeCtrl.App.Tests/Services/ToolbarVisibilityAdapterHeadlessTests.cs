namespace EyeCtrl.App.Tests.Services;

using System.Collections.Generic;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// The same load-bearing ordering contract <see cref="ToolbarVisibilityAdapterTests"/> pins — dwell
/// must be deactivated BEFORE the toolbar hides, so a gaze sample arriving in the gap cannot
/// accumulate against targets that are no longer on screen — but headless.
/// <para>
/// <b>Why a second file rather than an edit.</b> The original tests construct real
/// <c>ToolbarWindow</c> and <c>PauseIndicatorWindow</c> instances, so they need an STA thread and
/// the process-wide <c>Application</c> singleton, which is why they carry
/// <c>[Trait("Category", "UI")]</c> — and CI runs <c>--filter "Category!=UI"</c>. **The ordering
/// contract was therefore never executed in CI at all.** Narrowing <c>IOverlayManager.Toolbar</c>
/// and <c>.PauseIndicator</c> to interfaces removed the only reason those windows were needed here;
/// the dwell observation was always headless. This file is the same assertions with no WPF window,
/// no Application singleton, no exclusive collection, and no UI trait — so it gates on every push.
/// </para>
/// <para>
/// The originals are kept: they additionally prove the real windows respond, which a fake cannot.
/// </para>
/// </summary>
public class ToolbarVisibilityAdapterHeadlessTests
{
    private sealed class FakeToolbar : IToolbarOverlay
    {
        private readonly List<string> _log;

        public FakeToolbar(List<string> log) => _log = log;

        public bool IsVisible { get; private set; } = true;
        public bool TargetsInitialized => true;

        // Slide state, modelled rather than stubbed. The adapter branches on IsSliding and
        // IsOnScreen, so a fake that always answered false would silently take one path forever
        // and the tests would look like they cover a decision they never make.
        public bool IsSliding { get; private set; }
        public bool IsOnScreen => IsVisible && !_slidingOut;

        // Stand-in thickness; nothing in the adapter branches on the value.
        public double ExtentDip => 132;

        private bool _slidingOut;

        public void Show() { IsVisible = true; _log.Add("Toolbar.Show"); }
        public void Hide() { IsVisible = false; _slidingOut = false; _log.Add("Toolbar.Hide"); }

        public void ShowAtRest()
        {
            IsVisible = true; IsSliding = false; _slidingOut = false;
            _log.Add("Toolbar.ShowAtRest");
        }

        public void ShowThenSlideIn()
        {
            IsVisible = true; IsSliding = true; _slidingOut = false;
            _log.Add("Toolbar.ShowThenSlideIn");
        }

        /// <summary>
        /// Runs the completion callback synchronously. The real window invokes it when the
        /// animation ends; running it inline keeps the test deterministic while still proving the
        /// adapter passes a callback and that whatever it does on hide actually happens.
        /// </summary>
        public void SlideOutThenHide(Action onHidden)
        {
            IsSliding = true; _slidingOut = true;
            _log.Add("Toolbar.SlideOutThenHide");
            IsSliding = false;
            IsVisible = false;
            _slidingOut = false;
            onHidden();
            _log.Add("Toolbar.SlideOutCompleted");
        }

        public void Initialize(double buttonSize, int dwellTimeMs) => _log.Add("Toolbar.Initialize");
        public void SetMode(bool isDocked, string edge) => _log.Add("Toolbar.SetMode");
        public void SetSelectionModeState(SelectionMode mode) => _log.Add("Toolbar.SetSelectionModeState");
        public void SetPausedState(bool isPaused) => _log.Add("Toolbar.SetPausedState");
        public void UpdateGaze(PhysicalPoint screenPoint, long timestampMicroseconds) { }
        public void BindToViewModel() => _log.Add("Toolbar.BindToViewModel");

        // A fake can answer this honestly, which four raw WPF layout properties could not.
        public PhysicalRect GetPhysicalBounds() => new(0, 1040, 1920, 40);
    }

    private sealed class FakePauseIndicator : IPauseIndicatorOverlay
    {
        private readonly List<string> _log;

        public FakePauseIndicator(List<string> log) => _log = log;

        public string? Edge { get; private set; }

        public void Show() => _log.Add("Indicator.Show");
        public void Hide() => _log.Add("Indicator.Hide");
        public void SetEdge(string edge) { Edge = edge; _log.Add("Indicator.SetEdge"); }
    }

    private static (ToolbarVisibilityAdapter Adapter, List<string> Log,
        ExposedDwellToolbarViewModel Vm, FakePauseIndicator Indicator) Build()
    {
        var log = new List<string>();
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ExposedDwellToolbarViewModel(settings, messenger);
        var indicator = new FakePauseIndicator(log);
        var overlays = TestFakes.MakeOverlayManager(toolbar: new FakeToolbar(log), pauseIndicator: indicator);
        vm.ProgressReset += () => log.Add("SetActive(false)");
        return (new ToolbarVisibilityAdapter(overlays, Dispatcher.CurrentDispatcher, vm), log, vm, indicator);
    }

    /// <summary>Runs the adapter's queued work. No STA thread required — nothing here is a Window.</summary>
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void HideToolbar_DeactivatesDwellBeforeHiding()
    {
        var (adapter, log, vm, _) = Build();

        // Put a target mid-dwell so SetActive(false)'s ResetAllState has something to reset —
        // that reset is how SetActive is observed without the method being virtual.
        vm.DwellDetector.AddTarget(new DwellTarget("Close", new PhysicalPoint(100, 100),
            radius: 50, dwellTimeMs: 1000));
        ToolbarGazeDrive.Stream(vm, 100, 100, 500_000);
        Check.That(vm.LastProgress).IsEqualTo(0.5);

        log.Clear();
        adapter.HideToolbar(paused: false);
        Pump();

        // The contract: deactivate, THEN start the band leaving. The slide made this MORE
        // load-bearing, not less — it widens the window between deactivation and the actual Hide()
        // from one dispatcher tick to the whole slide duration, so a sample arriving in the gap has
        // far longer to accumulate against targets that are on their way off screen.
        Check.That(log).Contains("SetActive(false)");
        Check.That(log).Contains("Toolbar.SlideOutThenHide");
        Check.That(log.IndexOf("SetActive(false)"))
            .IsStrictlyLessThan(log.IndexOf("Toolbar.SlideOutThenHide"));
        Check.That(vm.LastProgress).IsEqualTo(0.0);
    }

    [Fact]
    public void HideToolbar_WhilePaused_RaisesTheIndicator()
    {
        // Paused + hidden + no feedback is the spec violation this branch exists to prevent: the
        // user has no way to tell the app is paused rather than broken.
        var (adapter, log, _, _) = Build();

        adapter.HideToolbar(paused: true);
        Pump();

        Check.That(log).Contains("Indicator.Show");
        Check.That(log).Not.Contains("Indicator.Hide");
    }

    [Fact]
    public void HideToolbar_WhileRunning_LeavesTheIndicatorDown()
    {
        var (adapter, log, _, _) = Build();

        adapter.HideToolbar(paused: false);
        Pump();

        Check.That(log).Contains("Indicator.Hide");
        Check.That(log).Not.Contains("Indicator.Show");
    }

    [Fact]
    public void ShowToolbar_ShowsTheToolbarAndClearsTheIndicator()
    {
        var (adapter, log, vm, _) = Build();
        vm.SetActive(false);

        adapter.ShowToolbar();
        Pump();

        // A summon animates in, and the indicator is cleared FIRST so it is not left underneath
        // the incoming band.
        Check.That(log).Contains("Toolbar.ShowThenSlideIn");
        Check.That(log).Contains("Indicator.Hide");
        Check.That(log.IndexOf("Indicator.Hide"))
            .IsStrictlyLessThan(log.IndexOf("Toolbar.ShowThenSlideIn"));

        // Dwell is live again: gaze now accumulates where it previously could not.
        vm.DwellDetector.AddTarget(new DwellTarget("Close", new PhysicalPoint(100, 100),
            radius: 50, dwellTimeMs: 1000));
        ToolbarGazeDrive.Stream(vm, 100, 100, 500_000);
        Check.That(vm.LastProgress).IsEqualTo(0.5);
    }

    [Fact]
    public void ForceShow_ShowsTheToolbarAndClearsTheIndicator()
    {
        // The tray fallback and suspend-recover path — the one that un-strands a user whose
        // toolbar auto-hid while a modal was up.
        var (adapter, log, _, _) = Build();

        adapter.ForceShow();
        Pump();

        // INSTANT, and that is the contract worth pinning: every caller of ForceShow exists
        // because the user needs the toolbar now, so it must take ShowAtRest and never the
        // animated path. ShowAtRest also cancels any in-flight slide and defuses its pending Hide.
        Check.That(log).Contains("Toolbar.ShowAtRest");
        Check.That(log).Not.Contains("Toolbar.ShowThenSlideIn");
        Check.That(log).Contains("Indicator.Hide");
    }

    [Fact]
    public void PositionIndicator_ForwardsTheEdge()
    {
        var (adapter, _, _, indicator) = Build();

        adapter.PositionIndicator("Left");
        Pump();

        Check.That(indicator.Edge).IsEqualTo("Left");
    }
}
