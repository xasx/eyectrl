namespace EyeCtrl.ViewModels;

using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// ViewModel for SettingsWindow that manages gaze/dwell interactions.
/// Wraps the existing SettingsViewModel to add UI interaction capabilities.
/// </summary>
public partial class SettingsWindowViewModel : ObservableObject,
    IRecipient<GazeCursorMovedMessage>,
    IDisposable
{
    private readonly SettingsViewModel _settingsViewModel;
    private readonly IMessenger _messenger;
    private readonly int _dwellMs;
    private bool _disposed;

    // 4 DwellDetectors for different UI sections
    private readonly DwellDetector _tabDwell;
    private readonly DwellDetector _controlDwell;
    private readonly DwellDetector _pagDwell;
    private readonly DwellDetector _modalDwell;

    // Observable properties for dwell progress (for visual feedback)
    [ObservableProperty] private Dictionary<string, double> _tabProgress = new();
    [ObservableProperty] private Dictionary<string, double> _controlProgress = new();
    [ObservableProperty] private Dictionary<string, double> _pagProgress = new();
    [ObservableProperty] private Dictionary<string, double> _modalProgress = new();

    // Modal state
    [ObservableProperty] private bool _isModalVisible;
    [ObservableProperty] private string _modalMessage = "";
    private Action? _modalYesAction;

    // Active tab for visual highlighting
    [ObservableProperty] private string _activeTabId = "";

    /// <summary>
    /// Exposes the underlying SettingsViewModel for data binding.
    /// </summary>
    public SettingsViewModel Settings => _settingsViewModel;

    public SettingsWindowViewModel(
        SettingsViewModel settingsViewModel,
        IMessenger messenger,
        DwellSettings dwellSettings)
    {
        _settingsViewModel = settingsViewModel;
        _messenger = messenger;
        _dwellMs = dwellSettings.ToolbarButtonDwellMs;

        // Initialize dwell detectors
        _tabDwell = new DwellDetector(dwellSettings.DwellCooldownMs);
        _controlDwell = new DwellDetector(dwellSettings.DwellCooldownMs);
        _pagDwell = new DwellDetector(dwellSettings.DwellCooldownMs);
        _modalDwell = new DwellDetector(dwellSettings.DwellCooldownMs);

        // Wire up event handlers
        _tabDwell.TargetActivated += OnTabActivated;
        _tabDwell.TargetProgress += OnTabProgress;

        _controlDwell.TargetActivated += OnControlActivated;
        _controlDwell.TargetProgress += OnControlProgress;

        _pagDwell.TargetActivated += OnPaginationActivated;
        _pagDwell.TargetProgress += OnPagProgress;

        _modalDwell.TargetActivated += OnModalActivated;
        _modalDwell.TargetProgress += OnModalProgress;

        // Register for gaze messages
        messenger.RegisterAll(this);
    }

    /// <summary>
    /// Receives gaze cursor movement and updates all dwell detectors.
    /// </summary>
    public void Receive(GazeCursorMovedMessage message)
    {
        if (_disposed) return;

        _tabDwell.Update(message.Position, message.TimestampMicroseconds);
        _controlDwell.Update(message.Position, message.TimestampMicroseconds);
        _pagDwell.Update(message.Position, message.TimestampMicroseconds);
        if (IsModalVisible)
            _modalDwell.Update(message.Position, message.TimestampMicroseconds);
    }

    #region Tab Management

    /// <summary>
    /// Adds a tab button as a dwell target.
    /// </summary>
    public void AddTabTarget(string tabId, double radius, int dwellMs)
    {
        _tabDwell.AddTarget(new DwellTarget(tabId,
            new PhysicalPoint(0, 0), radius, dwellMs));
    }

    /// <summary>
    /// Updates the screen position of a tab button for dwell detection.
    /// </summary>
    public void UpdateTabButtonPosition(string tabId, PhysicalPoint center)
    {
        _tabDwell.UpdateTargetCenter(tabId, center);
    }

    private void OnTabActivated(string tabId)
    {
        if (tabId == "close")
        {
            _messenger.Send(new CloseSettingsMessage());
            return;
        }

        _settingsViewModel.ShowTab(tabId);
        ActiveTabId = tabId;
    }

    private void OnTabProgress(string tabId, double progress)
    {
        TabProgress[tabId] = progress;
    }

    #endregion

    #region Pagination Management

    /// <summary>
    /// Adds a pagination button as a dwell target.
    /// </summary>
    public void AddPaginationTarget(string targetId, double radius, int dwellMs)
    {
        _pagDwell.AddTarget(new DwellTarget(targetId,
            new PhysicalPoint(0, 0), radius, dwellMs));
    }

    /// <summary>
    /// Updates the screen position of a pagination button for dwell detection.
    /// </summary>
    public void UpdatePaginationButtonPosition(string targetId, PhysicalPoint center)
    {
        _pagDwell.UpdateTargetCenter(targetId, center);
    }

    private void OnPaginationActivated(string targetId)
    {
        if (targetId == "pag_prev")
            _settingsViewModel.PreviousPage();
        else if (targetId == "pag_next")
            _settingsViewModel.NextPage();
    }

    private void OnPagProgress(string targetId, double progress)
    {
        PagProgress[targetId] = progress;
    }

    #endregion

    #region Control Management

    /// <summary>
    /// Adds a control button as a dwell target.
    /// </summary>
    public void AddControlTarget(string controlId, double radius, int dwellMs)
    {
        _controlDwell.AddTarget(new DwellTarget(controlId,
            new PhysicalPoint(0, 0), radius, dwellMs));
    }

    /// <summary>
    /// Updates the screen position of a control for dwell detection.
    /// </summary>
    public void UpdateControlPosition(string controlId, PhysicalPoint center)
    {
        _controlDwell.UpdateTargetCenter(controlId, center);
    }

    private void OnControlActivated(string controlId)
    {
        // Control activation logic will be handled by SettingsWindow
        // for now, as it requires specific UI interaction patterns
        // This will be expanded in future iterations
    }

    private void OnControlProgress(string controlId, double progress)
    {
        ControlProgress[controlId] = progress;
    }

    #endregion

    #region Modal Management

    /// <summary>
    /// Shows a modal confirmation dialog.
    /// </summary>
    public void ShowModal(string message, Action onYes)
    {
        ModalMessage = message;
        _modalYesAction = onYes;
        IsModalVisible = true;

        // Add modal button targets
        _modalDwell.AddTarget(new DwellTarget("modal_yes",
            new PhysicalPoint(0, 0), 70, _dwellMs));
        _modalDwell.AddTarget(new DwellTarget("modal_no",
            new PhysicalPoint(0, 0), 70, _dwellMs));
    }

    /// <summary>
    /// Hides the modal dialog.
    /// </summary>
    public void HideModal()
    {
        IsModalVisible = false;
        ModalMessage = "";
        _modalDwell.ClearTargets();
        _modalYesAction = null;
    }

    /// <summary>
    /// Updates the screen position of a modal button for dwell detection.
    /// </summary>
    public void UpdateModalButtonPosition(string buttonId, PhysicalPoint center)
    {
        _modalDwell.UpdateTargetCenter(buttonId, center);
    }

    /// <summary>For testing: simulate a modal button activation without gaze dwell.</summary>
    public void SimulateModalButtonActivated(string buttonId) => OnModalActivated(buttonId);

    private void OnModalActivated(string targetId)
    {
        if (targetId == "modal_yes")
        {
            _modalYesAction?.Invoke();
            HideModal();
        }
        else if (targetId == "modal_no")
        {
            HideModal();
        }
    }

    private void OnModalProgress(string targetId, double progress)
    {
        ModalProgress[targetId] = progress;
    }

    #endregion

    public void Dispose()
    {
        if (_disposed)
            return;

        _messenger.UnregisterAll(this);
        _settingsViewModel.Dispose();
        _disposed = true;
    }
}
