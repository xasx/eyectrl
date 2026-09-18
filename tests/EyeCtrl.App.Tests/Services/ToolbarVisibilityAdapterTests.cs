namespace EyeCtrl.App.Tests.Services;

using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// <see cref="ToolbarVisibilityAdapter"/> is where the load-bearing ordering contract lives:
/// <c>HideToolbar</c> must deactivate dwell (SetActive(false)) BEFORE the band starts sliding out,
/// and the window hides only when that slide completes — so no gaze sample can accumulate against
/// targets whose buttons are leaving or already gone. The slide widened that window from one
/// dispatcher tick to 200 ms, which is why the ordering matters more now, not less.
/// ShowToolbar / ForceShow must activate dwell (SetActive(true)). These tests pin that ordering
/// and the activation side of the contract.
///
/// The adapter is headlessly testable but takes concrete <see cref="IOverlayManager"/> +
/// <see cref="ToolbarViewModel"/>, neither of which exposes virtual hooks for Show/Hide/SetActive
/// (Window.Hide and ToolbarViewModel.SetActive are non-virtual). So activation is observed through
/// the real dwell machinery: a positioned target + a GazeCursorMovedMessage. ToolbarViewModel.Receive
/// gates on its private _dwellingActive flag (the exact flag SetActive flips), so progress reaching
/// the target proves SetActive(true) ran, and progress staying at 0 proves SetActive(false) ran.
/// For the ordering assertion, SetActive(false) is detected via DwellDetector.TargetProgress(id,0)
/// fired by ResetAllState, and Hide via the window's IsVisibleChanged event — both captured into one
/// ordered list.
///
/// Constructs real <c>ToolbarWindow</c> and <c>PauseIndicatorWindow</c> instances and Shows them,
/// and its own <see cref="EnsureApplicationWithTokens"/> creates the process-wide <c>Application</c>
/// singleton the same way <c>WpfApplicationGate</c> does — so this class must join the collection
/// that serialises every such caller against that singleton (see
/// <see cref="EyeCtrl.App.Tests.Fixtures.WpfApplicationCollection"/>); without it, Xunit.StaFact's
/// per-test STA thread can race another window-constructing test for the singleton.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[Collection("WpfApplication")]
public class ToolbarVisibilityAdapterTests
{
    /// <summary>
    /// HideToolbar must call SetActive(false) BEFORE the band starts sliding out, and Toolbar.Hide()
    /// only once that 200 ms slide completes — so a gaze sample arriving anywhere in that widened
    /// gap can't accumulate against targets whose buttons are leaving or already gone.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void HideToolbar_CallsSetActiveFalse_BeforeToolbarHide()
    {
        EnsureApplicationWithTokens();

        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ExposedDwellToolbarViewModel(settings, messenger);

        var toolbar = new ToolbarWindow();
        var indicator = new PauseIndicatorWindow();
        var overlays = new FakeOverlayManager(toolbar, indicator);
        var adapter = new ToolbarVisibilityAdapter(overlays, Dispatcher.CurrentDispatcher, vm);

        try
        {
            toolbar.Show();
            PumpDispatcher();
            Check.That(toolbar.IsVisible).IsTrue();

            // Dock it: the slide only runs on a docked toolbar. Without this the window is
            // floating, SlideVectorFor returns a zero vector, and SlideOutThenHide short-circuits
            // to a synchronous hide — which would make every assertion below pass while observing
            // none of the sequencing this test exists for. Initialize() is not needed; a
            // SizeToContent window is measured enough after Show() for a real extent.
            toolbar.SetMode(isDocked: true, edge: "Top");

            // Seed a dwell target and put it mid-dwell. Two gaze updates are required: the first
            // marks the target "inside" (records EnteredTimestamp, emits no progress); the second
            // accumulates and emits TargetProgress(id, 0.5). Route through Receive (the VM's dwell
            // gate) so the seed itself proves dwell is active going in.
            vm.DwellDetector.AddTarget(new DwellTarget("Close", new PhysicalPoint(100, 100),
                radius: 50, dwellTimeMs: 1000));
            ToolbarGazeDrive.Stream(vm, 100, 100, 500_000);     // 50% of the 1000ms dwell
            Check.That(vm.LastProgress).IsEqualTo(0.5);

            // Record the ordered sequence of side effects. TargetProgress(id,0) fires synchronously
            // inside SetActive(false)'s ResetAllState; IsVisibleChanged fires synchronously inside
            // Window.Hide(). Capturing both into one list preserves their relative order — the
            // load-bearing assertion of this test.
            var order = new List<string>();
            vm.ProgressReset += () => order.Add("SetActive(false)");
            toolbar.IsVisibleChanged += (_, _) =>
            {
                if (!toolbar.IsVisible) order.Add("Hide");
            };

            adapter.HideToolbar(paused: false);
            PumpDispatcher();

            // The contract moved but did not weaken: dwell is deactivated when the band STARTS
            // leaving, and the window hides when the slide finishes. A test STA thread has no
            // message pump, so the animation clock never advances here — the slide's completion is
            // driven through the seam, exactly as ToolbarSlideTests does.
            Check.That(order).ContainsExactly("SetActive(false)");
            Check.That(toolbar.IsVisible).IsTrue();          // still on screen, mid-slide
            Check.That(vm.LastProgress).IsEqualTo(0.0);      // dwell already reset

            toolbar.CompleteSlideForTest();
            PumpDispatcher();

            Check.That(order).ContainsExactly("SetActive(false)", "Hide");
            Check.That(toolbar.IsVisible).IsFalse();
        }
        finally
        {
            toolbar.Close();
            indicator.Close();
        }
    }

