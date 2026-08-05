namespace EyeCtrl.App.Tests.Services;

using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// <see cref="ToolbarVisibilityAdapter"/> is where the load-bearing ordering contract lives:
/// <c>HideToolbar</c> must deactivate dwell (SetActive(false)) BEFORE the toolbar window hides,
/// so a gaze sample arriving between Hide and the next dispatcher tick can't accumulate against
/// the now-hidden targets. ShowToolbar / ForceShow must activate dwell (SetActive(true)). These
/// tests pin that ordering and the activation side of the contract.
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
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class ToolbarVisibilityAdapterTests
{
    /// <summary>
    /// HideToolbar must call SetActive(false) BEFORE Toolbar.Hide(), so a gaze sample arriving in
    /// the gap between the two can't accumulate against the (now-hidden) targets.
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

            // Seed a dwell target and put it mid-dwell. Two gaze updates are required: the first
            // marks the target "inside" (records EnteredTimestamp, emits no progress); the second
            // accumulates and emits TargetProgress(id, 0.5). Route through Receive (the VM's dwell
            // gate) so the seed itself proves dwell is active going in.
            vm.DwellDetector.AddTarget(new DwellTarget("Close", new PhysicalPoint(100, 100),
                radius: 50, dwellTimeMs: 1000));
            vm.Receive(MakeGazeMessage(100, 100, 0));          // enter
            vm.Receive(MakeGazeMessage(100, 100, 500_000));    // 50% of the 1000ms dwell
            Check.That(vm.LastProgress).IsEqualTo(0.5);

            // Record the ordered sequence of side effects. The TargetProgress(id,0) reset fires
            // synchronously inside SetActive(false)'s ResetAllState; IsVisibleChanged fires
            // synchronously inside Window.Hide(). Capturing both into the same list preserves their
            // relative order — the load-bearing assertion of this test.
            var order = new List<string>();
            vm.ProgressReset += () => order.Add("SetActive(false)");
            toolbar.IsVisibleChanged += (_, _) =>
            {
                if (!toolbar.IsVisible) order.Add("Hide");
            };

            adapter.HideToolbar(paused: false);
            PumpDispatcher();

            // Both fired, and SetActive(false) preceded Hide.
            Check.That(order.Count).IsEqualTo(2);
            Check.That(order[0]).IsEqualTo("SetActive(false)");
            Check.That(order[1]).IsEqualTo("Hide");
            Check.That(toolbar.IsVisible).IsFalse();
            Check.That(vm.LastProgress).IsEqualTo(0.0); // dwell reset by SetActive(false)
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
            vm.Receive(MakeGazeMessage(100, 100, 0));
            vm.Receive(MakeGazeMessage(100, 100, 500_000));
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
            vm.Receive(MakeGazeMessage(100, 100, 0));
            vm.Receive(MakeGazeMessage(100, 100, 500_000));
            Check.That(vm.LastProgress).IsEqualTo(0.5);
            Check.That(toolbar.IsVisible).IsTrue();
        }
        finally
        {
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

        public ToolbarWindow Toolbar => _toolbar;
        public PauseIndicatorWindow PauseIndicator => _indicator;

        public CursorCircleWindow CursorCircle => throw new NotImplementedException();
        public ActivationButtonWindow ActivationButton => throw new NotImplementedException();
        public ManualSelectionOverlayWindow ManualSelectionOverlay => throw new NotImplementedException();
        public ActionMenuWindow ActionMenu => throw new NotImplementedException();
        public KeyboardWindow Keyboard => throw new NotImplementedException();
        public ManualSelectionGazeIndicatorWindow ManualSelectionIndicator => throw new NotImplementedException();
        public GlassBackdropWindow GlassBackdrop => throw new NotImplementedException();
        public CalibrationWindow Calibration => throw new NotImplementedException();
        public TargetHighlightWindow TargetHighlight => throw new NotImplementedException();
        public EdgeButtonBarWindow EdgeButtonBar => throw new NotImplementedException();
        public TrackStatusWindow TrackStatus => throw new NotImplementedException();
        public TaskSwitcherWindow TaskSwitcher => throw new NotImplementedException();
        public QuickActionsGridWindow QuickActionsGrid => throw new NotImplementedException();

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
