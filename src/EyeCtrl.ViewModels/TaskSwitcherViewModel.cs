namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
/// S2-B: the VM also carries the three states the tiles cannot show by their content alone — a
/// minimised window (<see cref="TaskSwitcherTileViewModel.IsMinimized"/>, drawn as a badge),
/// a session with no window at all (<see cref="IsEmpty"/>), and windows beyond the grid's
/// capacity (<see cref="OverflowCount"/>). Each is display-only: none of the three is a dwell
/// target, so the tile face and the ✕ stay the only things gaze can act on.
/// </para>
/// <para>
/// T3: the VM also writes ONE content-free <c>Information</c> line per open and one per close
/// (see <see cref="WriteOpenLineIfPending"/> / <see cref="BuildCloseLine"/>). The debate's
/// look-time ladder is decided by the share of picks followed by a reopen within 10 s, which
/// only these lines can measure. The lines carry integers, booleans and closed-set ids only —
/// never a window title, process name or path — so the log cannot spell out what was on screen.
/// </para>
/// <para>
/// S3-A: the session's LAST tile is "Show desktop" (<see cref="DesktopTile"/>), shown whenever the
/// session has a window to minimise (a tile, or the left-out foreground window) and hidden when it
/// has none. It is not a window: it lives only in <see cref="PanelItems"/> (what the grid draws),
/// never in <see cref="Tiles"/>, so the window-tile rules S2-A/S2-B built — counts, badges, the
/// overflow chip — keep their meaning. Firing it minimises the session's enumerated windows one by
/// one in the App (never the shell's MinimizeAll, and never an EyeCtrl surface) and closes the
/// switcher with outcome <c>Desktop</c>.
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

    /// <summary>The window tiles of this session — one per enumerated window, current one left out.</summary>
    public ObservableCollection<TaskSwitcherTileViewModel> Tiles { get; } = new();

    /// <summary>
    /// What the grid DRAWS, in display order: every <see cref="Tiles"/> entry, then
    /// <see cref="DesktopTile"/> when the session has one. It mirrors <see cref="Tiles"/> so the
    /// ItemsControl has one source to bind to, while the window-only collection keeps its S2-A/S2-B
    /// meaning (counts, badges, overflow). The desktop tile is always last — the ticket's placement
    /// rule — and a session that runs out of grid capacity loses the last WINDOW, never this tile.
    /// </summary>
    public ObservableCollection<TaskSwitcherTileViewModel> PanelItems { get; } = new();

    /// <summary>
    /// The "Show desktop" tile of this session, or null when the session has nothing to minimise
    /// (no window at all). Hidden with the rest on <see cref="HideOverlay"/>: the VM is a DI
    /// singleton, so a leftover tile would offer an action with no windows behind it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDesktopTile))]
    private TaskSwitcherTileViewModel? _desktopTile;

    /// <summary>Whether this session draws the "Show desktop" tile.</summary>
    public bool HasDesktopTile => DesktopTile is not null;

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
    /// while paused — a dismissal, never a switch), <c>Desktop</c> (the "Show desktop" tile
    /// minimised the session's windows) and <c>Other</c> (any other hide path: the toggle, a
    /// coordinator dismissal, a shutdown).
    /// </summary>
    private enum SessionOutcome
    {
        Other,
        Picked,
        CloseDisc,
        PausedDismiss,
        Desktop,
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
    private readonly List<(string TargetId, double Top, bool IsDesktop)> _registeredTiles = new();
    private readonly Dictionary<string, DateTimeOffset> _eligibleAt = new(StringComparer.Ordinal);
    private DateTimeOffset _sessionOpenedAt;
    private DateTimeOffset? _guardLiftedAt;
    private DateTimeOffset? _lastCloseAt;
    private string _lastOutcomeId = "none";
    private SessionOutcome _sessionOutcome = SessionOutcome.Other;
    private string? _sessionPickedTargetId;
    private int _sessionTileCount;
    private int _sessionTileDwellMs;
    private int _sessionPreviewCount;
    private int _sessionPreviewFailures;
    private int _sessionPreviewHresult;
    private bool _sessionCurrentExcluded;
    private int _sessionSlot = -1;
    private bool _anchorValid;
    private bool _sessionOpen;
    private bool _openLineWritten;

    /// <summary>
    /// The session's "show desktop" work: every window the enumeration returned (the current
    /// window included — it is left out of the TILES, not out of the action), minus EyeCtrl's own
    /// process. A foreground the enumeration filtered out is NOT in it: the action only touches
    /// windows the owner was shown or knowingly left out. Captured at the open, so the action can
    /// never touch a window that appeared after the owner saw the grid. Empty for a session with
    /// no desktop tile.
    /// </summary>
    private readonly List<IntPtr> _sessionMinimizeTargets = new();

    /// <summary>Reserved dwell-target id for the switcher's close button (not a window tile).</summary>
    public const string CloseTargetId = "__taskswitcher_close__";

    /// <summary>Reserved dwell-target id for the "Show desktop" tile (not a window tile).</summary>
    public const string DesktopTargetId = "__taskswitcher_desktop__";

    /// <summary>
    /// The tile dwell in effect: <c>max(TaskSwitcherTileDwellMs, ToolbarButtonDwellMs)</c>
    /// (S4-A) — a tile is never quicker than the toolbar, so a slower toolbar setting slows the
    /// tiles with it. The window tiles and the desktop tile alike; the close disc is NOT here
    /// (it keeps <see cref="DwellSettings.ToolbarButtonDwellMs"/> so the escape stays quick).
    /// </summary>
    private int EffectiveTileDwellMs => Math.Max(
        _settings.Dwell.TaskSwitcherTileDwellMs,
        _settings.Dwell.ToolbarButtonDwellMs);

    /// <summary>
    /// This process. "Show desktop" skips any window it owns — defensively, because
    /// <see cref="IWindowManager.EnumerateOpenWindows"/> already drops the tool-window overlays,
    /// but a minimised EyeCtrl surface would take away the owner's only way to control the PC.
    /// </summary>
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    [ObservableProperty] private double _closeDwellProgress;
    /// <summary>True while the close button's dwell is held (gaze stepped off mid-dwell): its bar dims.</summary>
    [ObservableProperty] private bool _closeDwellPaused;

    /// <summary>
    /// True when the session has nothing to draw at all: the View shows "Keine offenen Fenster" in
    /// the tile area instead of an empty grid. Read at the open from the panel the enumeration
    /// produced — so a session whose only window is the foreground one is NOT empty any more (S3-A
    /// draws the desktop tile for it), while a session with no window whatsoever is. A bare dark
    /// screen with a hint that names nothing is the state this replaces.
    /// </summary>
    [ObservableProperty] private bool _isEmpty;

    /// <summary>
    /// How many enumerated windows have NO tile on screen — the windows beyond the grid's capacity,
    /// which are enumerated but unreachable. This is the same "reachable" the T3 open line counts
    /// (<see cref="_registeredTiles"/>: tiles whose visible face got a dwell target), so the chip
    /// and the log can never disagree about what was on screen. The View draws it as
    /// "Weitere Fenster: N" in the hint row. Zero until the View's post-layout registration pass
    /// completes, because reachability is geometric.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverflow))]
    private int _overflowCount;

    /// <summary>Whether any enumerated window has no tile (the overflow chip's visibility).</summary>
    public bool HasOverflow => OverflowCount > 0;

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
        // One source for the grid: PanelItems mirrors the window tiles and appends the desktop
        // tile. Subscribing here (rather than rebuilding in every show/hide/trim path) means a
        // caller that edits Tiles directly — the App layout tests do exactly that — still gets
        // its tiles DRAWN.
        Tiles.CollectionChanged += OnTilesChanged;
    }

    private void OnTilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Incremental mirror (fixup-01 F4): ShowOverlay adds one tile at a time and
        // ApplyTileCapacity removes one at a time, so a full Clear+Add per change produced n^2/2
        // collection events for the bound ItemsControl (about 2,000 at 64 windows) on the UI
        // thread at every open. Window entries occupy PanelItems 0..Tiles.Count-1 in the same
        // order, so an add inserts at its own index and a remove drops its item; anything else
        // (a Reset from Tiles.Clear, a Move, a Replace) rebuilds.
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is { } added
            && e.NewStartingIndex >= 0)
        {
            for (var i = 0; i < added.Count; i++)
            {
                PanelItems.Insert(e.NewStartingIndex + i,
                    (TaskSwitcherTileViewModel)added[i]!);
            }

            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems is { } removed)
        {
            foreach (var item in removed)
                PanelItems.Remove((TaskSwitcherTileViewModel)item!);

            return;
        }

        RebuildPanelItems();
    }

    /// <summary>The desktop tile's own change is a grid change: append it (or drop it) at the end.</summary>
    partial void OnDesktopTileChanged(TaskSwitcherTileViewModel? value) => RebuildPanelItems();

    /// <summary>
    /// Rebuilds <see cref="PanelItems"/>: the window tiles in enumeration order, then the desktop
    /// tile. It is the desktop tile's placement rule (always last) in one place.
    /// </summary>
    private void RebuildPanelItems()
    {
        PanelItems.Clear();
        foreach (var tile in Tiles)
            PanelItems.Add(tile);
        if (DesktopTile is { } desktop)
            PanelItems.Add(desktop);
    }

    /// <summary>
    /// The session's "Show desktop" tile. It is a tile like any other for layout and dwell (the
    /// same cell, the same rectangular hit face, the same dwell time); only the View's template
    /// makes it look different, and only <see cref="TaskSwitcherTileViewModel.IsDesktop"/> tells
    /// them apart. No window fields: a zero handle and empty strings.
    /// </summary>
    private static TaskSwitcherTileViewModel CreateDesktopTile() =>
        new(IntPtr.Zero, string.Empty, 0, string.Empty, new PhysicalPoint(0, 0), isDesktop: true);

    /// <summary>
    /// S3-A: the desktop tile takes a visible grid slot (the last one), so it can cost the last
    /// WINDOW its tile. Called by the View after it has measured the grid's capacity, before the
    /// final layout: window tiles beyond <paramref name="capacity"/> - 1 are dropped from the grid
    /// and counted by the overflow chip, and the desktop tile stays drawn. Without a desktop tile
    /// this is a no-op — the pre-S3-A viewport clipping keeps its behaviour.
    /// </summary>
    /// <param name="capacity">Full tile cells the grid can show (columns x rows).</param>
    public void ApplyTileCapacity(int capacity)
    {
        if (!HasDesktopTile)
            return;

        // Remove from the END of the window list: the desktop tile is appended after it, so it
        // keeps the last visible slot and the windows that lose one are the same tail the chip
        // already described.
        while (Tiles.Count > 0 && Tiles.Count + 1 > capacity)
            Tiles.RemoveAt(Tiles.Count - 1);
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
        // The chip and the empty message describe THIS session's enumeration; a leftover count from
        // the previous show would claim windows that are not there (the VM is a DI singleton).
        OverflowCount = 0;
        IsEmpty = false;
        // …and so does the desktop tile: it is rebuilt from this session's enumeration below, and
        // it must not survive into a session with nothing to minimise.
        DesktopTile = null;

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

        // S3-A: the "Show desktop" set is a snapshot of THIS enumeration — and ONLY of it. The
        // current window is part of it when the enumeration returned it (S2-A leaves that window
        // out of the TILES, not out of "show desktop"); a foreground the enumeration filtered out
        // (a title-less window, an owned dialog such as "Save as", an odd shell) is never added:
        // the action only touches windows the owner was shown or knowingly left out. EyeCtrl's own
        // process is skipped defensively too — the enumeration already excludes the overlays
        // (WS_EX_TOOLWINDOW), but minimising any EyeCtrl surface would take away the owner's only
        // control, so it must be impossible here as well.
        foreach (var window in _windowManager.EnumerateOpenWindows())
        {
            if (window.ProcessId != OwnProcessId)
                _sessionMinimizeTargets.Add(window.Hwnd);

            if (currentWindow is { } current && window.Hwnd == current)
            {
                currentExcluded = true;
                continue;
            }

            Tiles.Add(new TaskSwitcherTileViewModel(
                window.Hwnd, window.Title, window.ProcessId, window.ProcessName,
                new PhysicalPoint(0, 0), window.IsMinimized));
        }

        _sessionTileCount = Tiles.Count;
        _sessionCurrentExcluded = currentExcluded;
        // S3-A: the desktop tile appears after the window tiles whenever the session has anything
        // to minimise — a window tile, or the left-out current window (S2-A: it has no tile, but
        // minimising it is exactly what "show desktop" is for). With neither, S2-B's "Keine
        // offenen Fenster" stands alone: a tile that minimises nothing would be a lie.
        if (Tiles.Count > 0 || currentExcluded)
            DesktopTile = CreateDesktopTile();

        // The empty grid's message. Read here, at the open, from the list just built: a session
        // whose only window is the foreground one (S2-A) has zero window tiles but DOES have the
        // desktop tile, so it shows that rather than a message claiming nothing is open.
        IsEmpty = PanelItems.Count == 0;

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
        DesktopTile = null;
        _landingGuard.Disarm();
        // A progress bar left over from the hidden session must not greet the next show (the
        // VM is a DI singleton — the next show reuses this instance, AppBootstrapper pattern).
        CloseDwellProgress = 0;
        CloseDwellPaused = false;
        OverflowCount = 0;
        IsEmpty = false;

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
    /// Records the View's thumbnail registration result for THIS session's open line (S4-C):
    /// <paramref name="registered"/> live previews, <paramref name="failed"/> tiles that fell back
    /// to their icon box, and the <paramref name="firstFailureHresult"/> of the first failing
    /// <c>dwmapi</c> call (0 = no HRESULT-bearing failure). Called from the same post-layout pass
    /// as <see cref="UpdateCloseButtonPosition"/> — which flushes the open line — so the counts
    /// describe the frames the owner is about to see. The installed build is the case the numbers
    /// exist for: UIAccess at High integrity in the z-band the trial never covered, so the first
    /// day log shows whether previews work there (<c>previewFailures=0</c> is the success shape)
    /// and, if not, whether DWM refused with E_ACCESSDENIED, E_INVALIDARG or composition.
    /// </summary>
    public void ReportPreviews(int registered, int failed, int firstFailureHresult = 0)
    {
        _sessionPreviewCount = Math.Max(0, registered);
        _sessionPreviewFailures = Math.Max(0, failed);
        _sessionPreviewHresult = firstFailureHresult;
    }

    /// <summary>
    /// The one record of the first failing HRESULT as a log token: <c>0xXXXXXXXX</c> or
    /// <c>none</c>. Numbers only — never a title, a process or window content. The digits are a
    /// machine constant (not a UI number), so they use the invariant culture explicitly instead of
    /// an interpolated <c>{…:X8}</c> hole.
    /// </summary>
    private string PreviewHresultToken()
    {
        if (_sessionPreviewHresult == 0)
            return "none";

        return "0x" + _sessionPreviewHresult.ToString("X8", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Called by the View after layout to register the close disc as a dwell target.
    /// Mirrors <see cref="QuickActionsGridViewModel.UpdateCloseButtonPosition"/> — same
    /// physical-pixel convention and the same <see cref="DwellSettings.ToolbarButtonDwellMs"/>.
    /// <para>
    /// It is also the LAST step of the View's post-layout registration pass
    /// (<c>TaskSwitcherWindow.UpdateTilePositions</c> registers every visible tile face and its
    /// thumbnail, then this), which is where the open line's <c>reachable</c> count is final — so
    /// this flushes it. A session that hides before any layout flushes the open line from the
    /// close path instead (T3). The overflow chip's count is final at the same moment, for the
    /// same reason, and is read from the same <see cref="_registeredTiles"/> list.
    /// </para>
    /// </summary>
    public void UpdateCloseButtonPosition(PhysicalPoint screenCenter)
    {
        DwellDetector.AddTarget(new DwellTarget(
            CloseTargetId, screenCenter,
            DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius),
            _settings.Dwell.ToolbarButtonDwellMs));

        WriteOpenLineIfPending();
        // Windows only: the desktop tile is not an enumerated window, so counting it as
        // "reachable" would hide a window that genuinely lost its slot and shrink the chip by one.
        OverflowCount = Math.Max(0, _sessionTileCount - _registeredTiles.Count(t => !t.IsDesktop));
    }

    /// <summary>
    /// Called by the View after layout for the desktop tile's real on-screen rectangle — the same
    /// drawn-face rule as <see cref="UpdateTilePosition"/>: what is drawn is what the detector
    /// hit-tests, and a fully clipped tile gets no target at all. The target id is
    /// <see cref="DesktopTargetId"/>, not a handle, because this tile is not a window.
    /// </summary>
    public void UpdateDesktopTilePosition(PhysicalRect screenRect)
    {
        if (DesktopTile is not { } desktop)
            return;

        desktop.Position = screenRect.Center;
        RegisterFace(DesktopTargetId, screenRect, isDesktop: true);
        DwellDetector.AddTarget(new DwellTarget(
            DesktopTargetId, screenRect, EffectiveTileDwellMs));
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

        // S3-A: the desktop tile. Paused it is a dismiss like every other tile — "show desktop"
        // is OS state change, which must not leave EyeCtrl while paused. Unpaused it sends the
        // session's window set to the App, which minimises them off the gaze path (the message is
        // queued on the UI dispatcher; this VM never calls IWindowManager.Minimize itself).
        if (targetId == DesktopTargetId)
        {
            // Both outcomes carry the desktop tile's slot: the log must tell a paused look at THIS
            // tile apart from a paused window-tile look and from a no-slot dismissal (F2). The
            // desktop tile is the LAST panel entry (RebuildPanelItems' placement rule), so its
            // display slot is the window count — the same convention as a window tile's index.
            _sessionPickedTargetId = DesktopTargetId;
            _sessionSlot = Tiles.Count;

            if (LastSampleWasPaused)
            {
                _sessionOutcome = SessionOutcome.PausedDismiss;
            }
            else
            {
                _sessionOutcome = SessionOutcome.Desktop;
                _messenger.Send(new TaskSwitcherShowDesktopMessage(_sessionMinimizeTargets.ToArray()));
            }

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
        if (targetId == DesktopTargetId)
        {
            if (DesktopTile is { } desktop)
                desktop.DwellProgress = progress;
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
        if (targetId == DesktopTargetId)
        {
            if (DesktopTile is { } desktop)
                desktop.IsDwellPaused = paused;
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
    /// Registers <see cref="EffectiveTileDwellMs"/> (S4-A, default 800 ms): a tile with a
    /// picture invites a longer look than a toolbar button, and it is never quicker than the
    /// toolbar. The ✕ close disc stays on <c>ToolbarButtonDwellMs</c> so the escape keeps its
    /// old, shorter look time.
    /// </summary>
    public void UpdateTilePosition(IntPtr hwnd, PhysicalRect screenRect)
    {
        var tile = Tiles.FirstOrDefault(t => t.Hwnd == hwnd);
        if (tile != null)
        {
            tile.Position = screenRect.Center;
            RegisterFace(hwnd.ToString(), screenRect, isDesktop: false);
        }

        DwellDetector.AddTarget(new DwellTarget(
            hwnd.ToString(),
            screenRect,
            EffectiveTileDwellMs));
    }

    /// <summary>
    /// Records one registered face for the session record: the drawn geometry in display
    /// (registration) order, with its physical top edge. <c>reachable</c> for the open line counts
    /// the window faces only; the picked tile's row/col come from ALL faces — the desktop tile's
    /// slot is logged like any other pick's. A re-registration of the same face replaces its entry.
    /// </summary>
    private void RegisterFace(string targetId, PhysicalRect screenRect, bool isDesktop)
    {
        var existing = _registeredTiles.FindIndex(entry => entry.TargetId == targetId);
        if (existing >= 0)
            _registeredTiles[existing] = (targetId, screenRect.Y, isDesktop);
        else
            _registeredTiles.Add((targetId, screenRect.Y, isDesktop));
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
        // S4-A: the tile dwell this session logs is the one captured at the OPEN — the settings
        // cannot change while the fullscreen switcher is up, and the ladder analysis wants the
        // value the tiles were registered with, not one read at the flush.
        _sessionTileDwellMs = EffectiveTileDwellMs;
        // S4-C: a session's preview counts describe the registration pass of THIS show. They are
        // set by the View just before the open line is flushed; a session that never lays out
        // reports zero of each rather than a previous session's numbers.
        _sessionPreviewCount = 0;
        _sessionPreviewFailures = 0;
        _sessionPreviewHresult = 0;
        _sessionCurrentExcluded = false;
        _registeredTiles.Clear();
        _eligibleAt.Clear();
        _sessionMinimizeTargets.Clear();
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
                $"Task switcher opened: tiles={_sessionTileCount} currentExcluded={(_sessionCurrentExcluded ? "true" : "false")} reachable={_registeredTiles.Count(t => !t.IsDesktop)} anchorValid={(_anchorValid ? "true" : "false")} sinceLastCloseMs={sinceLastClose} lastOutcome={_lastOutcomeId} tileDwellMs={_sessionTileDwellMs} previews={_sessionPreviewCount} previewFailures={_sessionPreviewFailures} previewHr={PreviewHresultToken()}");
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
            case SessionOutcome.Desktop: return "Desktop";
            default: return "Other";
        }
    }

    /// <summary>
    /// The picked tile's grid position, read from the registered faces: rows are runs of equal
    /// top edges (within <see cref="RowTolerancePhysical"/>), columns are the ordinal inside the
    /// run. The desktop tile is one of those faces — its slot is logged like any pick's. Returns
    /// (-1, -1) when no tile was picked (or it was never registered).
    /// </summary>
    private static (int Row, int Col) TileGridPosition(
        IReadOnlyList<(string TargetId, double Top, bool IsDesktop)> registered, string? pickedTargetId)
    {
        if (pickedTargetId is null)
            return (-1, -1);

        var row = -1;
        var col = 0;
        var rowTop = double.NaN;
        foreach (var (targetId, top, _) in registered)
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
