namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging;

/// <summary>
/// Fullscreen Task Switcher overlay ViewModel. Enumerates open windows via
/// <see cref="IWindowManager"/> into <see cref="Tiles"/> when toggled on — leaving out the
/// window that is in the foreground at that moment, so the switcher never offers the window the
/// owner means to leave — and exposes
/// dwell targets over each tile plus a dedicated close target (<see cref="CloseTargetId"/>,
/// a pure dismissal that never selects a window), and sends
/// <see cref="TaskSwitcherSelectionMessage"/> when a tile is dwelt on. WPF-free
/// (ViewModels targets plain net10.0): the View (TaskSwitcherWindow) resolves icons via
/// IAppIconProvider using each tile's ProcessId.
/// <para>
/// T3: the VM also writes ONE content-free <c>Information</c> line per open and one per close
/// (see <see cref="WriteOpenLineIfPending"/> / <see cref="BuildCloseLine"/>). The debate's
/// look-time ladder is decided by the share of picks followed by a reopen within 10 s, which
/// only these lines can measure. The lines carry integers, booleans and closed-set ids only —
/// never a window title, process name or path — so the log cannot spell out what was on screen.
/// </para>
/// </summary>
public partial class TaskSwitcherViewModel : DwellOverlayViewModelBase,
    IRecipient<TaskSwitcherToggledMessage>
{
    private readonly IWindowManager _windowManager;
    private readonly IMessenger _messenger;
    private readonly EyeCtrlSettings _settings;  // H2 fix: declared + assigned
    private readonly ILogger<TaskSwitcherViewModel>? _logger;
    private readonly TimeProvider _timeProvider;

    public ObservableCollection<TaskSwitcherTileViewModel> Tiles { get; } = new();

    /// <summary>Landing guard: no tile accumulates from the gaze that opened the switcher.</summary>
    private readonly FreshEntryGuard _landingGuard;

    // Last valid (non-gap) sample the VM saw, and whether the most recent sample was a gap. The VM
    // is a live IRecipient while hidden, so the sample stream is continuous across sessions; the
    // gap flag is what makes an anchor taken during a blink invalid rather than the stale point.
    private PhysicalPoint? _lastValidSample;
    private bool _lastSampleWasGap;

    /// <summary>
    /// How a switcher session ended. The ids written to the log are the closed set the ticket
    /// names: <c>Picked</c> (a tile switch), <c>CloseDisc</c>, <c>PausedDismiss</c> (a tile look
    /// while paused — a dismissal, never a switch) and <c>Other</c> (any other hide path: the
    /// toggle, a coordinator dismissal, a shutdown).
    /// </summary>
    private enum SessionOutcome
    {
        Other,
        Picked,
        CloseDisc,
        PausedDismiss,
    }

    /// <summary>
    /// Two tile faces count as the same grid row when their physical tops differ by no more than
    /// this. A WrapPanel gives every item in a row the same Y, so this only absorbs sub-pixel
    /// rounding at fractional display scales.
    /// </summary>
    private const double RowTolerancePhysical = 1.0;

    // Session record (T3). Reset at every show; the open line is written once the View has
    // finished its post-layout registration (reachable is only known then), the close line from
    // the hide path. Logging is best-effort: every write is wrapped, because a failing sink must
    // never reach the gaze path.
    private readonly List<(string TargetId, double Top)> _registeredTiles = new();
    private readonly Dictionary<string, DateTimeOffset> _eligibleAt = new(StringComparer.Ordinal);
    private DateTimeOffset _sessionOpenedAt;
    private DateTimeOffset? _guardLiftedAt;
    private DateTimeOffset? _lastCloseAt;
    private string _lastOutcomeId = "none";
    private SessionOutcome _sessionOutcome = SessionOutcome.Other;
    private string? _sessionPickedTargetId;
    private int _sessionTileCount;
    private bool _sessionCurrentExcluded;
    private int _sessionSlot = -1;
    private bool _anchorValid;
    private bool _sessionOpen;
    private bool _openLineWritten;

    /// <summary>Reserved dwell-target id for the switcher's close button (not a window tile).</summary>
    public const string CloseTargetId = "__taskswitcher_close__";
    [ObservableProperty] private double _closeDwellProgress;
    /// <summary>True while the close button's dwell is held (gaze stepped off mid-dwell): its bar dims.</summary>
    [ObservableProperty] private bool _closeDwellPaused;

    public TaskSwitcherViewModel(
        IWindowManager windowManager,
        EyeCtrlSettings settings,
        IMessenger messenger,
        IStruggleTelemetrySink? sink = null,
        IDwellDetectorRegistry? dwellRegistry = null,
        ILogger<TaskSwitcherViewModel>? logger = null,
        TimeProvider? timeProvider = null)
        : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.TaskSwitcher, sink, dwellRegistry, settings.Dwell)
    {
        _windowManager = windowManager;
        _messenger = messenger;
        _settings = settings;  // H2 fix: assigned
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        // The guard's two hooks only timestamp the session log; neither can change a decision.
        _landingGuard = new FreshEntryGuard(OnGuardAnchorLifted, OnGuardTargetEligible);
    }

    /// <summary>
    /// Toggled by the toolbar Task Switcher button. Shows the overlay if hidden,
    /// hides it if shown.
    /// </summary>
    public void Receive(TaskSwitcherToggledMessage message)
    {
        if (IsVisible)
            HideOverlay();
        else
            ShowOverlay();
    }

    private void ShowOverlay()
    {
        // Repopulate tiles from the live window list. Dwell targets are NOT created here:
        // the View computes each tile's real on-screen rectangle (its visible face) after
        // layout and calls UpdateTilePosition(), which creates the target at its true
        // position. Creating all targets up front at a shared (0,0) placeholder would let
        // gaze accumulate dwell on the overlapping origin targets during the layout gap
        // (the old (0,0) race).
        Tiles.Clear();
        DwellDetector.ClearTargets();
        CloseDwellProgress = 0;
        CloseDwellPaused = false;

        // The landing guard: anchor the rule at the gaze that opened the switcher. The anchor is
        // invalid when the last sample was a gap — a blink at opening says nothing about where
        // the gaze is, and the frozen point would otherwise be trusted as a real landing spot.
        var anchor = _lastSampleWasGap ? null : _lastValidSample;

        // T3: start the session record before anything can hide it again. The open line waits
        // for the View's layout pass (reachable is not known until then) or, for a session that
        // closes first, is flushed by the close.
        BeginSession(anchorValid: anchor is not null);

        _landingGuard.Arm(anchor);

        // The window the owner is in gets no tile, so the window used BEFORE it becomes the
        // first tile (Windows Alt-Tab does the same). Read here — at the open, in the same show
        // path that enumerates — and never cached: the foreground at this instant is the only
        // authority on "current". It is read before IsVisible flips, i.e. before the View shows
        // anything; every overlay is WS_EX_NOACTIVATE anyway, so the owner's app still holds the
        // foreground. Fail-closed: a null (desktop, shell, EyeCtrl's own window) excludes nothing,
        // as does a foreground that is not in the enumerated list.
        var currentWindow = _windowManager.TryGetEligibleForegroundWindow();
        var currentExcluded = false;

        foreach (var window in _windowManager.EnumerateOpenWindows())
        {
            if (currentWindow is { } current && window.Hwnd == current)
            {
                currentExcluded = true;
                continue;
            }

            Tiles.Add(new TaskSwitcherTileViewModel(
                window.Hwnd, window.Title, window.ProcessId, window.ProcessName,
                new PhysicalPoint(0, 0)));
        }

        _sessionTileCount = Tiles.Count;
        _sessionCurrentExcluded = currentExcluded;

        // Flip IsVisible so the EventCoordinator shows the fullscreen window. This is the
        // show trigger — the coordinator watches IsVisible. Dwell processing is harmless
        // until targets exist (UpdateTilePosition creates them once layout completes).
        IsVisible = true;
    }

    /// <summary>
    /// Hides the overlay, clears tiles + dwell targets. Public so the EventCoordinator
    /// can dismiss the overlay after a selection (in addition to the toggle path).
    /// </summary>
    public void HideOverlay()
    {
        // One close line per session, not two. In production the VM's hide for a pick runs once
        // (from OnTargetActivated) — QuickActionDispatcher's post-pick dismissal goes to the
        // WINDOW's ITaskSwitcherOverlay.HideOverlay, a different object — but this method is
        // public, so a second call must not write a second line. `closedASession` is read BEFORE
        // IsVisible flips, and EndSession refuses to re-enter.
        var closedASession = IsVisible;

        IsVisible = false;
        DwellDetector.ClearTargets();
        Tiles.Clear();
        _landingGuard.Disarm();
        // A progress bar left over from the hidden session must not greet the next show (the
        // VM is a DI singleton — the next show reuses this instance, AppBootstrapper pattern).
        CloseDwellProgress = 0;
        CloseDwellPaused = false;

        if (closedASession)
            EndSession();
    }

    /// <summary>
    /// Per-sample path. The base only feeds while visible, so this override adds two things and
    /// must keep the base contract: stamp the pause flag (round-2 finding 2 — an override that
    /// skipped <see cref="DwellOverlayViewModelBase.NoteSamplePaused"/> silently disarmed the pause
    /// suppression), and route the feed through <see cref="DwellOverlayViewModelBase.SampleMayDwell"/>.
    /// <para>
    /// Every non-gap sample is recorded even while hidden: the VM stays a live recipient, so the
    /// last sample before a show is the landing anchor and the previous valid one is what the
    /// entry gate compares against. The guard filters TILE targets only — the close disc is the
    /// escape and is never gated (a dead close path would make the fullscreen overlay
    /// inescapable).
    /// </para>
    /// </summary>
    public override void Receive(GazeCursorMovedMessage message)
    {
        NoteSamplePaused(message.IsPaused);

        if (message.IsInGap)
        {
            _lastSampleWasGap = true;
        }
        else
        {
            _lastSampleWasGap = false;
            _lastValidSample = message.Position;
            _landingGuard.ObserveValidSample(message.Position, DwellDetector.Targets);
        }

        if (!IsVisible || !SampleMayDwell(message))
            return;

        if (_landingGuard.IsArmed)
        {
            DwellDetector.Update(message.Position, message.TimestampMicroseconds,
                target => target.Id == CloseTargetId
                    || _landingGuard.AllowsDwell(target.Id));
        }
        else
        {
            DwellDetector.Update(message.Position, message.TimestampMicroseconds);
        }
    }

    /// <summary>
    /// Called by the View after layout to register the close disc as a dwell target.
    /// Mirrors <see cref="QuickActionsGridViewModel.UpdateCloseButtonPosition"/> — same
    /// physical-pixel convention and the same <see cref="DwellSettings.ToolbarButtonDwellMs"/>.
    /// <para>
    /// It is also the LAST step of the View's post-layout registration pass
    /// (<c>TaskSwitcherWindow.UpdateTilePositions</c> registers every visible tile face, then
    /// this), which is where the open line's <c>reachable</c> count is final — so this flushes
    /// it. A session that hides before any layout flushes the open line from the close path
    /// instead (T3).
    /// </para>
    /// </summary>
    public void UpdateCloseButtonPosition(PhysicalPoint screenCenter)
    {
        DwellDetector.AddTarget(new DwellTarget(
            CloseTargetId, screenCenter,
            DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius),
            _settings.Dwell.ToolbarButtonDwellMs));

        WriteOpenLineIfPending();
    }

    // Deliberately dwellable while paused (DwellOverlayViewModelBase.DwellActiveWhilePaused):
    // the close target and the tile dwells must stay dwellable while paused so a paused
    // fullscreen switcher stays escapable; the effect stays gated via LastSampleWasPaused.
    // While paused a tile dwell dismisses (never switches — see OnTargetActivated) and the
    // close target always just dismisses.
    protected override bool DwellActiveWhilePaused => true;

    protected override void OnTargetActivated(string targetId)
    {
        // The close target is a pure dismissal — paused or not it NEVER sends the selection
        // (OS input); it only hides the overlay.
        if (targetId == CloseTargetId)
        {
            _sessionOutcome = SessionOutcome.CloseDisc;
            HideOverlay();
            return;
        }

        // T3: record what this activation was before hiding, while the tile list is still alive.
        // A tile look is a switch (Picked) unless paused, where it is a dismiss (PausedDismiss) —
        // the same distinction the send below makes. The grid position is read from the faces
        // the View registered, so the log follows the layout that was actually drawn.
        var tile = Tiles.FirstOrDefault(t => t.Hwnd.ToString() == targetId);
        if (tile != null)
        {
            _sessionPickedTargetId = targetId;
            _sessionSlot = Tiles.IndexOf(tile);
            _sessionOutcome = LastSampleWasPaused ? SessionOutcome.PausedDismiss : SessionOutcome.Picked;
        }

        // targetId is the hwnd string. While paused the dwell is a DISMISS, never a switch —
        // suppressing the selection send (OS input) but keeping HideOverlay is what keeps the
        // overlay escapable while still obeying "no input leaves EyeCtrl while paused".
        if (!LastSampleWasPaused && IntPtr.TryParse(targetId, out var hwnd))
        {
            _messenger.Send(new TaskSwitcherSelectionMessage(hwnd));
        }
        HideOverlay();
    }

    protected override void OnTargetProgress(string targetId, double progress)
    {
        if (targetId == CloseTargetId)
        {
            CloseDwellProgress = progress;
            return;
        }
        var tile = Tiles.FirstOrDefault(t => t.Hwnd.ToString() == targetId);
        if (tile != null)
            tile.DwellProgress = progress;
    }

    protected override void OnTargetPauseChanged(string targetId, bool paused)
    {
        if (targetId == CloseTargetId)
        {
            CloseDwellPaused = paused;
            return;
        }
        var tile = Tiles.FirstOrDefault(t => t.Hwnd.ToString() == targetId);
        if (tile != null)
            tile.IsDwellPaused = paused;
    }

    /// <summary>
    /// Called by the View once a tile's container is laid out, with the tile's real on-screen
    /// rectangle (already clamped to the visible face for clipped rows). Creates (or
    /// repositions — <see cref="DwellDetector.AddTarget"/> replaces by id) a RECTANGULAR dwell
    /// target here so the detector hit-tests the drawn tile face: the tiles are 184x104 DIP
    /// faces (200x120 cells minus the template border's 8 DIP margin), and the old r50 circle
    /// covered only ~41% of every visible face, leaving ~59% dead — gaze accumulated nothing
    /// there, with no cue explaining why (the same drawn-vs-hit mismatch QuickActions fixed as
    /// a field finding, and the class behind the 2026-08-17 'tiles gave no feedback' report).
    /// Reuses <c>ToolbarButtonDwellMs</c> (700 ms default): tiles are large so it feels easy to
    /// hit, which is intentional for a fullscreen picker. If user testing finds it too
    /// sensitive, add a dedicated TaskSwitcherTileDwellMs field to DwellSettings.
    /// </summary>
    public void UpdateTilePosition(IntPtr hwnd, PhysicalRect screenRect)
    {
        var tile = Tiles.FirstOrDefault(t => t.Hwnd == hwnd);
        if (tile != null)
        {
            tile.Position = screenRect.Center;

            // T3: the faces registered in this session, in registration (= display) order, with
            // their physical top edge. `reachable` for the open line is this list's count, and
            // the picked tile's row/col come from these tops — the geometry that was drawn, not
            // a recomputed grid. A re-registration of the same tile replaces its entry.
            var targetId = hwnd.ToString();
            var existing = _registeredTiles.FindIndex(entry => entry.TargetId == targetId);
            if (existing >= 0)
                _registeredTiles[existing] = (targetId, screenRect.Y);
            else
                _registeredTiles.Add((targetId, screenRect.Y));
        }

        DwellDetector.AddTarget(new DwellTarget(
            hwnd.ToString(),
            screenRect,
            _settings.Dwell.ToolbarButtonDwellMs));
    }

    // ── T3 session log ────────────────────────────────────────────────────────────────────────
    // One Information line per open and one per close, so the quick-reopen rate (an open whose
    // lastOutcome is Picked and whose sinceLastCloseMs < 10000) can be computed from the day log.
    // Both lines stay content-free: every value is an integer, a boolean or one of the closed
    // ids above. The debate's look-time ladder is decided by these numbers, so they are worth
    // keeping exact — and worth keeping safe: every write is wrapped, because a throwing sink
    // must never reach the gaze path.

    /// <summary>Starts the record for one session: open time, fresh outcome, fresh grid data.</summary>
    private void BeginSession(bool anchorValid)
    {
        _sessionOpen = true;
        _openLineWritten = false;
        _sessionOpenedAt = _timeProvider.GetUtcNow();
        _anchorValid = anchorValid;
        _guardLiftedAt = null;
        _sessionOutcome = SessionOutcome.Other;
        _sessionPickedTargetId = null;
        _sessionSlot = -1;
        _sessionTileCount = 0;
        _sessionCurrentExcluded = false;
        _registeredTiles.Clear();
        _eligibleAt.Clear();
    }

    /// <summary>Writes the open line once, then the close line, and remembers the outcome.</summary>
    private void EndSession()
    {
        if (!_sessionOpen)
            return;

        // A session that hid before its layout pass still owes its open line; the two lines must
        // stay paired and in order in the log.
        WriteOpenLineIfPending();
        _sessionOpen = false;

        var closedAt = _timeProvider.GetUtcNow();
        try
        {
            var line = BuildCloseLine(closedAt);
            _logger?.LogInformation("{Line}", line);
        }
        catch (Exception)
        {
            // A logging failure must never reach the gaze path.
        }

        _lastCloseAt = closedAt;
        _lastOutcomeId = OutcomeId(_sessionOutcome);
    }

    /// <summary>
    /// Writes "Task switcher opened: …" once per session. Called when the View's registration
    /// pass completes (reachable is final), or from the close when the session never got there.
    /// <c>sinceLastCloseMs</c> is the previous close → THIS session's OPEN, never this flush
    /// (the two differ by the layout latency, which would bias the quick-reopen predicate).
    /// <c>currentExcluded</c> says whether the foreground window at the open was left out of the
    /// tiles (one boolean — never which window it was).
    /// </summary>
    private void WriteOpenLineIfPending()
    {
        if (!_sessionOpen || _openLineWritten)
            return;

        _openLineWritten = true;
        try
        {
            // Measured at the OPEN, not at this flush: the flush runs after the View's
            // Loaded-priority layout (or at the close, for a session that never laid out), and
            // reading the clock here would bias every value upward by that latency — always in
            // the same direction, so a 9.8 s quick reopen would cross the 10 s cutoff the whole
            // measurement exists for. `_sessionOpenedAt` also gives `openMs` and
            // `sinceLastCloseMs` the same reference point (T3 review finding 1).
            var sinceLastClose = _lastCloseAt is { } lastClose
                ? ElapsedMs(lastClose, _sessionOpenedAt).ToString(CultureInfo.InvariantCulture)
                : "none";

            var line = string.Create(CultureInfo.InvariantCulture,
                $"Task switcher opened: tiles={_sessionTileCount} currentExcluded={(_sessionCurrentExcluded ? "true" : "false")} reachable={_registeredTiles.Count} anchorValid={(_anchorValid ? "true" : "false")} sinceLastCloseMs={sinceLastClose} lastOutcome={_lastOutcomeId}");
            _logger?.LogInformation("{Line}", line);
        }
        catch (Exception)
        {
            // A logging failure must never reach the gaze path.
        }
    }

    /// <summary>
    /// Builds the close line. <c>guardLiftMs</c> is <c>n/a</c> when the opening anchor was
    /// invalid (there was no distance gate to lift) and <c>never</c> when it did not lift before
    /// the close; <c>liftToEntryMs</c> is the lift→entry span for a pick and <c>n/a</c>
    /// otherwise; <c>slot/row/col</c> are <c>-</c> when no tile was picked.
    /// </summary>
    private string BuildCloseLine(DateTimeOffset closedAt)
    {
        var openMs = ElapsedMs(_sessionOpenedAt, closedAt).ToString(CultureInfo.InvariantCulture);
        var guardLiftMs = GuardLiftMs();
        var liftToEntryMs = LiftToEntryMs();
        var (row, col) = TileGridPosition(_registeredTiles, _sessionPickedTargetId);

        var slot = _sessionSlot >= 0 ? _sessionSlot.ToString(CultureInfo.InvariantCulture) : "-";
        var rowText = row >= 0 ? row.ToString(CultureInfo.InvariantCulture) : "-";
        var colText = col >= 0 ? col.ToString(CultureInfo.InvariantCulture) : "-";

        return string.Create(CultureInfo.InvariantCulture,
            $"Task switcher closed: outcome={OutcomeId(_sessionOutcome)} openMs={openMs} guardLiftMs={guardLiftMs} liftToEntryMs={liftToEntryMs} slot={slot} row={rowText} col={colText}");
    }

    /// <summary>The distance gate's lift, in ms after the open, or <c>n/a</c> / <c>never</c>.</summary>
    private string GuardLiftMs()
    {
        if (!_anchorValid)
            return "n/a";
        if (_guardLiftedAt is not { } lifted)
            return "never";
        return ElapsedMs(_sessionOpenedAt, lifted).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Lift→entry for the picked tile; <c>n/a</c> for every other outcome.</summary>
    private string LiftToEntryMs()
    {
        if (_sessionOutcome != SessionOutcome.Picked || !_anchorValid
            || _guardLiftedAt is not { } lifted
            || _sessionPickedTargetId is not { } picked
            || !_eligibleAt.TryGetValue(picked, out var eligibleAt))
        {
            return "n/a";
        }

        return ElapsedMs(lifted, eligibleAt).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>One closed-set id per outcome; the values are pinned by the session-log tests.</summary>
    private static string OutcomeId(SessionOutcome outcome)
    {
        switch (outcome)
        {
            case SessionOutcome.Picked: return "Picked";
            case SessionOutcome.CloseDisc: return "CloseDisc";
            case SessionOutcome.PausedDismiss: return "PausedDismiss";
            default: return "Other";
        }
    }

    /// <summary>
    /// The picked tile's grid position, read from the registered faces: rows are runs of equal
    /// top edges (within <see cref="RowTolerancePhysical"/>), columns are the ordinal inside the
    /// run. Returns (-1, -1) when no tile was picked (or it was never registered).
    /// </summary>
    private static (int Row, int Col) TileGridPosition(
        IReadOnlyList<(string TargetId, double Top)> registered, string? pickedTargetId)
    {
        if (pickedTargetId is null)
            return (-1, -1);

        var row = -1;
        var col = 0;
        var rowTop = double.NaN;
        foreach (var (targetId, top) in registered)
        {
            if (row < 0 || Math.Abs(top - rowTop) > RowTolerancePhysical)
            {
                row++;
                col = 0;
                rowTop = top;
            }

            if (targetId == pickedTargetId)
                return (row, col);

            col++;
        }

        return (-1, -1);
    }

    /// <summary>Whole milliseconds between two clock reads, never negative.</summary>
    private static long ElapsedMs(DateTimeOffset from, DateTimeOffset to) =>
        Math.Max(0, (long)Math.Round((to - from).TotalMilliseconds));

    /// <summary>The guard's distance gate lifted on this sample (T3 hook; timestamps only).</summary>
    private void OnGuardAnchorLifted() => _guardLiftedAt ??= _timeProvider.GetUtcNow();

    /// <summary>A target became dwell-eligible on this sample (T3 hook; timestamps only).</summary>
    private void OnGuardTargetEligible(string targetId) => _eligibleAt.TryAdd(targetId, _timeProvider.GetUtcNow());
}