    /// <summary>
    /// ShowToolbar must activate dwell (SetActive(true)): after it runs, a gaze sample on a
    /// positioned target accumulates progress.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void ShowToolbar_CallsSetActiveTrue()
    {
        EnsureApplicationWithTokens();

        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ExposedDwellToolbarViewModel(settings, messenger);

        var toolbar = new ToolbarWindow();
        var indicator = new PauseIndicatorWindow();
        var overlays = new FakeOverlayManager(toolbar, indicator);
        var adapter = new ToolbarVisibilityAdapter(overlays, Dispatcher.CurrentDispatcher, vm);

        try
        {
            toolbar.Show();
            PumpDispatcher();

            // Deactivate dwell directly (the auto-hide hide path or calibration start would do this).
            vm.SetActive(false);
            vm.DwellDetector.AddTarget(new DwellTarget("Close", new PhysicalPoint(100, 100),
                radius: 50, dwellTimeMs: 1000));

            // While deactivated, gaze must NOT accumulate.
            vm.Receive(MakeGazeMessage(100, 100, 0));
            vm.Receive(MakeGazeMessage(100, 100, 500_000));
            Check.That(vm.LastProgress).IsEqualTo(0.0);

            adapter.ShowToolbar();
            PumpDispatcher();

            // After ShowToolbar re-activates dwell, gaze now accumulates.
            ToolbarGazeDrive.Stream(vm, 100, 100, 500_000);
            Check.That(vm.LastProgress).IsEqualTo(0.5);
            Check.That(toolbar.IsVisible).IsTrue();
        }
        finally
        {
            toolbar.Close();
            indicator.Close();
        }
    }

    /// <summary>
    /// ForceShow must activate dwell (SetActive(true)): after it runs, a gaze sample on a
    /// positioned target accumulates progress.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void ForceShow_CallsSetActiveTrue()
    {
        EnsureApplicationWithTokens();

        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ExposedDwellToolbarViewModel(settings, messenger);

        var toolbar = new ToolbarWindow();
        var indicator = new PauseIndicatorWindow();
        var overlays = new FakeOverlayManager(toolbar, indicator);
        var adapter = new ToolbarVisibilityAdapter(overlays, Dispatcher.CurrentDispatcher, vm);

        try
        {
            toolbar.Show();
            PumpDispatcher();

            vm.SetActive(false);
            vm.DwellDetector.AddTarget(new DwellTarget("Close", new PhysicalPoint(100, 100),
                radius: 50, dwellTimeMs: 1000));

            adapter.ForceShow();
            PumpDispatcher();

            // After ForceShow re-activates dwell, gaze accumulates.
            ToolbarGazeDrive.Stream(vm, 100, 100, 500_000);
            Check.That(vm.LastProgress).IsEqualTo(0.5);
            Check.That(toolbar.IsVisible).IsTrue();
        }
        finally
        {
            toolbar.Close();
            indicator.Close();
        }
    }

    /// <summary>
    /// The indicator is a handover, not a swap: it must appear only once the band has cleared.
    /// This is the only test covering HideToolbar's paused:true branch, and the show call now
    /// lives inside the slide's completion callback.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void HideToolbar_WhenPaused_ShowsTheIndicator_OnlyAfterTheSlideCompletes()
    {
        EnsureApplicationWithTokens();

        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ExposedDwellToolbarViewModel(settings, messenger);

        var toolbar = new ToolbarWindow();
        var indicator = new PauseIndicatorWindow();
        var overlays = new FakeOverlayManager(toolbar, indicator);
        var adapter = new ToolbarVisibilityAdapter(overlays, Dispatcher.CurrentDispatcher, vm);

        try
        {
            toolbar.Show();
            PumpDispatcher();
            toolbar.SetMode(isDocked: true, edge: "Top");

            adapter.HideToolbar(paused: true);
            PumpDispatcher();

            Check.That(indicator.IsVisible).IsFalse();   // band still on its way out

            toolbar.CompleteSlideForTest();
            PumpDispatcher();

            Check.That(indicator.IsVisible).IsTrue();
        }
        finally
        {
            toolbar.Close();
            indicator.Close();
        }
    }

