namespace EyeCtrl.ViewModels;

using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Localization;
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
    /// <summary>Buttons currently offered by the modal, in display order. Empty when hidden.</summary>
    [ObservableProperty] private IReadOnlyList<ModalChoice> _modalChoices = [];

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

        // Wire up unified event handlers. Each activation lambda dispatches first and publishes
        // DwellActivationMessage (the dwell-completion cue's trigger) ONLY when the dispatch
        // actually did something — a stale target (view-teardown leftover) must stay silent.
        _tabDwell.TargetActivated += id =>
        {
            if (HandleTargetActivated(id, DwellCategory.Tab))
                Messenger.Send(new DwellActivationMessage());
        };
        _tabDwell.TargetProgress += (id, p) => OnTargetProgress(id, p, DwellCategory.Tab);

        _controlDwell.TargetActivated += id =>
        {
            if (HandleTargetActivated(id, DwellCategory.Control))
                Messenger.Send(new DwellActivationMessage());
        };
        _controlDwell.TargetProgress += (id, p) => OnTargetProgress(id, p, DwellCategory.Control);

        _pagDwell.TargetActivated += id =>
        {
            if (HandleTargetActivated(id, DwellCategory.Pagination))
                Messenger.Send(new DwellActivationMessage());
        };
        _pagDwell.TargetProgress += (id, p) => OnTargetProgress(id, p, DwellCategory.Pagination);

        _modalDwell.TargetActivated += id =>
        {
            if (HandleTargetActivated(id, DwellCategory.Modal))
                Messenger.Send(new DwellActivationMessage());
        };
        _modalDwell.TargetProgress += (id, p) => OnTargetProgress(id, p, DwellCategory.Modal);
    }

    /// <summary>
    /// Receives gaze cursor movement and updates all dwell detectors.
    /// </summary>
    public void Receive(GazeCursorMovedMessage message)
    {
        if (IsDisposed) return;
        // Held (blink) samples never advance dwell on any surface: the position is frozen, so feeding
        // it would complete a tab, control or modal button the user merely blinked at.
        if (message.IsInGap) return;

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
    /// <remarks>
    /// Calls <see cref="HandleTargetActivated"/> directly, bypassing the detector-event
    /// lambdas, so it never publishes <see cref="Messages.DwellActivationMessage"/>:
    /// a mouse click is not a dwell completion and must not sound the cue.
    /// </remarks>
    public void SimulateActivated(string id, DwellCategory category)
    {
        HandleTargetActivated(id, category);
    }

    #endregion

    #region Unified Event Handlers

    /// <summary>
    /// Runs the activation logic for a dwell target and reports whether it actually did
    /// something. The subscription lambdas publish <see cref="Messages.DwellActivationMessage"/>
    /// only on a <c>true</c> return — a stale target id (an action the view's teardown already
    /// removed) is a documented no-op and must not announce itself.
    /// </summary>
    private bool HandleTargetActivated(string id, DwellCategory category)
    {
        switch (category)
        {
            case DwellCategory.Tab:
                if (id == "close")
                {
                    Messenger.Send(new CloseSettingsMessage());
                    return true;
                }
                _settingsViewModel.ShowTab(id);
                ActiveTabId = id;
                if (_tabActions.TryGetValue(id, out var tabAction))
                    tabAction.Invoke();
                return true;

            case DwellCategory.Pagination:
                if (id is "pag_prev" or "pag_next")
                {
                    // Resolve the registered action BEFORE the auto-step and invoke THAT after
                    // it. The step changes CurrentPage, whose PropertyChanged re-renders the
                    // pager synchronously on this same thread (everything here is UI thread)
                    // and REPLACES the registration with an action computed for the NEW page.
                    // Looking _pagActions up after the step would therefore run the new page's
                    // action: a step landing on a boundary page (1 -> 0 of 4) would re-register
                    // pag_prev as the jump to page 3 and run it. A jump's own resolution is
                    // stable: at a boundary the step no-ops (PreviousPage/NextPage clamp), so
                    // no page change fires and no re-render replaces the action mid-activation.
                    _pagActions.TryGetValue(id, out var pagAction);
                    if (id == "pag_prev")
                        _settingsViewModel.PreviousPage();
                    else if (id == "pag_next")
                        _settingsViewModel.NextPage();
                    pagAction?.Invoke();
                    return true;
                }
                if (_pagActions.TryGetValue(id, out var extra))
                {
                    extra.Invoke();
                    return true;
                }
                return false;

            case DwellCategory.Modal:
                // Cache both callbacks before HideModal() clears the modal state.
                _modalActions.TryGetValue(id, out var modalAction);
                var choice = ModalChoices.FirstOrDefault(c => c.Id == id);

                // An id that is not one of the current choices is stale — a target left
                // over from a modal that has already closed. Ignore it rather than
                // closing a modal the user is still reading.
                if (choice is not null)
                {
                    var chosen = choice.OnChosen;
                    // HideModal() runs BEFORE the chosen action — the reverse of the old
                    // two-button form, which invoked onYes first. Load-bearing for the
                    // supervisor-restart choice (Task 13): its action shuts the app down, and
                    // leaving modal state live across shutdown would leave dwell targets
                    // pointing at a disposing window. Note this means a future OnChosen that
                    // reads modal state will see it cleared — by design.
                    HideModal();
                    chosen?.Invoke();
                }

                modalAction?.Invoke();
                return choice is not null || modalAction is not null;

            case DwellCategory.Control:
                if (_controlActions.TryGetValue(id, out var controlAction))
                {
                    controlAction.Invoke();
                    return true;
                }
                return false;

            default:
                throw new ArgumentOutOfRangeException(nameof(category), category, null);
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
    /// Shows a two-button Yes/No confirmation. Unchanged in behaviour and appearance —
    /// every reset confirmation in the settings window uses this form.
    /// </summary>
    /// <param name="message">Text displayed in the modal dialog body.</param>
    /// <param name="onYes">Invoked when the user confirms (dwells or clicks Yes).</param>
    public void ShowModal(string message, Action onYes) =>
        ShowModal(message,
        [
            new ModalChoice("modal_yes", StringTable.Current.T("settings.modal.yes"), ModalChoiceKind.Affirmative, onYes),
            new ModalChoice("modal_no", StringTable.Current.T("settings.modal.no"), ModalChoiceKind.Negative, null),
        ]);

    /// <summary>
    /// Shows a modal offering <paramref name="choices"/>. Clears any previous modal state
    /// and always notifies the view to re-register modal dwell targets, even if the modal
    /// was already visible.
    /// </summary>
    /// <param name="message">Text displayed in the modal dialog body.</param>
    /// <param name="choices">Buttons to offer, in display order. Must not be empty.</param>
    public void ShowModal(string message, IReadOnlyList<ModalChoice> choices)
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
        ModalChoices = choices;
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
        ModalChoices = [];
        _modalDwell.ClearTargets();
        _modalActions.Clear();
    }

    #endregion
}
