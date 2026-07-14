namespace EyeCtrl.ViewModels;

using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// ViewModel for the settings overlay window. Manages all gaze/dwell interactions
/// by routing <see cref="GazeCursorMovedMessage"/> events through four independent
/// <see cref="DwellDetector"/> instances — one per <see cref="DwellCategory"/>.
/// </summary>
/// <remarks>
/// Implements <see cref="IDwellRegistrar"/> so that <see cref="Controls.DwellControl"/>
/// sub-classes and the view's legacy button helpers can register targets through a
/// unified API without taking a dependency on this concrete type.
/// </remarks>
public partial class SettingsWindowViewModel : MessengerViewModelBase,
    IRecipient<GazeCursorMovedMessage>,
    IDwellRegistrar
{
    private readonly SettingsViewModel _settingsViewModel;

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

    /// <summary>
    /// Editor ViewModel for the Quick Actions tab (drives the deferred action-card UI).
    /// </summary>
    public SettingsQuickActionsEditorViewModel QuickActionsEditor { get; }

    /// <summary>
    /// Initialises the ViewModel and wires up the four dwell detectors.
    /// </summary>
    /// <param name="settingsViewModel">The underlying settings data ViewModel.</param>
    /// <param name="quickActionsEditor">Editor ViewModel for the Quick Actions tab.</param>
    /// <param name="messenger">Messenger used to send and receive application-wide messages.</param>
    /// <param name="dwellSettings">Dwell configuration (cooldown, dwell times, etc.).</param>
    public SettingsWindowViewModel(
        SettingsViewModel settingsViewModel,
        SettingsQuickActionsEditorViewModel quickActionsEditor,
        IMessenger messenger,
        DwellSettings dwellSettings)
        : base(messenger)
    {
        _settingsViewModel = settingsViewModel;
        QuickActionsEditor = quickActionsEditor;

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
    }

    /// <summary>
    /// Receives gaze cursor movement and updates all dwell detectors.
    /// </summary>
    public void Receive(GazeCursorMovedMessage message)
    {
        if (IsDisposed) return;

        if (IsModalVisible)
        {
            // While the modal is open, only modal targets should receive dwell updates.
            // This prevents tabs or other underlying UI from activating through the overlay.
            _modalDwell.Update(message.Position, message.TimestampMicroseconds);
        }
        else
        {
            _tabDwell.Update(message.Position, message.TimestampMicroseconds);
            _controlDwell.Update(message.Position, message.TimestampMicroseconds);
            _pagDwell.Update(message.Position, message.TimestampMicroseconds);
        }
    }

    #region IDwellRegistrar Implementation

    /// <inheritdoc/>
    /// <remarks>
    /// For <see cref="DwellCategory.Modal"/> the built-in Yes/No logic always runs first
    /// (invoking <see cref="ShowModal"/>'s <c>onYes</c> callback and calling
    /// <see cref="HideModal"/>); the registered <paramref name="onActivated"/> callback is
    /// invoked afterwards as an optional extension point.
    /// </remarks>
    public void Register(string id, DwellCategory category, double radiusPhysical, int dwellMs, Action onActivated)
    {
        GetActions(category)[id] = onActivated;
        GetDetector(category).AddTarget(new DwellTarget(id, new PhysicalPoint(0, 0), radiusPhysical, dwellMs));
    }

    /// <inheritdoc/>
    public void Unregister(string id, DwellCategory category)
    {
        GetActions(category).Remove(id);
        GetDetector(category).RemoveTarget(id);
        GetProgressDict(category).Remove(id);
        OnPropertyChanged(GetProgressPropertyName(category));
    }

    /// <inheritdoc/>
    public void UpdatePosition(string id, DwellCategory category, PhysicalPoint center)
    {
        GetDetector(category).UpdateTargetCenter(id, center);
    }

    /// <inheritdoc/>
    public double GetProgress(string id, DwellCategory category)
    {
        return GetDetector(category).GetProgress(id);
    }

    /// <summary>
    /// Clears all dwell targets, registered actions, and progress state for the given category.
    /// Called by the view when rebuilding a page to discard stale control registrations.
    /// </summary>
    public void ClearCategory(DwellCategory category)
    {
        GetDetector(category).ClearTargets();
        GetActions(category).Clear();
        GetProgressDict(category).Clear();
        OnPropertyChanged(GetProgressPropertyName(category));
    }

    /// <summary>
    /// Triggers the activation logic for a registered target without a real gaze dwell.
    /// Used by mouse-click fallbacks in the view and by unit tests.
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
                    Messenger.Send(new CloseSettingsMessage());
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
                // Cache the callback before HideModal() clears _modalActions.
                _modalActions.TryGetValue(id, out var modalAction);

                if (id == "modal_yes")
                {
                    _modalYesAction?.Invoke();
                    HideModal();
                }
                else if (id == "modal_no")
                {
                    HideModal();
                }

                modalAction?.Invoke();
                break;

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
    /// Shows a modal confirmation dialog with the specified message and Yes callback.
    /// Clears any previous modal state and always notifies the view to re-register modal
    /// dwell targets, even if the modal was already visible.
    /// </summary>
    /// <param name="message">Text displayed in the modal dialog body.</param>
    /// <param name="onYes">Invoked when the user confirms (dwells or clicks Yes).</param>
    public void ShowModal(string message, Action onYes)
    {
        // Clear any existing modal state
        _modalDwell.ClearTargets();
        _modalActions.Clear();
        ModalProgress.Clear();
        OnPropertyChanged(nameof(ModalProgress));

        // Reset non-modal detectors so stale EnteredTimestamp values accumulated before
        // the modal opened cannot fire a spurious activation when updates resume on close.
        _tabDwell.ResetAllState();
        _controlDwell.ResetAllState();
        _pagDwell.ResetAllState();

        ModalMessage = message;
        _modalYesAction = onYes;
        // Always fire PropertyChanged so the view always re-registers the modal targets.
        // SetProperty skips notification if IsModalVisible is already true, which would
        // leave _modalDwell without targets after ClearTargets() above.
        if (!IsModalVisible)
            IsModalVisible = true;
        else
            OnPropertyChanged(nameof(IsModalVisible));
    }

    /// <summary>
    /// Hides the modal dialog and clears all modal state.
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

    protected override void OnDispose()
    {
        _settingsViewModel.Dispose();
    }
}