    /// <summary>
    /// Spec B3 #5: VisibilityChanged fires once per real transition and not on re-asserts —
    /// SetPaused/ReassertVisibility call HideToolbar repeatedly, so an un-edge-detected event
    /// would fire continuously and the keyboard would churn. Also pins the UI-thread contract
    /// (fired inside the dispatcher lambda: a gaze-thread send would write window Left/Top off
    /// the UI thread, which this app turns into a process kill). Since the slide landed, the
    /// hide event fires inside SlideOutThenHide's completion callback — CompleteSlideForTest
    /// drives it the same way the paused-indicator fact above does.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void VisibilityChanged_FiresOncePerTransition_NotOnReasserts_AndOnUiThread()
    {
        EnsureApplicationWithTokens();

        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ExposedDwellToolbarViewModel(settings, messenger);
        var toolbar = new ToolbarWindow();
        var indicator = new PauseIndicatorWindow();
        var overlays = new FakeOverlayManager(toolbar, indicator);
        var adapter = new ToolbarVisibilityAdapter(overlays, Dispatcher.CurrentDispatcher, vm);

        var fired = new List<(int ThreadId, bool Visible)>();
        adapter.VisibilityChanged += visible => fired.Add((Environment.CurrentManagedThreadId, visible));

        try
        {
            toolbar.Show();
            PumpDispatcher();
            toolbar.SetMode(isDocked: true, edge: "Top");

            adapter.HideToolbar(paused: false);
            PumpDispatcher();
            toolbar.CompleteSlideForTest();
            PumpDispatcher();

            adapter.HideToolbar(paused: false);   // re-assert — must NOT fire again
            PumpDispatcher();
            toolbar.CompleteSlideForTest();
            PumpDispatcher();

            adapter.ShowToolbar();
            PumpDispatcher();

            adapter.ForceShow();                  // already visible — must NOT fire
            PumpDispatcher();

            Check.That(fired).HasSize(2);
            Check.That(fired[0].Visible).IsFalse();
            Check.That(fired[1].Visible).IsTrue();
            Check.That(fired.Select(f => f.ThreadId)).ContainsOnlyElementsThatMatch(
                id => id == Environment.CurrentManagedThreadId);
        }
        finally
        {
            toolbar.Close();
            indicator.Close();
        }
    }

    /// <summary>
    /// Spec §6 (summoned-dwellability) / B2 #3: a toolbar hidden and then shown while the
    /// keyboard is up must accumulate dwell on its buttons — the ShowToolbar messenger +
    /// SetActive(true) chain reactivates dwell and KeyboardVisible must not gate it. Progress
    /// is observed through the real DwellDetector, exactly like the sibling facts.
    /// </summary>
    [StaFact]
    [Trait("Category", "UI")]
    public void SummonedToolbar_AccumulatesDwell_WhileKeyboardVisible()
    {
        EnsureApplicationWithTokens();

        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ExposedDwellToolbarViewModel(settings, messenger);
        var modal = new ModalInteractionState { KeyboardVisible = true };
        var toolbar = new ToolbarWindow();
        var indicator = new PauseIndicatorWindow();
        var overlays = new FakeOverlayManager(toolbar, indicator);
        var adapter = new ToolbarVisibilityAdapter(overlays, Dispatcher.CurrentDispatcher, vm);

        var progress = new List<double>();
        vm.DwellDetector.TargetProgress += (id, p) => progress.Add(p);

        try
        {
            toolbar.Hide();
            PumpDispatcher();
            adapter.ShowToolbar();      // the summon transition — must reactivate dwell
            PumpDispatcher();

            vm.DwellDetector.AddTarget(new DwellTarget("Keyboard", new PhysicalPoint(100, 100),
                radius: 50, dwellTimeMs: 1000));
            ToolbarGazeDrive.Stream(vm, 100, 100, 500_000);     // 50% of the dwell

            Check.That(progress).HasElementThatMatches(p => p > 0.4);
        }
        finally
        {
            modal.KeyboardVisible = false;
            toolbar.Close();
            indicator.Close();
        }
    }

    // ── helpers ──

    private static GazeCursorMovedMessage MakeGazeMessage(double x, double y, long timestampUs)
        => new(new PhysicalPoint(x, y), timestampUs);

