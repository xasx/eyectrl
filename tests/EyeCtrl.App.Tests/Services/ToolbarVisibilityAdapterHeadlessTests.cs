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

        public bool Visible { get; private set; }

        public void Show() { Visible = true; _log.Add("Indicator.Show"); }
        public void Hide() { Visible = false; _log.Add("Indicator.Hide"); }
        public void RaiseToTop() => _log.Add("Indicator.RaiseToTop");
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
    public void SetPauseIndicator_True_ShowsAndRaises()
    {
        var (adapter, log, _, _) = Build();
        adapter.SetPauseIndicator(true);
        Pump();
        Check.That(log).Contains("Indicator.Show");
        Check.That(log).Contains("Indicator.RaiseToTop");
    }

    [Fact]
    public void SetPauseIndicator_IsEdgeGuarded()
    {
        var (adapter, log, _, _) = Build();
        adapter.SetPauseIndicator(true);
        adapter.SetPauseIndicator(true);
        Pump();
        Check.That(log.Count(t => t == "Indicator.Show")).IsEqualTo(1);
    }

    [Fact]
    public void SetPauseIndicator_UnchangedCommand_QueuesNoDispatcherItem()
    {
        var (adapter, log, _, _) = Build();
        adapter.SetPauseIndicator(true);
        Pump();
        Check.That(log.Count(t => t == "Indicator.Show")).IsEqualTo(1);

        // Issue #166 M8: the edge guard used to live only INSIDE the posted lambda, so every
        // gaze flip queued a dispatcher item just to return there — the cost class the
        // no-change guard at ToolbarAutoHideController.SetPaused documents as load-bearing.
        // The pre-check must refuse to POST at all when the command equals the last commanded
        // state, observed via the dispatcher's OperationPosted hook (subscribed after the
        // warm-up pump, unsubscribed before the closing pump so its own frame item is not
        // counted).
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var posted = 0;
        void Count(object? sender, System.Windows.Threading.DispatcherHookEventArgs e) => posted++;
        dispatcher.Hooks.OperationPosted += Count;
        try
        {
            adapter.SetPauseIndicator(true);
        }
        finally
        {
            dispatcher.Hooks.OperationPosted -= Count;
        }
        Pump();

        Check.WithCustomMessage(
                "An unchanged SetPauseIndicator command still queued a dispatcher item — the "
                + "per-gaze-flip no-op hop the M8 pre-check exists to remove.")
            .That(posted).IsEqualTo(0);
        Check.That(log.Count(t => t == "Indicator.Show")).IsEqualTo(1);
    }

    [Fact]
    public void Unpause_WhileBandVisible_HidesStrip()
    {
        var (adapter, log, _, _) = Build();
        adapter.SetPauseIndicator(true);
        adapter.SetPauseIndicator(false); // the H1 row: nothing else would dismiss the strip
        Pump();
        Check.That(log).Contains("Indicator.Hide");
        Check.That(log.Count(t => t == "Indicator.Hide")).IsEqualTo(1);
    }

    [Fact]
    public void Summon_WhileStripCommanded_RaisesIndicatorAfterTheBandShow()
    {
        var (adapter, log, _, _) = Build();
        adapter.SetPauseIndicator(true);
        Pump();      // the command must have RUN before the log is cleared
        log.Clear();
        adapter.ShowToolbar();
        Pump();
        var bandIndex = log.IndexOf("Toolbar.ShowThenSlideIn");
        var raiseIndex = log.IndexOf("Indicator.RaiseToTop");
        Check.That(raiseIndex).IsStrictlyGreaterThan(bandIndex);
    }

    [Fact]
    public void Summon_WithoutStrip_DoesNotRaise()
    {
        var (adapter, log, _, _) = Build();
        adapter.ShowToolbar();
        Pump();
        Check.That(log).Not.Contains("Indicator.RaiseToTop");
    }

    [Fact]
    public void HideToolbar_NeitherShowsNorHidesNorRaisesTheStrip()
    {
        var (adapter, log, _, _) = Build();
        adapter.SetPauseIndicator(true);
        Pump();      // the command must have RUN before the log is cleared
        log.Clear();
        adapter.HideToolbar(paused: true);
        Pump();
        Check.That(log.Where(t => t.StartsWith("Indicator."))).IsEmpty();
    }

    [Fact]
    public void ShowToolbar_SummonsTheBandAnimated_AndLeavesAnUncommandedStripAlone()
    {
        var (adapter, log, vm, _) = Build();
        vm.SetActive(false);

        adapter.ShowToolbar();
        Pump();

        // The summon animates in, and the strip is NOT the band show's business: with no
        // SetPauseIndicator(true) on record, a summon neither hides nor raises it.
        Check.That(log).Contains("Toolbar.ShowThenSlideIn");
        Check.That(log.Where(t => t.StartsWith("Indicator."))).IsEmpty();

        // Dwell is live again: gaze now accumulates where it previously could not.
        vm.DwellDetector.AddTarget(new DwellTarget("Close", new PhysicalPoint(100, 100),
            radius: 50, dwellTimeMs: 1000));
        ToolbarGazeDrive.Stream(vm, 100, 100, 500_000);
        Check.That(vm.LastProgress).IsEqualTo(0.5);
    }

    [Fact]
    public void ForceShow_ShowsTheBandAtRest_RaisingACommandedStripAfter()
    {
        // The tray fallback and suspend-recover path — the one that un-strands a user whose
        // toolbar auto-hid while a modal was up.
        var (adapter, log, _, _) = Build();
        adapter.SetPauseIndicator(true);
        Pump();      // the command must have RUN before the log is cleared
        log.Clear();

        adapter.ForceShow();
        Pump();

        // INSTANT, and that is the contract worth pinning: every caller of ForceShow exists
        // because the user needs the toolbar now, so it must take ShowAtRest and never the
        // animated path. ShowAtRest also cancels any in-flight slide and defuses its pending Hide.
        Check.That(log).Contains("Toolbar.ShowAtRest");
        Check.That(log).Not.Contains("Toolbar.ShowThenSlideIn");
        // The band show never hides the strip; a commanded strip is raised back above it.
        Check.That(log).Not.Contains("Indicator.Hide");
        var raiseIndex = log.IndexOf("Indicator.RaiseToTop");
        var bandIndex = log.IndexOf("Toolbar.ShowAtRest");
        Check.That(raiseIndex).IsStrictlyGreaterThan(bandIndex);
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
