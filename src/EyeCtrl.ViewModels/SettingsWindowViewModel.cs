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
/// Implements IDwellRegistrar to provide a unified target registration API.
/// </summary>
public partial class SettingsWindowViewModel : ObservableObject,
    IRecipient<GazeCursorMovedMessage>,
    IDwellRegistrar,
    IDisposable
{
    private readonly SettingsViewModel _settingsViewModel;
    private readonly IMessenger _messenger;
    private bool _disposed;

    // 4 DwellDetectors for different UI sections
    private readonly DwellDetector _tabDwell;
    private readonly DwellDetector _controlDwell;
    private readonly DwellDetector _pagDwell;
    private readonly DwellDetector _modalDwell;

    // Action dictionaries per category
    private readonly Dictionary<string, Action> _tabActions = new();
    private readonly Dictionary<string, Action> _controlActions = new();
    private readonly Dictionary<string, Action> _pagActions = new();
    private readonly Dictionary<string, Action> _modalActions = new();

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

        // Initialize dwell detectors
        _tabDwell = new DwellDetector(dwellSettings.DwellCooldownMs);
        _controlDwell = new DwellDetector(dwellSettings.DwellCooldownMs);
        _pagDwell = new DwellDetector(dwellSettings.DwellCooldownMs);
        _modalDwell = new DwellDetector(dwellSettings.DwellCooldownMs);

        // Wire up unified event handlers
        _tabDwell.TargetActivated += id => OnTargetActivated(id, DwellCategory.Tab);
        _tabDwell.TargetProgress += (id, p) => OnTargetProgress(id, p, DwellCategory.Tab);

        _controlDwell.TargetActivated += id => OnTargetActivated(id, DwellCategory.Control);
        _controlDwell.TargetProgress += (id, p) => OnTargetProgress(id, p, DwellCategory.Control);

        _pagDwell.TargetActivated += id => OnTargetActivated(id, DwellCategory.Pagination);
        _pagDwell.TargetProgress += (id, p) => OnTargetProgress(id, p, DwellCategory.Pagination);

        _modalDwell.TargetActivated += id => OnTargetActivated(id, DwellCategory.Modal);
        _modalDwell.TargetProgress += (id, p) => OnTargetProgress(id, p, DwellCategory.Modal);

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

    #region IDwellRegistrar Implementation

    /// <summary>
    /// Registers a dwell target with the appropriate detector for the given category.
    /// </summary>
    public void Register(string id, DwellCategory category, double radiusPhysical, int dwellMs, Action onActivated)
    {
        GetActions(category)[id] = onActivated;
        GetDetector(category).AddTarget(new DwellTarget(id, new PhysicalPoint(0, 0), radiusPhysical, dwellMs));
    }

    /// <summary>
    /// Removes a dwell target and its associated action from the given category.
    /// </summary>
    public void Unregister(string id, DwellCategory category)
    {
        GetActions(category).Remove(id);
        GetDetector(category).RemoveTarget(id);
        GetProgressDict(category).Remove(id);
        OnPropertyChanged(GetProgressPropertyName(category));
    }

    /// <summary>
    /// Updates the screen-space center position of a registered target.
    /// </summary>
    public void UpdatePosition(string id, DwellCategory category, PhysicalPoint center)
    {
        GetDetector(category).UpdateTargetCenter(id, center);
    }

    /// <summary>
    /// Returns the current dwell progress (0.0–1.0) for a registered target.
    /// </summary>
    public double GetProgress(string id, DwellCategory category)
    {
        return GetDetector(category).GetProgress(id);
    }

    /// <summary>
    /// Clears all targets and actions for the given category.
    /// </summary>
    public void ClearCategory(DwellCategory category)
    {
        GetDetector(category).ClearTargets();
        GetActions(category).Clear();
        GetProgressDict(category).Clear();
        OnPropertyChanged(GetProgressPropertyName(category));
    }

    /// <summary>
    /// Triggers the activation logic for a registered target without requiring a gaze dwell.
    /// Useful for testing and programmatic activation.
    /// </summary>
    public void SimulateActivated(string id, DwellCategory category)
    {
        OnTargetActivated(id, category);
    }

    #endregion

    #region Unified Event Handlers

    private void OnTargetActivated(string id, DwellCategory category)
    {
        switch (category)
        {
            case DwellCategory.Tab:
                if (id == "close")
                {
                    _messenger.Send(new CloseSettingsMessage());
                    return;
                }
                _settingsViewModel.ShowTab(id);
                ActiveTabId = id;
                if (_tabActions.TryGetValue(id, out var tabAction))
                    tabAction.Invoke();
                break;

            case DwellCategory.Pagination:
                if (id == "pag_prev")
                    _settingsViewModel.PreviousPage();
                else if (id == "pag_next")
                    _settingsViewModel.NextPage();
                if (_pagActions.TryGetValue(id, out var pagAction))
                    pagAction.Invoke();
                break;

            case DwellCategory.Modal:
                if (id == "modal_yes")
                {
                    _modalYesAction?.Invoke();
                    HideModal();
                }
                else if (id == "modal_no")
                {
                    HideModal();
                }
                // Modal uses _modalYesAction directly, not _modalActions dict — return immediately
                return;

            case DwellCategory.Control:
                if (_controlActions.TryGetValue(id, out var controlAction))
                    controlAction.Invoke();
                break;
        }
    }

    private void OnTargetProgress(string id, double progress, DwellCategory category)
    {
        var dict = GetProgressDict(category);
        dict[id] = progress;
        OnPropertyChanged(GetProgressPropertyName(category));
    }

    #endregion

    #region Helper Methods

    private DwellDetector GetDetector(DwellCategory category) => category switch
    {
        DwellCategory.Tab => _tabDwell,
        DwellCategory.Control => _controlDwell,
        DwellCategory.Pagination => _pagDwell,
        DwellCategory.Modal => _modalDwell,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
    };

    private Dictionary<string, Action> GetActions(DwellCategory category) => category switch
    {
        DwellCategory.Tab => _tabActions,
        DwellCategory.Control => _controlActions,
        DwellCategory.Pagination => _pagActions,
        DwellCategory.Modal => _modalActions,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
    };

    private Dictionary<string, double> GetProgressDict(DwellCategory category) => category switch
    {
        DwellCategory.Tab => TabProgress,
        DwellCategory.Control => ControlProgress,
        DwellCategory.Pagination => PagProgress,
        DwellCategory.Modal => ModalProgress,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
    };

    private static string GetProgressPropertyName(DwellCategory category) => category switch
    {
        DwellCategory.Tab => nameof(TabProgress),
        DwellCategory.Control => nameof(ControlProgress),
        DwellCategory.Pagination => nameof(PagProgress),
        DwellCategory.Modal => nameof(ModalProgress),
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
    };

    #endregion

    #region Modal Management

    /// <summary>
    /// Shows a modal confirmation dialog.
    /// </summary>
    public void ShowModal(string message, Action onYes)
    {
        // Clear any existing modal state
        _modalDwell.ClearTargets();
        _modalActions.Clear();
        ModalProgress.Clear();
        OnPropertyChanged(nameof(ModalProgress));

        ModalMessage = message;
        _modalYesAction = onYes;
        IsModalVisible = true;
    }

    /// <summary>
    /// Hides the modal dialog.
    /// </summary>
    public void HideModal()
    {
        IsModalVisible = false;
        ModalMessage = "";
        _modalDwell.ClearTargets();
        _modalActions.Clear();
        _modalYesAction = null;
    }

    #endregion

    #region Legacy Wrapper Methods (kept for backward compatibility during migration)

    /// <summary>
    /// Adds a tab button as a dwell target.
    /// </summary>
    public void AddTabTarget(string tabId, double radius, int dwellMs)
    {
        // Empty action: tab activation logic is handled in OnTargetActivated's Tab case
        Register(tabId, DwellCategory.Tab, radius, dwellMs, () => { });
    }

    /// <summary>
    /// Updates the screen position of a tab button for dwell detection.
    /// </summary>
    public void UpdateTabButtonPosition(string tabId, PhysicalPoint center)
    {
        UpdatePosition(tabId, DwellCategory.Tab, center);
    }

    /// <summary>
    /// Adds a pagination button as a dwell target.
    /// </summary>
    public void AddPaginationTarget(string targetId, double radius, int dwellMs)
    {
        Register(targetId, DwellCategory.Pagination, radius, dwellMs, () => { });
    }

    /// <summary>
    /// Updates the screen position of a pagination button for dwell detection.
    /// </summary>
    public void UpdatePaginationButtonPosition(string targetId, PhysicalPoint center)
    {
        UpdatePosition(targetId, DwellCategory.Pagination, center);
    }

    /// <summary>
    /// Registers an action to be invoked when a control is activated via dwell.
    /// The action is stored separately from the target so that AddControlTarget
    /// can be called independently (legacy API pattern).
    /// </summary>
    public void RegisterControlAction(string controlId, Action action)
    {
        _controlActions[controlId] = action;
    }

    /// <summary>
    /// Adds a control button as a dwell target (action must be registered separately
    /// via RegisterControlAction for legacy callers).
    /// </summary>
    public void AddControlTarget(string controlId, double radius, int dwellMs)
    {
        _controlDwell.AddTarget(new DwellTarget(controlId, new PhysicalPoint(0, 0), radius, dwellMs));
    }

    /// <summary>
    /// Updates the screen position of a control for dwell detection.
    /// </summary>
    public void UpdateControlPosition(string controlId, PhysicalPoint center)
    {
        UpdatePosition(controlId, DwellCategory.Control, center);
    }

    /// <summary>For testing: simulate a control activation without gaze dwell.</summary>
    public void SimulateControlActivated(string controlId)
    {
        SimulateActivated(controlId, DwellCategory.Control);
    }

    /// <summary>
    /// Clears all control targets when rebuilding the settings page.
    /// </summary>
    public void ClearControlTargets()
    {
        ClearCategory(DwellCategory.Control);
    }

    /// <summary>
    /// Adds a modal button as a dwell target.
    /// </summary>
    public void AddModalTarget(string buttonId, double radius, int dwellMs)
    {
        _modalDwell.AddTarget(new DwellTarget(buttonId, new PhysicalPoint(0, 0), radius, dwellMs));
    }

    /// <summary>
    /// Updates the screen position of a modal button for dwell detection.
    /// </summary>
    public void UpdateModalButtonPosition(string buttonId, PhysicalPoint center)
    {
        UpdatePosition(buttonId, DwellCategory.Modal, center);
    }

    /// <summary>For testing: simulate a modal button activation without gaze dwell.</summary>
    public void SimulateModalButtonActivated(string buttonId)
    {
        SimulateActivated(buttonId, DwellCategory.Modal);
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
