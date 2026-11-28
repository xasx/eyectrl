namespace EyeCtrl.ViewModels;

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The headless half of the fullscreen Backstage menu (Ticket 02): the session/install cards, the
/// round Close disc and the inline confirmation modal all share this one <see cref="DwellDetector"/>
/// (the base class's), and the View only reports where each drawn face ended up.
///
/// <para>
/// <b>Dwellable while paused.</b> <see cref="DwellActiveWhilePaused"/> is true because Backstage is
/// the owner's way OUT of a paused state: the owner controls this PC only by gaze, gets stuck in
/// pause more often than not, and cannot always reach the toolbar's resume affordance. Every card
/// and the Close disc therefore keep accumulating while <c>InteractionState.Paused</c>, and the
/// effects are deliberately NOT gated on <see cref="DwellOverlayViewModelBase.LastSampleWasPaused"/>:
/// Quit, Restart and Calibrate must work from inside a pause, or the escape is a dead end.
/// </para>
///
/// <para>
/// <b>Leave-before-arm.</b> Every target registration HOLDS the target through
/// <see cref="LeaveBeforeArmLatch"/> and only <see cref="UpdateCardPosition"/> /
/// <see cref="UpdateCloseButtonPosition"/> / <see cref="UpdateModalChoiceCentre"/> mark it
/// positioned once the View has pushed a real centre. A target registered under the resting gaze
/// (the toolbar Backstage disc, the card that just opened a modal, the modal button that just
/// dismissed it) therefore cannot fire until the gaze leaves its release area and comes back.
/// </para>
///
/// <para>
/// <b>One surface at a time.</b> <see cref="ShowModal"/> clears every main-surface target and only
/// the modal's own choices are registered; <see cref="DismissModal"/> clears those and re-registers
/// the cards + Close disc it remembered, each held again. A background card can never be dwelt
/// through a confirmation modal.
/// </para>
/// </summary>
public sealed partial class BackstageViewModel : DwellOverlayViewModelBase
{
    /// <summary>Reserved dwell-target id for the top-right Close disc (not a card).</summary>
    public const string CloseTargetId = "__backstage_close__";

    /// <summary>The session row's cards (Ticket 02 names the ids; the View draws and labels them).</summary>
    public const string QuitCardId = "backstage_quit";
    public const string RestartCardId = "backstage_restart";
    public const string CalibrateCardId = "backstage_calibrate";

    /// <summary>The install row's cards (only registered when the build is installed).</summary>
    public const string UpdateCardId = "backstage_update";
    public const string RollbackCardId = "backstage_rollback";
    public const string UninstallCardId = "backstage_uninstall";

    /// <summary>The modal's own choices do not use a reserved prefix: the View/coordinator names them
    /// (confirm, cancel, a restart choice …), and they live only while <see cref="IsModalOpen"/>.</summary>
    private const double ModalChoiceRadiusDip = 70;

    private readonly EyeCtrlSettings _settings;
    private readonly LeaveBeforeArmLatch _latch;
    private readonly double _releaseMarginPhysical;

    /// <summary>
    /// The ids of the modal choices currently registered. Kept so a modal that is replaced, dismissed
    /// or hidden mid-dwell can drop every choice's progress bar to zero: <c>ClearTargets()</c> emits
    /// no progress, so without this a part-filled modal bar would stay frozen beside a live
    /// confirmation button (the N7 follow-up from the Ticket 2 review).
    /// </summary>
    private readonly List<string> _modalTargetIds = new();

    /// <summary>
    /// The main surface the View last laid out, in registration order. Kept so a modal dismiss can
    /// re-register exactly the cards and Close disc that were on screen — as FRESH
    /// <see cref="DwellTarget"/> instances, never the pre-modal objects, whose accumulated dwell
    /// state would otherwise survive the modal.
    /// </summary>
    private readonly List<MainRegistration> _mainTargets = new();

    [ObservableProperty] private double _closeDwellProgress;

    /// <summary>True while the Close disc's dwell is held (gaze stepped off mid-dwell): its bar dims.</summary>
    [ObservableProperty] private bool _closeDwellPaused;

    /// <summary>True while an inline confirmation/notice modal owns the surface.</summary>
    [ObservableProperty] private bool _isModalOpen;

