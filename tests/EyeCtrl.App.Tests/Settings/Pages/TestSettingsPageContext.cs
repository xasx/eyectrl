namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Services;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using CommunityToolkit.Mvvm.Messaging;

/// <summary>
/// Builds a <see cref="SettingsPageContext"/> for headless page-source tests: real settings,
/// default-constructible services where they exist, no-op view delegates. Override only the deps a
/// source actually reads (e.g. <see cref="ModelsPageSource"/> passes registry+modelManagement,
/// <see cref="QuickActionsPageSource"/> passes viewModel+messenger).
/// </summary>
internal static class TestSettingsPageContext
{
    public static SettingsPageContext Build(
        EyeCtrlSettings? settings = null,
        SettingsWindowViewModel? viewModel = null,
        ModelRegistryService? registry = null,
        ModelManagementService? modelManagement = null,
        CalibrationService? calibration = null,
        IMessenger? messenger = null,
        Action<string, Action>? showModal = null,
        Func<string>? readClipboard = null) => new(
        settings ?? new EyeCtrlSettings(),
        viewModel!, registry!, modelManagement!,
        calibration ?? new CalibrationService(),
        messenger!,
        restart: null!,
        showModal: showModal ?? ((_, _) => { }),
        showError: _ => { },
        applyAndSave: () => { }, rebuildContent: () => { }, invoke: _ => { },
        close: () => { }, readClipboard: readClipboard ?? (() => ""));
}
