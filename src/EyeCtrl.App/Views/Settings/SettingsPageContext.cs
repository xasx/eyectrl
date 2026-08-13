namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using CommunityToolkit.Mvvm.Messaging;

/// <summary>
/// Everything a <see cref="ISettingsPageSource"/> needs to build its specs: the settings/services it
/// reads, and the view-bound delegates it invokes from action callbacks. The view-bound delegates
/// (ShowModal/ShowError/ApplyAndSave/RebuildContent/Invoke/Close/ReadClipboard) are the seam that keeps page sources
/// free of WPF/Dispatcher coupling — the shell supplies them.
/// </summary>
internal sealed class SettingsPageContext
{
    public EyeCtrlSettings Settings { get; }
    public SettingsWindowViewModel ViewModel { get; }
    public ModelRegistryService Registry { get; }
    public ModelManagementService ModelManagement { get; }
    public CalibrationService Calibration { get; }
    public IMessenger Messenger { get; }
    public AppRestartCoordinator Restart { get; }

    /// <param name="showModal"><c>(message, onYes)</c> — the two-button Yes/No modal. A thin wrapper
    /// over <see cref="ViewModel"/><c>.ShowModal(message, onYes)</c>; kept on the context for
    /// call-site readability. The General tab's three-choice modal bypasses it and calls
    /// <c>_ctx.ViewModel.ShowModal(message, choices)</c> directly (the only place that needs &gt;2 choices).</param>
    /// <param name="showError">Acknowledgement modal — the shell implements it as
    /// <c>ShowModal(message, () => {})</c>, so it is also reachable via the VM; kept for call-site clarity.</param>
    /// <param name="invoke">Marshals to the UI thread (Dispatcher.Invoke).</param>
    /// <param name="close">Closes the settings window (the Calibrate-Now flow closes before handing
    /// off to the calibration overlay). Defined up front so the context signature is stable across
    /// every task — do not add parameters to this ctor later without updating every test helper.</param>
    /// <param name="readClipboard">Reads the clipboard text, so <c>ModelsPageSource</c>'s custom-URL
    /// branch is headlessly testable instead of calling <c>System.Windows.Clipboard</c> directly
    /// (which would both leak WPF into the spec layer and make the invalid-URL/downloading branches
    /// untestable).</param>
    public SettingsPageContext(
        EyeCtrlSettings settings, SettingsWindowViewModel viewModel,
        ModelRegistryService registry, ModelManagementService modelManagement,
        CalibrationService calibration, IMessenger messenger, AppRestartCoordinator restart,
        Action<string, Action> showModal, Action<string> showError,
        Action applyAndSave, Action rebuildContent, Action<Action> invoke, Action close,
        Func<string> readClipboard)
    {
        Settings = settings;
        ViewModel = viewModel;
        Registry = registry;
        ModelManagement = modelManagement;
        Calibration = calibration;
        Messenger = messenger;
        Restart = restart;
        ShowModal = showModal;
        ShowError = showError;
        ApplyAndSave = applyAndSave;
        RebuildContent = rebuildContent;
        Invoke = invoke;
        Close = close;
        ReadClipboard = readClipboard;
    }

    public Action<string, Action> ShowModal { get; }
    public Action<string> ShowError { get; }
    public Action ApplyAndSave { get; }
    public Action RebuildContent { get; }
    public Action<Action> Invoke { get; }
    public Action Close { get; }
    public Func<string> ReadClipboard { get; }
}