    /// <summary>The open modal's identity, for the View's content selector; null when none is open.</summary>
    [ObservableProperty] private string? _modalId;

    /// <summary>The status strip's three read-only texts, pushed by the App on open and on every
    /// supervisor presence refresh (the 2 s timer is the window's, Ticket 03).</summary>
    [ObservableProperty] private string _versionText = string.Empty;
    [ObservableProperty] private string _installText = string.Empty;
    [ObservableProperty] private string _supervisorText = string.Empty;

    /// <summary>Whether the strip shows its paused badge. Sample-driven, so it cannot lag the
    /// pause toggle by the supervisor refresh's 2 s.</summary>
    [ObservableProperty] private bool _pausedIndicatorVisible;

    /// <summary>A card fired. The id is one of the card constants above; the surface's targets are
    /// already cleared when this runs, and the coordinator either closes Backstage or opens a modal.</summary>
    public event Action<string>? CardActivated;

    /// <summary>The Close disc fired (a pure dismissal; the VM hides itself immediately after).</summary>
    public event Action? CloseRequested;

    /// <summary>
    /// The source of the last <see cref="CloseRequested"/>: <see cref="StrandingLedger.BackstageSources.Disc"/>
    /// for a gaze dwell on the round disc, <see cref="StrandingLedger.BackstageSources.Mouse"/> when
    /// the request came through <see cref="ActivateForMouse"/> (a mouse click, or a caregiver's
    /// click through TD Control). The coordinator logs it, so "how did the owner leave Backstage"
    /// is answerable.
    /// </summary>
    public string CloseRequestSource { get; private set; } = StrandingLedger.BackstageSources.Disc;

    /// <summary>True only for the synchronous <see cref="ActivateForMouse"/> call it wraps.</summary>
    private bool _activationWasMouse;

    /// <summary>A modal choice fired. The modal's choices stay cleared until the coordinator calls
    /// <see cref="DismissModal"/> or <see cref="ShowModal"/> again.</summary>
    public event Action<string>? ModalChoiceActivated;

    /// <summary>Card dwell progress, 0..1 (the cards' bottom-edge progress bars bind to this).</summary>
    public event Action<string, double>? CardProgress;

    /// <summary>Modal choice dwell progress, 0..1.</summary>
    public event Action<string, double>? ModalProgress;

    /// <summary>
    /// The one opt-in that matters for safety: Backstage stays dwellable while EyeCtrl is paused,
    /// because it is the owner's escape to Quit/Restart. See the class remarks.
    /// </summary>
    protected override bool DwellActiveWhilePaused => true;

    /// <summary>Test seam: the pause opt-in, so a test cannot pass for the wrong reason.</summary>
    internal bool DwellActiveWhilePausedForTest => DwellActiveWhilePaused;

    /// <summary>Test seam: the sample stamp the base's pause gates read. Ticket 03 adds no
    /// input-injecting card, but the stamp becomes load-bearing the moment one exists, and no
    /// assembly-wide pin reads Receive overrides — so this one is pinned now.</summary>
    internal bool LastSampleWasPausedForTest => LastSampleWasPaused;

