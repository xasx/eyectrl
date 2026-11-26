namespace EyeCtrl.App.Overlays;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Views;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Serilog;

/// <summary>
/// The fullscreen Backstage menu (Ticket 03): an edge-to-edge SurfaceDark overlay that renders the
/// session cards, the round neutral <see cref="DwellArcButton"/> close disc, the read-only status
/// strip and the inline confirmation modal.
///
/// <para>
/// <b>One dwell surface.</b> Every card, the Close disc and the modal choices are registered with
/// the single <see cref="BackstageViewModel"/> detector; the View only reports where each drawn
/// face ended up. The modal registers its buttons' REAL on-screen rectangles, so the drawn button
/// and the hit target are the same geometry (B8) — a confirmation button has no painted corner
/// that cannot be hit.
/// </para>
///
/// <para>
/// <b>It owns no policy.</b> Quit, Restart, Calibrate and the restart notices arrive through the
/// injected delegates and the messenger; the window only presents them. The
/// <see cref="EyeCtrl.App.Services.Coordinators.BackstageUiCoordinator"/> owns the screen claims
/// and the deterministic teardown.
/// </para>
///
/// <para>
/// <b>Testing.</b> Every test that constructs this window calls
/// <c>InvisibleTestWindow.Prepare(window)</c> before <see cref="ShowBackstage"/>, so
/// <c>NoVisibleTestWindowGuardTests</c> stays green and the owner's gaze is never stolen by a
/// test window.
/// </para>
/// </summary>
public partial class BackstageWindow : OverlayWindow
{
    /// <summary>
    /// The close disc's diameter: 2 × the shared hit radius, declared in XAML as 100. B8: the disc
    /// is drawn to exactly the size the dwell detector hit-tests
    /// (<see cref="UIConstants.ButtonRadius"/> = 50 DIP), so a gaze on the visible circle always
    /// lands in its hit area.
    /// </summary>
    internal const double CloseButtonSize = UIConstants.ButtonRadius * 2;

    /// <summary>
    /// The card geometry the <c>backstageCard</c> fit class measures: the 1280-DIP reference
    /// layout's three equal columns with 24 DIP gutters and 52 DIP outer margins. The live window is
    /// fullscreen, so on the owner's larger display the cards are wider than this floor — never
    /// narrower than the measured box at the minimum supported layout.
    /// </summary>
    internal const double ReferenceScreenWidthDip = 1280;
    internal const double OuterMarginDip = 52;
    internal const double CardGutterDip = 24;
    internal const double CardWidthDip =
        (ReferenceScreenWidthDip - (2 * OuterMarginDip) - (2 * CardGutterDip)) / 3;
    internal const double CardMinHeightDip = 164;

    /// <summary>The one-third cell a status pill must fit at the same reference layout. The header
    /// gives the title group the content width less the close host and its 24 DIP gap.</summary>
    internal const double StatusCellWidthDip =
        (ReferenceScreenWidthDip - (2 * OuterMarginDip) - CloseButtonSize - CardGutterDip) / 3;

    internal const double CardIconSizeDip = 28;
    internal const double CardProgressBarHeightDip = 6;

    /// <summary>Modal choice ids, stable so a test and the View agree without reading text.</summary>
    internal const string QuitConfirmChoiceId = "backstage_quit_yes";
    internal const string CancelChoiceId = "backstage_cancel";
    internal const string RestartChoiceId = "backstage_restart_yes";
    internal const string RestartSupervisorChoiceId = "backstage_restart_supervisor";

    /// <summary>The VM's modal ids, exposed for the refusal-notice pin.</summary>
    internal const string QuitModalId = "backstage_quit_confirm";
    internal const string RestartModalId = "backstage_restart_confirm";
    internal const string RestartInProgressModalId = "backstage_restart_in_progress";
    internal const string RestartRefusedModalId = "backstage_restart_refused";

    private enum BackstageModal
    {
        None,
        QuitConfirm,
        RestartConfirm,
        RestartInProgress,
        RestartRefused,
    }

    private IMessenger? _messenger;
    private BackstageViewModel? _vm;
    private Func<BackstageStatusView>? _status;
    private Func<bool>? _shouldOfferSupervisor;
    private Action? _shutdown;
    private Action<bool>? _restart;
    private Func<UiLanguage>? _targetLanguage;