    private static void PumpDispatcher()
    {
        // Drain the dispatcher queue so the adapter's BeginInvoke callbacks (Show/Hide/SetActive)
        // have run before assertions. Mirrors ToolbarWindowRebindTests.PumpDispatcher.
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new DispatcherOperationCallback(_ => { frame.Continue = false; return null; }),
            null);
        Dispatcher.PushFrame(frame);
    }

    // Mirrors OverlayResourceResolutionTests.EnsureApplicationWithTokens — ToolbarWindow.xaml
    // references token brushes (BrushSurfaceDark etc.) merged at App.xaml startup; without an
    // Application holding the merged dictionary, the XAML parser throws on construction.
    private static void EnsureApplicationWithTokens()
    {
        if (Application.Current is null)
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        const string uri = "pack://application:,,,/EyeCtrl.Controls;component/Theme/OverlayStyles.xaml";
        var already = Application.Current!.Resources.MergedDictionaries
            .Any(d => d.Source?.ToString() == uri);
        if (!already)
            Application.Current.Resources.MergedDictionaries.Add(
                new ResourceDictionary { Source = new Uri(uri, UriKind.Absolute) });
    }

    /// <summary>
    /// Minimal IOverlayManager double exposing only Toolbar + PauseIndicator (all the adapter
    /// touches). Every other member throws — the adapter must not reach them, and throwing (not
    /// returning null!) surfaces any accidental reach as a test failure rather than an NRE.
    /// </summary>
    private sealed class FakeOverlayManager : IOverlayManager
    {
        private readonly ToolbarWindow _toolbar;
        private readonly PauseIndicatorWindow _indicator;

        public FakeOverlayManager(ToolbarWindow toolbar, PauseIndicatorWindow indicator)
        {
            _toolbar = toolbar;
            _indicator = indicator;
        }

        public IToolbarOverlay Toolbar => _toolbar;
        public IPauseIndicatorOverlay PauseIndicator => _indicator;

        public ICursorCircleOverlay CursorCircle => throw new NotImplementedException();
        public IActivationButtonOverlay ActivationButton => throw new NotImplementedException();
        public IManualSelectionOverlay ManualSelectionOverlay => throw new NotImplementedException();
        public IActionMenuOverlay ActionMenu => throw new NotImplementedException();
        public KeyboardWindow Keyboard => throw new NotImplementedException();
        public IManualSelectionIndicatorOverlay ManualSelectionIndicator => throw new NotImplementedException();
        public IGlassBackdropOverlay GlassBackdrop => throw new NotImplementedException();
        public ICalibrationOverlay Calibration => throw new NotImplementedException();
        public ITargetHighlightOverlay TargetHighlight => throw new NotImplementedException();
        public IEdgeButtonBar EdgeButtonBar => throw new NotImplementedException();
        public ITrackStatusOverlay TrackStatus => throw new NotImplementedException();
        public ITaskSwitcherOverlay TaskSwitcher => throw new NotImplementedException();
        public IModesMenuOverlay ModesMenu => null!;
        public IRepeatCursorOverlay RepeatCursor => null!;
        public IQuickActionsGridOverlay QuickActionsGrid => throw new NotImplementedException();

        public void Initialize(OverlayViewModels viewModels, EyeCtrlSettings settings, Core.Enums.GazeProviderType providerType)
            => throw new NotImplementedException();
    }
}

/// <summary>
/// ToolbarViewModel subclass that exposes the protected DwellDetector so the ordering test can
/// subscribe to TargetProgress (the observable side effect of SetActive(false)'s ResetAllState),
/// and captures the last reported progress. ToolbarViewModel.SetActive is non-virtual, so this
/// subclass adds observation hooks only — the real SetActive still runs unchanged. Top-level (not
/// nested) so the CommunityToolkit messenger source generator — which emits a partial RegisterAll
/// for inherited IRecipient&lt;&gt; types — can access it without an accessibility mismatch (CS0122).
/// </summary>
internal sealed class ExposedDwellToolbarViewModel : ToolbarViewModel
{
    public ExposedDwellToolbarViewModel(EyeCtrlSettings settings, IMessenger messenger)
        : base(settings, messenger)
    {
        DwellDetector.TargetProgress += OnProgress;
    }

    public new DwellDetector DwellDetector => base.DwellDetector;

    /// <summary>Last dwell progress reported for any target (0..1).</summary>
    public double LastProgress { get; private set; }

    /// <summary>Raised when a target's progress resets to 0 (SetActive(false)'s ResetAllState).</summary>
    public event Action? ProgressReset;

    private void OnProgress(string id, double progress)
    {
        LastProgress = progress;
        if (progress == 0.0) ProgressReset?.Invoke();
    }
}
