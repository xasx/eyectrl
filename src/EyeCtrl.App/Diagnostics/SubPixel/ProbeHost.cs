namespace EyeCtrl.App.Diagnostics.SubPixel;

using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// The production <see cref="IProbeHost"/> (build plan C14): the thin adapter from C7's effects to
/// the real ring, engine, toolbar, keyboard and summon. Every method runs synchronously on the UI
/// thread unless its target posts internally; the one exception is <see cref="CaptureBackdrop"/>,
/// which is also the only method that touches the desktop.
/// </summary>
internal sealed class ProbeHost : IProbeHost
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    private readonly ModalInteractionState _modal;
    private readonly GazeEngine _engine;
    private readonly ICursorCircleOverlay _cursorCircle;
    private readonly CursorCircleViewModel _cursorVm;
    private readonly ToolbarViewModel _toolbarVm;
    private readonly ToolbarLifecycleCoordinator _toolbar;
    private readonly KeyboardCoordinator _keyboard;
    private readonly QuickActionsCoordinator _quickActions;
    private readonly ModesMenuViewModel _modesMenuVm;
    private readonly TaskSwitcherViewModel _taskSwitcherVm;
    private readonly QuickActionsGridViewModel _quickActionsGridVm;
    private readonly IMessenger _messenger;
    private readonly EyeCtrlSettings _settings;

    public ProbeHost(
        ModalInteractionState modal,
        GazeEngine engine,
        ICursorCircleOverlay cursorCircle,
        CursorCircleViewModel cursorVm,
        ToolbarViewModel toolbarVm,
        ToolbarLifecycleCoordinator toolbar,
        KeyboardCoordinator keyboard,
        QuickActionsCoordinator quickActions,
        ModesMenuViewModel modesMenuVm,
        TaskSwitcherViewModel taskSwitcherVm,
        QuickActionsGridViewModel quickActionsGridVm,
        IMessenger messenger,
        EyeCtrlSettings settings)
    {
        _modal = modal ?? throw new ArgumentNullException(nameof(modal));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _cursorCircle = cursorCircle ?? throw new ArgumentNullException(nameof(cursorCircle));
        _cursorVm = cursorVm ?? throw new ArgumentNullException(nameof(cursorVm));
        _toolbarVm = toolbarVm ?? throw new ArgumentNullException(nameof(toolbarVm));
        _toolbar = toolbar ?? throw new ArgumentNullException(nameof(toolbar));
        _keyboard = keyboard ?? throw new ArgumentNullException(nameof(keyboard));
        _quickActions = quickActions ?? throw new ArgumentNullException(nameof(quickActions));
        _modesMenuVm = modesMenuVm ?? throw new ArgumentNullException(nameof(modesMenuVm));
        _taskSwitcherVm = taskSwitcherVm ?? throw new ArgumentNullException(nameof(taskSwitcherVm));
        _quickActionsGridVm = quickActionsGridVm ?? throw new ArgumentNullException(nameof(quickActionsGridVm));
        _messenger = messenger ?? throw new ArgumentNullException(nameof(messenger));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <inheritdoc/>
    public void SetProbeOpen(bool open) => _modal.ProbeOpen = open;

    /// <inheritdoc/>
    public bool IsRingVisible => _cursorVm.IsVisible;

    /// <inheritdoc/>
    public void HideRing() => _cursorCircle.Hide();

    /// <inheritdoc/>
    public void ShowRingIfVisible()
    {
        if (_cursorVm.IsVisible) _cursorCircle.Show();
    }

    /// <inheritdoc/>
    public void AcquireProbeSuppression() =>
        _engine.AcquireFixationSuppression(FixationSuppressor.SubPixelProbe);

    /// <inheritdoc/>
    public void ReleaseProbeSuppression() =>
        _engine.ReleaseFixationSuppression(FixationSuppressor.SubPixelProbe);

    /// <inheritdoc/>
    public void DeactivateToolbar() => _toolbar.SetActive(false);

    /// <inheritdoc/>
    public void AssertToolbarInactive() => _toolbarVm.SetActive(false);

    /// <inheritdoc/>
    public void ReassertToolbarVisibility() => _toolbar.ReassertVisibility();

    /// <inheritdoc/>
    public bool KeyboardVisible => _modal.KeyboardVisible;

    /// <inheritdoc/>
    public void HideKeyboard() => _keyboard.HideForProbe();

    /// <inheritdoc/>
    public void RestoreKeyboard(bool wasVisible) => _keyboard.RestoreAfterProbe(wasVisible);

    /// <inheritdoc/>
    public void SuspendSummon() => _quickActions.SetSummonSuspended(true);

    /// <inheritdoc/>
    public void ResumeSummon() => _quickActions.SetSummonSuspended(false);

    /// <inheritdoc/>
    public bool AnyMenuVisible =>
        _modesMenuVm.IsVisible || _taskSwitcherVm.IsVisible || _quickActionsGridVm.IsVisible;

    /// <inheritdoc/>
    public BitmapSource? CaptureBackdrop()
    {
        // Flush DirectComposition first so the capture sees the frame the user last saw (build plan
        // C7 step 3). DwmFlush blocks until the composition queue is empty; failure is non-fatal.
        DwmFlush();
        return ScreenCaptureService.CaptureFullScreen();
    }

    /// <inheritdoc/>
    public SubPixelProbeViewModel CreateViewModel(
        SubPixelAbSchedule schedule,
        TimeProvider timeProvider,
        Func<bool> isValidSample,
        SubPixelProbeStartMode startMode,
        SubPixelAbSession1Verdict? resumedSession1Verdict) =>
        new(_messenger, timeProvider, schedule, _settings.Dwell, isValidSample,
            startMode, resumedSession1Verdict);

    /// <inheritdoc/>
    public void DisposeViewModel(SubPixelProbeViewModel vm) => vm.Dispose();

    /// <inheritdoc/>
    public IProbeWindow CreateWindow(
        BitmapSource? backdrop,
        SubPixelProbeViewModel vm,
        CursorSettings cursorSettings,
        Action sessionEnded) =>
        new SubPixelProbeWindow(backdrop, vm, cursorSettings, sessionEnded);
}