    private DwellArcButton? _closeDisc;
    private Border? _pausedBadge;
    private readonly Dictionary<string, ActionCardFace> _cards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModalChoiceFace> _modalChoices = new(StringComparer.Ordinal);
    private readonly HashSet<string> _registeredChoiceIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _modalButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SolidColorBrush> _modalBrushes = new(StringComparer.Ordinal);
    private BackstageModal _modal = BackstageModal.None;
    private bool _noticesRegistered;
    private bool _configured;
    private Func<DateTime> _clockNow = static () => DateTime.Now;

    /// <summary>The coordinator's dismissal path: the Close disc, Esc, or a card that hands the
    /// screen back. The window never closes itself.</summary>
    internal event Action? DismissRequested;

    /// <summary>Calibrate asks the coordinator to close Backstage synchronously, then send the
    /// calibration request — the ordering that keeps teardown clear of the calibration window.</summary>
    internal event Action? CalibrateRequested;

    public BackstageWindow()
    {
        InitializeComponent();

        CreateCloseDisc();

        // The clock's time is deliberately larger than the title, but still from the token ramp:
        // RawGeometryLiteralTests rejects a raw FontSize literal, and a named const would not be
        // token-backed either. Display + Subtitle is 52 DIP at the shipped theme (the mockup's 54).
        ClockTimeText.FontSize = Tokens.FontSizeDisplay + Tokens.FontSizeSubtitle;

        // The window covers the primary screen. Width/Height are DIP, like every WPF measure; the
        // dwell targets are computed from the realised faces through PointToScreen, never from
        // these numbers.
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        Left = 0;
        Top = 0;

        CloseHost.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _vm?.ActivateForMouse(BackstageViewModel.CloseTargetId);
        };
        DpiChanged += (_, _) => ApplyDpiChangeAfterLayout();
    }

    /// <summary>The disc this window draws for its close target (the harness reads the anatomy off
    /// the object the window painted, never off a restatement).</summary>
    internal DwellArcButton? CloseDisc => _closeDisc;

    /// <summary>True while a confirmation/notice owns the modal overlay.</summary>
    internal bool IsModalOpen => _vm?.IsModalOpen ?? false;

    /// <summary>The face a test/measurer reads for one card.</summary>
    internal ActionCardFace? CardFace(string cardId) => _cards.GetValueOrDefault(cardId);

    /// <summary>The choice ids currently drawn in the modal, in order (test seam).</summary>
    internal IReadOnlyList<string> ModalChoiceIds => new List<string>(_modalChoices.Keys);

    /// <summary>The one label the refusal pin reads, so the test can fail on the text the user
    /// actually sees rather than on an id alone.</summary>
    internal string? ModalBodyText => ModalBody.Text;

    /// <summary>Test seam for the clock's local time source. Production is <see cref="DateTime.Now"/>.</summary>
    internal Func<DateTime> ClockNow
    {
        get => _clockNow;
        set => _clockNow = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>How many rendered clock strings actually changed (both fields counted). A poll
    /// that re-renders identical text is exactly what the change-only rule forbids, so the test
    /// asserts this stays put across a same-minute tick.</summary>
    internal int ClockTextChangesForTest { get; private set; }

    /// <summary>Test read of the rendered time/date, without reaching through FindName.</summary>
    internal string ClockTimeForTest => ClockTimeText.Text;
    internal string ClockDateForTest => ClockDateText.Text;

    /// <summary>
    /// Wires the window to its view model and the coordinator's actions. A fresh window is built
    /// per open, so this runs once per session; the VM is the app-lifetime singleton.
    /// </summary>
    internal void Configure(
        IMessenger messenger,
        BackstageViewModel vm,
        Func<BackstageStatusView> status,
        Func<bool> shouldOfferSupervisor,
        Action shutdown,
        Action<bool> restart,
        Func<UiLanguage> targetLanguage)
    {
        _messenger = messenger ?? throw new ArgumentNullException(nameof(messenger));
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _status = status ?? throw new ArgumentNullException(nameof(status));
        _shouldOfferSupervisor = shouldOfferSupervisor ?? throw new ArgumentNullException(nameof(shouldOfferSupervisor));
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
        _restart = restart ?? throw new ArgumentNullException(nameof(restart));
        _targetLanguage = targetLanguage ?? throw new ArgumentNullException(nameof(targetLanguage));

        DataContext = vm;
        TitleText.Text = StringTable.Current.T("overlays.toolbar.backstage");
        SessionLabel.Text = StringTable.Current.T("overlays.backstage.session");
        _pausedBadge = BuildStatusBadge(StringTable.Current.T("overlays.backstage.status.paused"), accent: true);
        PausedBadgeHost.Content = _pausedBadge;

        vm.PropertyChanged += OnVmPropertyChanged;
        vm.CardActivated += OnCardActivated;
        vm.ModalChoiceActivated += OnModalChoiceActivated;
        vm.CardProgress += OnCardProgress;
        vm.ModalProgress += OnModalProgress;
        vm.CloseRequested += OnCloseRequested;

        BuildCards();
        RefreshStatus();
        ApplyCloseDiscState();
        ApplyPausedBadge();
        _configured = true;
    }

    /// <summary>
    /// Shows the menu and registers the restart notices for this session. The coordinator has
    /// already acquired the screen claims; this method only handles the window and the messenger
    /// lifetime. A Loaded-priority pass pushes the drawn faces into the detector once layout has
    /// run (before the first visible frame is composed).
    /// </summary>
    internal void ShowBackstage()
    {
        if (!_configured)
            throw new InvalidOperationException("BackstageWindow.ShowBackstage requires Configure first.");

        RefreshStatus();
        _vm!.ShowOverlay();
        RegisterNotices();
        Show();
        // After Show(), because RefreshClock deliberately no-ops while the window is hidden:
        // the first rendered frame still carries the time.
        RefreshClock();
        Dispatcher.BeginInvoke(new Action(UpdatePositions), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Unregisters the notices, hides the menu and closes this window. Detaches the VM first so a
    /// close driven by the coordinator cannot re-enter through a VM event.
    /// </summary>
    internal void HideBackstage()
    {
        UnregisterNotices();
        _vm?.HideOverlay();
        Hide();
        Close();
    }

    /// <summary>
    /// Rebuilds the three status badges and the paused pill from the App-owned facts. Called on
    /// open and on every <see cref="EyeCtrl.App.Services.SupervisionToggleService.StateChanged"/>
    /// (change-only), never per gaze sample.
    /// </summary>
    internal void RefreshStatus()
    {
        if (_vm is null || _status is null)
            return;

        var view = _status();
        _vm.SetStatus(view.Snapshot.VersionText, view.Snapshot.InstallText, view.Snapshot.SupervisorText);

        VersionBadgeHost.Content = BuildStatusBadge(view.Snapshot.VersionText);
        InstallBadgeHost.Content = BuildStatusBadge(view.Snapshot.InstallText);
        SupervisorBadgeHost.Content = view.ShowSupervisor
            ? BuildStatusBadge(view.Snapshot.SupervisorText)
            : null;
        SupervisorBadgeHost.Visibility = view.ShowSupervisor ? Visibility.Visible : Visibility.Collapsed;

        ApplyPausedBadge();
    }

    /// <summary>
    /// Re-renders the read-only clock from the local time, in the UI-LANGUAGE culture (never
    /// <c>CurrentCulture</c>). Called on open and from the coordinator's existing 2 s supervision
    /// poll — there is deliberately NO timer here. It touches the TextBlocks only when the
    /// rendered string actually changes, so a same-minute poll cannot churn the header in a gaze
    /// user's peripheral vision, and it is inert once the menu is hidden (the close path is the
    /// window's own IsVisible false, before the coordinator drops the window).
    /// </summary>
    internal void RefreshClock()
    {
        if (!IsVisible)
            return;

        var text = BackstageClock.Format(
            ClockNow(), StringTable.Current.FormatProviderFor("overlays"));

        if (!string.Equals(ClockTimeText.Text, text.Time, StringComparison.Ordinal))
        {
            ClockTimeText.Text = text.Time;
            ClockTextChangesForTest++;
        }

        if (!string.Equals(ClockDateText.Text, text.Date, StringComparison.Ordinal))
        {
            ClockDateText.Text = text.Date;
            ClockTextChangesForTest++;
        }
    }

    /// <summary>
    /// Computes each drawn card's real on-screen rectangle and the close disc's centre, and pushes
    /// them into the VM. Called after layout, on show, on a modal change and after a DPI change.
    /// </summary>
    internal void UpdatePositions()
    {
        if (_vm is null || !_vm.IsVisible)
            return;

        foreach (var (cardId, face) in _cards)
        {
            var topLeft = face.Root.PointToScreen(new Point(0, 0));
            var bottomRight = face.Root.PointToScreen(
                new Point(face.Root.ActualWidth, face.Root.ActualHeight));
            _vm.UpdateCardPosition(cardId, new PhysicalRect(
                topLeft.X, topLeft.Y,
                bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y));
        }

        var closeCentre = CloseHost.PointToScreen(
            new Point(CloseHost.ActualWidth / 2, CloseHost.ActualHeight / 2));
        _vm.UpdateCloseButtonPosition(new PhysicalPoint(closeCentre.X, closeCentre.Y));

        if (_vm.IsModalOpen)
            UpdateModalChoicePositions();
    }

    /// <summary>
    /// Re-runs the real layout pass after a DPI change. The ORDER is load-bearing: position pushes
    /// first (they store the new card rectangles and the new close-disc radius), then
    /// <see cref="BackstageViewModel.RestoreMainSurface"/> rebuilds the live targets from those
    /// registrations. The old order — push positions only — left the circular close target at the
    /// previous DPI's radius, because an existing circular target is only centre-updated.
    /// </summary>
    internal void ApplyDpiChangeAfterLayout()
    {
        if (_vm is null || !_vm.IsVisible)
            return;

        UpdatePositions();
        _vm.RestoreMainSurface();
        if (_vm.IsModalOpen)
            UpdateModalChoicePositions();
    }

    /// <summary>Esc: dismiss an open modal first, otherwise the coordinator closes the menu.</summary>
    internal void HandleEscape()
    {
        if (_vm is null)
            return;

        if (_vm.IsModalOpen)
            DismissModal();
        else
            DismissRequested?.Invoke();
    }

    /// <summary>Dismisses the modal and restores the main surface (the coordinator's Esc/Tab path
    /// and the notice buttons call this).</summary>
    internal void DismissModal()
    {
        _vm?.DismissModal();
        CloseModalVisuals();
    }

    // ── Close disc ────────────────────────────────────────────────────────────────────────────

    private void CreateCloseDisc()
    {
        CloseLayer.Children.Clear();
        var centre = new Point(UIConstants.ButtonRadius, UIConstants.ButtonRadius);

        // Resolve the shared palette through the WINDOW's resource lookup, not Tokens directly.
        // WPF materialises each {x:Static} entry in OverlayStyles.xaml on that key's first lookup,
        // under whichever theme is active then, and keeps it for the process; a Tokens comparison
        // in a test can then disagree with the rendered brush depending on test order (the #392
        // flake lesson). The fallback keeps a token dictionary-less construction working.
        Brush Palette(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

        _closeDisc = DwellArcButton.Create(
            centre,
            UIConstants.ButtonRadius,
            symbol: null,
            fill: Palette("BrushSurfaceMedium", Tokens.BrushSurfaceMedium),
            stroke: Palette("BrushBorderSubtle", Tokens.BrushBorderSubtle),
            arcBrush: Palette("BrushAccent", Tokens.BrushAccent),
            symbolBrush: Palette("BrushTextPrimary", Tokens.BrushTextPrimary),
            icon: ActionIcons.For(ActionIcon.Close),
            iconSize: DwellArcButton.IconSize.Solo,
            accessibleName: StringTable.Current.T("overlays.edge.close"));
        _closeDisc.AddTo(CloseLayer);
        ApplyCloseDiscState();
    }

    private void ApplyCloseDiscState()
    {
        _closeDisc?.UpdateProgress(_vm?.CloseDwellProgress ?? 0);
        _closeDisc?.SetPaused(_vm?.CloseDwellPaused ?? false);
    }

    private void ApplyPausedBadge()
    {
        if (PausedBadgeHost.Content is null && _pausedBadge is not null)
            PausedBadgeHost.Content = _pausedBadge;
        PausedBadgeHost.Visibility = _vm?.PausedIndicatorVisible == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ── Cards ─────────────────────────────────────────────────────────────────────────────────

    private void BuildCards()
    {
        SessionCards.Children.Clear();
        _cards.Clear();

        AddCard(BackstageViewModel.QuitCardId, 0,
            "overlays.backstage.quit.title", "overlays.backstage.quit.description",
            BackstageIcons.Quit, danger: true);
        AddCard(BackstageViewModel.RestartCardId, 2,
            "overlays.backstage.restart.title", "overlays.backstage.restart.description",
            BackstageIcons.Restart, danger: false);
        AddCard(BackstageViewModel.CalibrateCardId, 4,
            "overlays.backstage.calibrate.title", "overlays.backstage.calibrate.description",
            BackstageIcons.Calibrate, danger: false);
    }

    private void AddCard(string cardId, int column, string titleKey, string descriptionKey,
        IconGeometry icon, bool danger)
    {
        var face = BuildActionCard(
            StringTable.Current.T(titleKey),
            StringTable.Current.T(descriptionKey),
            icon, danger);
        face.Root.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _vm?.ActivateForMouse(cardId);
        };
        AutomationProperties.SetAutomationId(face.Root, "Backstage_Card_" + cardId);
        Grid.SetColumn(face.Root, column);
        SessionCards.Children.Add(face.Root);
        _cards[cardId] = face;
    }

    // ── Modal ─────────────────────────────────────────────────────────────────────────────────

    private void OnCardActivated(string cardId)
    {
        switch (cardId)
        {
            case BackstageViewModel.QuitCardId:
                OpenQuitConfirmation();
                break;
            case BackstageViewModel.RestartCardId:
                OpenRestartConfirmation();
                break;
            case BackstageViewModel.CalibrateCardId:
                // The coordinator closes Backstage FIRST, then sends the calibration request; a
                // teardown racing CalibrationWindow's own fixation claim is the failure this
                // ordering exists to prevent.
                CalibrateRequested?.Invoke();
                break;
        }
    }

    private void OpenQuitConfirmation()
    {
        var choices = new[]
        {
            new ModalChoice(QuitConfirmChoiceId,
                StringTable.Current.T("overlays.backstage.quit.confirm.action"),
                ModalChoiceKind.Negative, null),
            new ModalChoice(CancelChoiceId,
                StringTable.Current.T("overlays.backstage.modal.cancel"),
                ModalChoiceKind.Neutral, null),
        };
        OpenModal(BackstageModal.QuitConfirm, QuitModalId,
            StringTable.Current.T("overlays.backstage.quit.confirm.title"),
            StringTable.Current.T("overlays.backstage.quit.confirm.body"),
            choices);
    }

    private void OpenRestartConfirmation()
    {
        var offerSupervisor = _shouldOfferSupervisor?.Invoke() ?? false;
        var title = StringTable.Current.T("overlays.backstage.restart.confirm.title");
        var bodyKey = offerSupervisor
            ? "overlays.backstage.restart.confirm.supervisedBody"
            : "overlays.backstage.restart.confirm.body";
        var choices = new List<ModalChoice>();
        if (offerSupervisor)
        {
            choices.Add(new ModalChoice(RestartSupervisorChoiceId,
                StringTable.Current.T("overlays.backstage.restart.confirm.supervisor"),
                ModalChoiceKind.Affirmative, null));
            // In the 3-choice shape a bare "Restart" beside "Restart with Supervisor" does not say
            // what the plain option does; the coordinator's ruling names it explicitly.
            choices.Add(new ModalChoice(RestartChoiceId,
                StringTable.Current.T("overlays.backstage.restart.confirm.withoutSupervisor"),
                ModalChoiceKind.Affirmative, null));
        }
        else
        {
            choices.Add(new ModalChoice(RestartChoiceId,
                StringTable.Current.T("overlays.backstage.restart.confirm.action"),
                ModalChoiceKind.Affirmative, null));
        }
        choices.Add(new ModalChoice(CancelChoiceId,
            StringTable.Current.T("overlays.backstage.modal.cancel"),
            ModalChoiceKind.Neutral, null));

        OpenModal(BackstageModal.RestartConfirm, RestartModalId,
            title, StringTable.Current.T(bodyKey), choices);
    }

    private void OpenModal(BackstageModal modal, string modalId, string title, string body,
        IReadOnlyList<ModalChoice> choices)
    {
        if (_vm is null)
            return;

        _modal = modal;
        ModalTitle.Text = title;
        ModalTitle.Visibility = string.IsNullOrEmpty(title) ? Visibility.Collapsed : Visibility.Visible;
        ModalBody.Text = body;

        ModalChoices.Children.Clear();
        _modalChoices.Clear();
        _registeredChoiceIds.Clear();
        _modalButtons.Clear();
        _modalBrushes.Clear();

        foreach (var choice in choices)
        {
            var (background, foreground) = ModalButtonPalette.For(choice.Kind);
            // The production Settings modal face: the same builder the modal170/modal460 fit
            // classes measure. Backstage uses the 460-wide stacked form for every choice, so the
            // explicit verb-phrase labels fit at the builder's 24 DIP type.
            var button = SettingsWindow.BuildModalButton(
                choice.Id, choice.Label, background, foreground, wide: true,
                () => _vm?.ActivateForMouse(choice.Id), _modalButtons, _modalBrushes);

            var track = new Grid
            {
                Height = CardProgressBarHeightDip,
                Margin = new Thickness(12, 0, 12, 10),
                Background = Tokens.BrushSurfaceDarkOpaque,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var fill = new Border
            {
                Height = CardProgressBarHeightDip,
                Width = 0,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Tokens.BrushAccent,
                CornerRadius = Tokens.SmallCornerRadius,
            };
            track.Children.Add(fill);

            var face = new Grid();
            face.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            face.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = (TextBlock)button.Child;
            // The label is still the Border's logical child; detach it before reparenting it into
            // the progress-bar grid, or WPF refuses the second parent.
            button.Child = null;
            Grid.SetRow(label, 0);
            face.Children.Add(label);
            Grid.SetRow(track, 1);
            face.Children.Add(track);
            button.Child = face;

            AutomationProperties.SetAutomationId(button, "Backstage_Choice_" + choice.Id);
            ModalChoices.Children.Add(button);
            _modalChoices[choice.Id] = new ModalChoiceFace(button, fill);
        }

        ModalOverlay.Visibility = Visibility.Visible;
        _vm.ShowModal(modalId);

        // Register the REAL rectangles after the modal is laid out. ShowModal has already cleared
        // every background target; each choice is registered held and only positioned once its
        // drawn rectangle is known, so a gaze resting under the modal cannot answer it.
        UpdateLayout();
        UpdateModalChoicePositions();
    }

    private void UpdateModalChoicePositions()
    {
        if (_vm is null)
            return;

        foreach (var (choiceId, face) in _modalChoices)
        {
            if (face.Root.ActualWidth <= 0 || face.Root.ActualHeight <= 0)
                continue;

            var topLeft = face.Root.PointToScreen(new Point(0, 0));
            var bottomRight = face.Root.PointToScreen(
                new Point(face.Root.ActualWidth, face.Root.ActualHeight));

            // ActualWidth/Height are DIP; RegisterModalChoice converts the half-extents to
            // physical pixels itself and PointToScreen supplies the physical rectangle. That is
            // the whole B8 contract: drawn button == hit rectangle at every DPI.
            if (_registeredChoiceIds.Add(choiceId))
            {
                _vm.RegisterModalChoice(choiceId,
                    face.Root.ActualWidth / 2, face.Root.ActualHeight / 2);
            }

            _vm.UpdateModalChoiceRect(choiceId, new PhysicalRect(
                topLeft.X, topLeft.Y,
                bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y));
        }
    }

    private void CloseModalVisuals()
    {
        ModalOverlay.Visibility = Visibility.Collapsed;
        ModalChoices.Children.Clear();
        _modalChoices.Clear();
        _registeredChoiceIds.Clear();
        _modalButtons.Clear();
        _modalBrushes.Clear();
        _modal = BackstageModal.None;
    }

    private void OnModalChoiceActivated(string choiceId)
    {
        switch (_modal)
        {
            case BackstageModal.QuitConfirm:
                if (choiceId == QuitConfirmChoiceId)
                    _shutdown?.Invoke();
                else
                    DismissModal();
                break;

            case BackstageModal.RestartConfirm:
                if (choiceId == RestartSupervisorChoiceId)
                    _restart?.Invoke(true);
                else if (choiceId == RestartChoiceId)
                    _restart?.Invoke(false);
                else
                    DismissModal();
                break;

            default:
                // In-progress and refusal notices have one acknowledgement; any choice dismisses.
                DismissModal();
                break;
        }
    }

    // ── Restart notices ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers this window for the restart notices while it owns the screen. The App-level
    /// <c>RestartNoticeHost</c> suppresses its floating card while Backstage is open and does NOT
    /// replay it on close, so this subscription is the ONLY thing that makes a refusal raised by a
    /// Backstage card visible. The pin test sends the message and asserts the modal appears; it
    /// goes red if this registration is missing or was removed early.
    /// </summary>
    private void RegisterNotices()
    {
        if (_noticesRegistered || _messenger is null)
            return;

        _messenger.Register<RestartInProgressMessage>(this, (_, _) =>
            Dispatcher.Invoke(ShowRestartInProgress));
        _messenger.Register<RestartUnavailableMessage>(this, (_, message) =>
            Dispatcher.Invoke(() => ShowRestartRefused(message.Reason)));
        _noticesRegistered = true;
    }

    private void UnregisterNotices()
    {
        if (!_noticesRegistered || _messenger is null)
            return;

        _messenger.Unregister<RestartInProgressMessage>(this);
        _messenger.Unregister<RestartUnavailableMessage>(this);
        _noticesRegistered = false;
    }

    private void ShowRestartInProgress()
    {
        if (_vm is null || !_vm.IsVisible)
            return;

        var target = _targetLanguage?.Invoke() ?? UiLanguage.System;
        OpenModal(BackstageModal.RestartInProgress, RestartInProgressModalId,
            title: string.Empty,
            body: RestartNotices.InProgressMessage(target),
            choices: RestartNotices.InProgressChoices(target));
    }

    private void ShowRestartRefused(string reason)
    {
        if (_vm is null || !_vm.IsVisible)
            return;

        var target = _targetLanguage?.Invoke() ?? UiLanguage.System;
        OpenModal(BackstageModal.RestartRefused, RestartRefusedModalId,
            title: string.Empty,
            body: reason,
            choices: RestartNotices.RefusedChoices(target));
    }

    // ── VM events ─────────────────────────────────────────────────────────────────────────────

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BackstageViewModel.CloseDwellProgress):
            case nameof(BackstageViewModel.CloseDwellPaused):
                ApplyCloseDiscState();
                break;
            case nameof(BackstageViewModel.PausedIndicatorVisible):
                ApplyPausedBadge();
                break;
        }
    }

    private void OnCardProgress(string cardId, double progress)
    {
        if (_cards.TryGetValue(cardId, out var face))
            face.Fill.Width = Math.Max(0, face.Track.ActualWidth * progress);
    }

    private void OnModalProgress(string choiceId, double progress)
    {
        if (_modalChoices.TryGetValue(choiceId, out var face))
            face.Fill.Width = Math.Max(0, face.Track.ActualWidth * progress);
    }

    private void OnCloseRequested() => DismissRequested?.Invoke();

    // ── Production control builders (the fit harness measures these) ──────────────────────────

    /// <summary>
    /// The production status pill — the exact control the strip shows. The
    /// <c>backstageBadge</c> measurer builds this and checks that the text plus the pill's
    /// padding stays inside one <see cref="StatusCellWidthDip"/> cell, on one line.
    /// </summary>
    internal static Border BuildStatusBadge(string text, bool accent = false)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = accent ? Tokens.BrushAccent : Tokens.BrushTextPrimary,
            FontSize = Tokens.FontSizeBody,
            FontWeight = Tokens.FontWeightStrong,
            TextWrapping = TextWrapping.NoWrap,
        };
        var badge = new Border
        {
            Background = accent ? Tokens.BrushSurfaceLight : Tokens.BrushSurfaceMedium,
            BorderBrush = accent ? Tokens.BrushAccent : Tokens.BrushBorderSubtle,
            BorderThickness = new Thickness(1),
            // A rounded tag, not a circle: CircleCornerRadius is reserved for square faces
            // (CornerRadiusClampTests), and this pill is wider than it is tall.
            CornerRadius = Tokens.LargeCornerRadius,
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = label,
        };
        AutomationProperties.SetName(badge, text);
        return badge;
    }

    /// <summary>
    /// The production action card — the exact face the session row shows. The
    /// <c>backstageCard</c> measurer builds this in a <see cref="CardWidthDip"/> ×
    /// <see cref="CardMinHeightDip"/> host and checks the title is one line and the description's
    /// widest run and wrapped height fit the remaining row.
    /// </summary>
    internal static ActionCardFace BuildActionCard(string title, string description,
        IconGeometry? icon, bool danger)
    {
        var titleBlock = new TextBlock
        {
            Text = title,
            Foreground = danger ? Tokens.BrushAccentDanger : Tokens.BrushTextPrimary,
            FontSize = Tokens.FontSizeTitle,
            FontWeight = Tokens.FontWeightStrong,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleBlock.Name = "BackstageCardTitle";

        var descriptionBlock = new TextBlock
        {
            Text = description,
            Foreground = Tokens.BrushTextSecondary,
            FontSize = Tokens.FontSizeBodyLarge,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 8),
        };
        descriptionBlock.Name = "BackstageCardDescription";

        var track = new Grid
        {
            Height = CardProgressBarHeightDip,
            Background = Tokens.BrushSurfaceDarkOpaque,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var fill = new Border
        {
            Height = CardProgressBarHeightDip,
            Width = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = danger ? Tokens.BrushAccentDanger : Tokens.BrushAccent,
            CornerRadius = Tokens.SmallCornerRadius,
        };
        track.Children.Add(fill);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon is not null)
        {
            titleRow.Children.Add(BuildIcon(icon, CardIconSizeDip,
                danger ? Tokens.BrushAccentDanger : Tokens.BrushTextPrimary));
            titleRow.Children.Add(new Border { Width = 12 });
        }

        titleRow.Children.Add(titleBlock);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleRow, 0);
        Grid.SetRow(descriptionBlock, 1);
        Grid.SetRow(track, 2);
        content.Children.Add(titleRow);
        content.Children.Add(descriptionBlock);
        content.Children.Add(track);

        var root = new Border
        {
            MinHeight = CardMinHeightDip,
            Padding = new Thickness(22, 20, 22, 20),
            CornerRadius = Tokens.LargeCornerRadius,
            Background = Tokens.BrushSurfaceMedium,
            BorderBrush = danger ? Tokens.BrushAccentDanger : Tokens.BrushBorderSubtle,
            BorderThickness = new Thickness(2),
            Cursor = Cursors.Hand,
            Child = content,
        };
        AutomationProperties.SetName(root, title);
        return new ActionCardFace(root, titleBlock, descriptionBlock, track, fill);
    }

    /// <summary>Draws an icon registry entry at a card's size on the shared 24-unit grid.</summary>
    private static FrameworkElement BuildIcon(IconGeometry geometry, double size, Brush brush)
    {
        var canvas = new Canvas { Width = ActionIcons.GridSize, Height = ActionIcons.GridSize };
        if (geometry.Stroke is { } stroke)
        {
            canvas.Children.Add(new Path
            {
                Data = stroke,
                Stroke = brush,
                StrokeThickness = ActionIcons.GridStrokeThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            });
        }

        if (geometry.Fill is { } fill)
        {
            canvas.Children.Add(new Path { Data = fill, Fill = brush });
        }

        return new Viewbox { Width = size, Height = size, Child = canvas, Stretch = Stretch.Uniform };
    }

    protected override void OnClosed(EventArgs e)
    {
        UnregisterNotices();
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.CardActivated -= OnCardActivated;
            _vm.ModalChoiceActivated -= OnModalChoiceActivated;
            _vm.CardProgress -= OnCardProgress;
            _vm.ModalProgress -= OnModalProgress;
            _vm.CloseRequested -= OnCloseRequested;
            _vm = null;
        }

        _messenger = null;
        _closeDisc = null;
        base.OnClosed(e);
    }

    /// <summary>One card face: the drawn root plus the elements the View updates.</summary>
    internal sealed record ActionCardFace(
        Border Root,
        TextBlock Title,
        TextBlock Description,
        FrameworkElement Track,
        Border Fill);

    private sealed record ModalChoiceFace(FrameworkElement Root, Border Fill)
    {
        public FrameworkElement Track => (FrameworkElement)Fill.Parent;
    }
}