    public BackstageViewModel(IMessenger messenger, EyeCtrlSettings settings,
        IStruggleTelemetrySink? sink = null, IDwellDetectorRegistry? dwellRegistry = null)
        : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.Other, sink, dwellRegistry,
               settings.Dwell)
    {
        _settings = settings;
        _releaseMarginPhysical = DpiHelper.LogicalToPhysicalX(SettingsWindowViewModel.LatchReleaseMarginDip);
        _latch = new LeaveBeforeArmLatch(DwellDetector, () => _releaseMarginPhysical);
    }

    /// <summary>
    /// A session card is Quit / Restart / Calibrate: more consequential than switching a window.
    /// It therefore follows the Task Switcher tile's precedent — <c>max(TaskSwitcherTileDwellMs,
    /// ToolbarButtonDwellMs)</c> — instead of inheriting the toolbar's 700 ms. Inheriting the
    /// toolbar value would also let an owner who shortens their toolbar dwell silently shorten
    /// Quit.
    /// </summary>
    private int CardDwellMs => Math.Max(_settings.Dwell.ToolbarButtonDwellMs, _settings.Dwell.TaskSwitcherTileDwellMs);

    /// <summary>
    /// The Close disc deliberately keeps the toolbar's quick dwell: it is the escape from the
    /// surface, and the Task Switcher's own close disc is left at the same value for the same
    /// reason (DwellSettings.cs's close-disc comment).
    /// </summary>
    private int CloseDwellMs => _settings.Dwell.ToolbarButtonDwellMs;

    /// <summary>A confirmation choice carries the same weight as the card that opened it.</summary>
    private int ModalChoiceDwellMs => Math.Max(_settings.Dwell.ToolbarButtonDwellMs, _settings.Dwell.TaskSwitcherTileDwellMs);

    /// <summary>
    /// Opens a fresh session: forgets the previous layout, clears every target and both latches,
    /// resets the round close disc and the pause badge, and flips <see cref="IsVisible"/> — the show
    /// trigger the coordinator watches. The status texts are NOT cleared here: the App has just
    /// built them for this open and pushes them alongside (see <see cref="SetStatus"/>).
    /// </summary>
    public void ShowOverlay()
    {
        ResetCardProgress();
        ResetModalProgress();
        _mainTargets.Clear();
        DwellDetector.ClearTargets();
        _latch.Clear();
        IsModalOpen = false;
        ModalId = null;
        CloseDwellProgress = 0;
        CloseDwellPaused = false;
        PausedIndicatorVisible = false;
        IsVisible = true;
    }

    /// <summary>
    /// Hides Backstage, clearing every target, the remembered layout and the close disc's progress.
    /// Public so the coordinator can dismiss it for a card, a Close dwell or an Esc.
    /// </summary>
    public void HideOverlay()
    {
        IsVisible = false;
        ResetCardProgress();
        ResetModalProgress();
        _mainTargets.Clear();
        DwellDetector.ClearTargets();
        _latch.Clear();
        IsModalOpen = false;
        ModalId = null;
        CloseDwellProgress = 0;
        CloseDwellPaused = false;
        PausedIndicatorVisible = false;
    }

    /// <summary>
    /// Pushes the status strip's localized texts (built by <c>BackstageStatusSnapshot</c> in the
    /// App). Called on open and on each supervisor presence refresh; it never touches dwell state.
    /// </summary>
    public void SetStatus(string versionText, string installText, string supervisorText)
    {
        VersionText = versionText;
        InstallText = installText;
        SupervisorText = supervisorText;
    }

    /// <summary>
    /// Registers a card's real drawn face after layout. The first report for an id creates the
    /// target at its true rectangle and HOLDS it (leave-before-arm); a later report (DPI change, a
    /// resize, the theme rebuilding the face) only moves the stored registration and re-bases the
    /// live target — it must not disarm a dwell in progress.
    /// <para>
    /// <paramref name="screenRect"/> is the drawn face in PHYSICAL pixels, exactly what the
    /// detector hit-tests (the View never restates DIP arithmetic here).
    /// </para>
    /// </summary>
    public void UpdateCardPosition(string cardId, PhysicalRect screenRect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardId);
        if (string.Equals(cardId, CloseTargetId, StringComparison.Ordinal))
            return;   // the Close disc has its own circular registration method

        var registration = new CardRegistration(cardId, screenRect);
        var existing = StoreRegistration(registration);
        if (!IsVisible || IsModalOpen)
            return;   // remembered for the next dismiss, never a live target through a modal

        if (existing && DwellDetector.Targets.Any(t => t.Id == cardId))
        {
            // Rebase keeps any accumulated dwell; a plain AddTarget would replace the object and
            // silently restart a look that was already halfway.
            DwellDetector.RebaseTarget(cardId, screenRect);
            return;
        }

        RegisterHeld(CreateTarget(registration));
    }

    /// <summary>
    /// Registers the round Close disc after layout, at the host's own centre in physical pixels —
    /// the drawn disc's centre, with the shared <see cref="UIConstants.ButtonRadius"/> hit radius
    /// (what is drawn IS what the detector hit-tests, the B8 invariant). Same first-vs-later rule as
    /// <see cref="UpdateCardPosition"/>.
    /// </summary>
    public void UpdateCloseButtonPosition(PhysicalPoint screenCentre)
    {
        var registration = new CloseRegistration(screenCentre,
            DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius));
        var existing = StoreRegistration(registration);
        if (!IsVisible || IsModalOpen)
            return;

        if (existing && DwellDetector.Targets.Any(t => t.Id == CloseTargetId))
        {
            DwellDetector.UpdateTargetCenter(CloseTargetId, screenCentre);
            return;
        }

        RegisterHeld(CreateTarget(registration));
    }

    /// <summary>
    /// Removes one main-surface card (the install row disappearing after a probe, a policy change)
    /// and its latch state. The Close disc is never removed this way.
    /// </summary>
    public void RemoveCard(string cardId)
    {
        if (string.Equals(cardId, CloseTargetId, StringComparison.Ordinal))
            return;

        _mainTargets.RemoveAll(r => string.Equals(r.Id, cardId, StringComparison.Ordinal));
        DwellDetector.RemoveTarget(cardId);
        _latch.Forget(cardId);
    }

    /// <summary>
    /// Opens an inline modal: background cards and the Close disc stop being eavesdroppable
    /// targets BEFORE the modal's own choices are registered, so a gaze resting on a card can never
    /// dwell through the modal. The View then calls <see cref="RegisterModalChoice"/> for each
    /// button and pushes each real centre through <see cref="UpdateModalChoiceCentre"/>.
    /// </summary>
    public void ShowModal(string modalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modalId);
        ResetCardProgress();
        ResetModalProgress();
        ModalId = modalId;
        IsModalOpen = true;
        DwellDetector.ClearTargets();
        _latch.Clear();
        CloseDwellProgress = 0;
        CloseDwellPaused = false;
    }

    /// <summary>
    /// Registers a circular modal choice at the origin and HOLDS it. The View pushes the real centre
    /// after layout through <see cref="UpdateModalChoiceCentre"/>; until then the target cannot fire
    /// even if the gaze happens to rest on the placeholder.
    /// <para>
    /// Production uses the rectangular overload below instead: a modal button is wider than it is
    /// tall, and a circle is the wrong shape for "what is drawn is what is hit". This overload
    /// remains for tests and for any caller whose choice really is a disc.
    /// </para>
    /// </summary>
    /// <param name="choiceId">The choice's id — what <see cref="ModalChoiceActivated"/> reports.</param>
    /// <param name="radiusDip">The drawn button's hit radius in DIP; defaults to the guide's 70 DIP
    /// fallback, and the View should pass the real half-extent where it differs.</param>
    public void RegisterModalChoice(string choiceId, double? radiusDip = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(choiceId);
        _modalTargetIds.Add(choiceId);
        DwellDetector.AddTarget(new DwellTarget(choiceId, new PhysicalPoint(0, 0),
            DpiHelper.LogicalToPhysicalX(radiusDip ?? ModalChoiceRadiusDip), ModalChoiceDwellMs));
        _latch.Hold(choiceId);
    }

    /// <summary>
    /// Registers a RECTANGULAR modal choice from the drawn button's real half-extents in DIP. This
    /// is the production path: the drawn button and the hit target are the same rectangle (B8), so
    /// the confirm/cancel pair of a Quit dialog has no painted area that does not hit. The
    /// placeholder rect is empty-sized and HELD, so it cannot fire before
    /// <see cref="UpdateModalChoiceRect"/> pushes the real on-screen rectangle.
    /// </summary>
    /// <param name="choiceId">The choice's id.</param>
    /// <param name="halfWidthDip">The drawn button's half width in DIP.</param>
    /// <param name="halfHeightDip">The drawn button's half height in DIP.</param>
    public void RegisterModalChoice(string choiceId, double halfWidthDip, double halfHeightDip)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(choiceId);
        if (halfWidthDip <= 0) throw new ArgumentOutOfRangeException(nameof(halfWidthDip));
        if (halfHeightDip <= 0) throw new ArgumentOutOfRangeException(nameof(halfHeightDip));

        _modalTargetIds.Add(choiceId);
        var placeholder = new PhysicalRect(0, 0,
            DpiHelper.LogicalToPhysicalX(halfWidthDip * 2), DpiHelper.LogicalToPhysicalY(halfHeightDip * 2));
        DwellDetector.AddTarget(new DwellTarget(choiceId, placeholder, ModalChoiceDwellMs));
        _latch.Hold(choiceId);
    }

    /// <summary>The View's layout/DPI push for a modal choice: real centre + the latch's positioned
    /// mark (the mark only sticks on an already-held target, so this follows
    /// <see cref="RegisterModalChoice(string, double?)"/>).</summary>
    public void UpdateModalChoiceCentre(string choiceId, PhysicalPoint centre)
    {
        DwellDetector.UpdateTargetCenter(choiceId, centre);
        _latch.Positioned(choiceId);
    }

    /// <summary>
    /// The View's layout/DPI push for a rectangular modal choice: the drawn button's real on-screen
    /// rectangle, rebased without disturbing dwell state, plus the latch's positioned mark.
    /// </summary>
    public void UpdateModalChoiceRect(string choiceId, PhysicalRect screenRect)
    {
        DwellDetector.RebaseTarget(choiceId, screenRect);
        _latch.Positioned(choiceId);
    }

    /// <summary>
    /// Closes the modal and re-registers the cards + Close disc the previous layout reported, each
    /// as a fresh target and each HELD again — the gaze that just answered the modal is still inside
    /// the card underneath, and must leave before it can dwell anything.
    /// </summary>
    public void DismissModal()
    {
        if (!IsModalOpen)
            return;

        IsModalOpen = false;
        ModalId = null;
        RestoreMainSurface();
    }

    /// <summary>
    /// The mouse/keyboard path to exactly what a completed dwell does. It clears the live targets
    /// first (the same OnTargetActivated contract), so a caregiver click cannot leave a gaze dwell
    /// running underneath the effect. Inert while hidden.
    /// </summary>
    public void ActivateForMouse(string targetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        if (!IsVisible)
            return;

        // The flag lives only for the synchronous activation it wraps, so a later gaze activation
        // can never inherit it. The close branch below reads it before raising CloseRequested.
        _activationWasMouse = true;
        try
        {
            OnTargetActivated(targetId);
        }
        finally
        {
            _activationWasMouse = false;
        }
    }

    /// <summary>
    /// Re-registers the remembered main-surface cards + Close disc after a modal (or after an
    /// activation that did not open one), all held. Clears whatever targets were live first, so a
    /// modal's choices cannot survive into the grid. No-op while hidden or while a modal is open
    /// (its choices are the only valid targets then).
    /// </summary>
    public void RestoreMainSurface()
    {
        // The guard is load-bearing, not defensive: a re-layout that runs while a confirmation is
        // up must only remember the card's new geometry. Clearing here would re-register the grid
        // UNDER the modal, and OnTargetActivated would then deliver a card id as the modal answer.
        if (!IsVisible || IsModalOpen)
            return;

        ResetCardProgress();
        ResetModalProgress();
        DwellDetector.ClearTargets();
        _latch.Clear();
        CloseDwellProgress = 0;
        CloseDwellPaused = false;
        foreach (var registration in _mainTargets)
            RegisterHeld(CreateTarget(registration));
    }

    /// <summary>
    /// Per-sample path. The base only feeds while visible, so this override adds the pause badge and
    /// the latch's pre-update arming pass, and must keep the base contract: stamp the pause flag and
    /// route the feed through <see cref="DwellOverlayViewModelBase.SampleMayDwell"/>.
    /// </summary>
    public override void Receive(GazeCursorMovedMessage message)
    {
        NoteSamplePaused(message.IsPaused);
        PausedIndicatorVisible = IsVisible && message.IsPaused;

        if (!IsVisible || !SampleMayDwell(message))
            return;

        _latch.Update(message.Position);
        DwellDetector.Update(message.Position, message.TimestampMicroseconds, _latch.Filter);
    }

    protected override void OnTargetActivated(string targetId)
    {
        // The Settings precedent (SettingsWindowViewModel) and the guide's chrome: clear BEFORE
        // dispatching, so the handler cannot re-enter the same target and nothing behind a modal
        // can fire while the effect runs.
        DwellDetector.ClearTargets();
        _latch.Clear();

        if (string.Equals(targetId, CloseTargetId, StringComparison.Ordinal))
        {
            CloseRequestSource = _activationWasMouse
                ? StrandingLedger.BackstageSources.Mouse
                : StrandingLedger.BackstageSources.Disc;
            CloseRequested?.Invoke();
            HideOverlay();
            return;
        }

        if (IsModalOpen)
        {
            ModalChoiceActivated?.Invoke(targetId);
            return;
        }

        CardActivated?.Invoke(targetId);
    }

    protected override void OnTargetProgress(string targetId, double progress)
    {
        if (string.Equals(targetId, CloseTargetId, StringComparison.Ordinal))
        {
            CloseDwellProgress = progress;
            return;
        }

        if (IsModalOpen)
            ModalProgress?.Invoke(targetId, progress);
        else
            CardProgress?.Invoke(targetId, progress);
    }

    protected override void OnTargetPauseChanged(string targetId, bool paused)
    {
        if (string.Equals(targetId, CloseTargetId, StringComparison.Ordinal))
            CloseDwellPaused = paused;
    }

    /// <summary>Adds or replaces one registration in the remembered layout; true when it replaced
    /// an entry (the View is re-reporting an existing face, not adding one).</summary>
    private bool StoreRegistration(MainRegistration registration)
    {
        var existing = _mainTargets.FindIndex(r => string.Equals(r.Id, registration.Id, StringComparison.Ordinal));
        if (existing < 0)
        {
            _mainTargets.Add(registration);
            return false;
        }

        _mainTargets[existing] = registration;
        return true;
    }

    /// <summary>
    /// Drops every card's progress bar to zero before a clear. <c>ClearTargets()</c> emits no
    /// progress, so a card dwell interrupted by a modal it did not open (or by a hide / a restore)
    /// would otherwise leave its bar frozen part-filled — and a part-filled bar reads as "still
    /// armed" right next to Quit. The Close disc has its own observable and is not a card.
    /// </summary>
    private void ResetCardProgress()
    {
        // A snapshot, not the live list: CardProgress is a public event, and a handler that
        // re-enters the VM (a View rebuilding its layout synchronously) must not invalidate the
        // enumeration mid-raise.
        foreach (var registration in _mainTargets.ToArray())
        {
            if (registration is CardRegistration)
                CardProgress?.Invoke(registration.Id, 0);
        }
    }

    /// <summary>
    /// Drops every live modal choice's progress bar to zero before the modal is replaced, dismissed
    /// or hidden. Same reason as <see cref="ResetCardProgress"/>: ClearTargets emits no progress, so
    /// a dismissed/replaced modal would otherwise leave a part-filled bar reading as still armed.
    /// </summary>
    private void ResetModalProgress()
    {
        if (_modalTargetIds.Count == 0)
            return;

        foreach (var id in _modalTargetIds.ToArray())
            ModalProgress?.Invoke(id, 0);
        _modalTargetIds.Clear();
    }

    /// <summary>Adds a fresh target and puts it behind the leave-before-arm latch. Its centre is
    /// already real (the View reported it), so it counts as positioned; arming happens on the first
    /// sample that finds the gaze outside its release area.</summary>
    private void RegisterHeld(DwellTarget target)
    {
        DwellDetector.AddTarget(target);
        _latch.Hold(target.Id);
        _latch.Positioned(target.Id);
    }

    private DwellTarget CreateTarget(MainRegistration registration) => registration switch
    {
        CardRegistration card => new DwellTarget(card.Id, card.Bounds, CardDwellMs),
        CloseRegistration close => new DwellTarget(CloseTargetId, close.Centre, close.Radius, CloseDwellMs),
        _ => throw new InvalidOperationException($"Unknown Backstage registration {registration.GetType().Name}."),
    };

    /// <summary>One main-surface registration: a card's drawn rectangle or the Close disc's circle.</summary>
    private abstract record MainRegistration(string Id);

    private sealed record CardRegistration(string CardId, PhysicalRect Bounds) : MainRegistration(CardId);

    private sealed record CloseRegistration(PhysicalPoint Centre, double Radius)
        : MainRegistration(CloseTargetId);
}
