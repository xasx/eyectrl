namespace EyeCtrl.App.Services;

using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.EyeCare;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging;

public class NoteManager : INoteManager, IRecipient<GazeCursorMovedMessage>,
    IRecipient<EyeCareExerciseActiveMessage>, IRecipient<CalibrationStateChangedMessage>,
    IRecipient<DisplayGeometryChangedMessage>, IDisposable
{
    private readonly List<NoteWindow> _windows = new();
    private readonly List<NoteData> _notes = new();
    private readonly DwellDetector _dwellDetector;
    private readonly IMessenger _messenger;
    private readonly IDwellDetectorRegistry? _dwellRegistry;
    // A4 (sitting 10): one Information line per FIRED note control — the control, the note's
    // number, the gaze's distance from the control's centre in whole DIP and the dwell the target
    // required. Content-free by contract: no note text, no word, no position (only a distance).
    // Optional so the headless seams and tests that never read a log stay argument-free; the
    // App's DI passes the real logger.
    private readonly ILogger<NoteManager>? _logger;
    // Never null (T2 fixup 01 M2, the plan's D2): an absent argument IS the production tester, so
    // deleting the wiring cannot silently switch the own-window conjunct off. Headless tests pass
    // the named <see cref="InactiveNoteHitTester"/> sentinel instead.
    private readonly INoteHitTester _hitTester;
    private PhysicalPoint _lastGazePoint;
    private bool _disposed;
    private Func<bool>? _suspendPredicate;
    // True once a sample was dropped by the gate (pause or the suspend predicate, P2 D1). The
    // first sample after the gate opens resets the detector and arm-latches (see Receive).
    private bool _gateClosed;
    // The pause poll (P2 D1 row 2): read per delivered sample alongside the sample's IsPaused flag.
    private Func<bool>? _pausePoll;
    // The exercise flag (P2 D1): kept from EyeCareExerciseActiveMessage, which EyeCareService sends
    // once around the whole run + summary. The exercise window is click-through and full-screen, so
    // WindowFromPoint reports the note beneath it — only this flag stops hidden controls firing.
    // volatile: the sender's break flow may not be the UI thread; the read is per sample (n1).
    private volatile bool _exerciseActive;
    // The calibration flag (P2 D1, review-03 B-2): kept from CalibrationStateChangedMessage. The
    // calibration window turns click-through at runtime when Collecting starts, and the owner
    // deliberately fixates edges and corners where notes are clamped. volatile: same reasoning.
    private volatile bool _calibrating;
    // The occluder sources (P2 D3), evaluated per sample — no cache to invalidate. Null sources
    // contribute nothing: the lag mark before its bridge is attached, the banner slot while empty.
    private Func<IReadOnlyList<PhysicalRect>>? _lagMarkRects;
    private Func<PhysicalRect?>? _slotOccupantBounds;
    private Func<PhysicalRect?>? _trackStatusBounds;
    private Func<PhysicalRect?>? _noteNoticeBounds;
    // B2b: the status strip's label card (paused / tracking lost / no tracker), its own named source.
    private Func<PhysicalRect?>? _statusStripBounds;
    // C10 fixup-04: the blocked-input banner. Its emergency buttons are dwell targets at the top of
    // the screen, and a note's own dwell (600 ms, below the banner's 700 ms) keeps running while
    // UIPI discards injected clicks - so a click-through banner would let a note's ✕ beneath it fire
    // first. The whole visible window occludes, and the source answers null once it is hidden.
    private Func<PhysicalRect?>? _blockedBannerBounds;
    // Gate conjunct 4's own-window half (P2 D2): target id → the HWND of the note window that
    // draws it, recorded at registration. Windowless targets (the test seams) are absent here and
    // exempt from the conjunct; the conjunct is inactive only while the tester is the named
    // InactiveNoteHitTester sentinel — the visible choice headless tests make instead of an
    // absent argument (T2 fixup 01 M2: the production default is the real tester).
    private readonly Dictionary<string, IntPtr> _targetWindows = new();
    private const int MaxNotes = 8;
    private const int DwellTimeMs = 600;

    /// <summary>
    /// A4 (sitting 10): the one Information line per FIRED note control. Content-free by
    /// contract — the closed control id, the note's number, the gaze's distance from the
    /// control's centre in WHOLE DIP (never a double: Serilog renders a double with the current
    /// culture, and a German comma would break every parser) and the dwell the target required
    /// in ms. NEVER the note's text, a word, a prediction or a screen position: a position plus
    /// the layout would spell the text out, and a distance only says how far off the aim was.
    /// The format lives in a const so the text-literal ratchet records it (category log).
    /// </summary>
    private const string NoteControlFiredLogFormat =
        "note-control fired: control={Control} note={Number} distDip={DistanceDip} dwellMs={DwellMs}";

    /// <summary>The controls A4 logs — the closed set of note dwell targets a card or chip can
    /// fire. The Place-here SHOW and its relocations are M3's lines (note-move-settle); only the
    /// FIRED <c>:place</c> control appears here.</summary>
    private static readonly string[] LoggedControls =
        ["edit", "move", "close", "done", "keyboard", "discard", "otherside", "cancel", "place", "undo"];

    /// <summary>
    /// The note dwell floor (P2 D6): adaptation may lengthen a note's dwell, never shorten it
    /// below 600 ms — the router clamps every shortening map to this floor, and the two routing
    /// registration tests pin the floor's presence.
    /// </summary>
    public const int NoteMinDwellMs = 600;

    // ── the single editor (P1 D8) ──
    private NoteData? _editingNote;
    // Whether the EDITING note owns the eye keyboard (P1 D5, fixup M1): claimed ONLY by an entry
    // that summons the keyboard (it was hidden). A hand-over keeps the previous editor's claim
    // (the release returns early and never clears it); a fresh entry onto a keyboard the owner
    // opened claims nothing, so Done cannot hide a keyboard this note never opened.
    private bool _editingOwnsKeyboard;
    // The probe's IsVisible at the last Changed raise, so the hide rule reads a transition.
    private bool _keyboardWasVisible;
    private IKeyboardProbe? _keyboardProbe;
    // Leave-before-arm (fixups B2/F1/F2; T2 generalises the latch, P2 D4): targets registered
    // while the gaze sits inside their hit circle — Done's slot IS ✕'s slot, and a neighbour's
    // ✕ can sit under the same held gaze — stay ineligible until the gaze has been CONTINUOUSLY
    // outside for LeaveBeforeArmMs. Maps target id → the timestamp of the first outside sample
    // since the last inside one (null = the gaze is inside right now). Empty in steady state.
    private static readonly int LeaveBeforeArmMs = 150;
    private readonly Dictionary<string, long?> _targetsAwaitingGazeLeave = new();

    // ── PendingUndo: the chip, its detector and its clock (P2 D5) ──
    // The chip's countdown is 8 s of REACHABLE time, measured by an injected monotonic clock that
    // an injected 100 ms UI timer advances — the timer runs only while a chip is pending.
    /// <summary>How long Undo stays possible: 8 s of reachable time, not wall time (P2 D5).</summary>
    internal const int UndoWindowMs = 8000;
    /// <summary>The chip clock's freshness rule: a valid, non-gap sample within the last 500 ms,
    /// stamped with the injected clock's own now (P2 D5's condition 3).</summary>
    internal const int ClockFreshSampleMs = 500;
    // The chip's Undo target lives in its OWN detector (P2 D5): a second DwellDetector at a fixed
    // 600 ms, NOT registered with the adaptation registry, gated exactly like the main one. It
    // stays registered while another note edits — the single-editor rule clears only card targets.
    private readonly DwellDetector _undoDetector;
    // The chip targets' HWNDs, separate from _targetWindows so the full-replacement clears that
    // RegisterSoleEditorTargets/ReRegisterAllTargets run never drop a chip's own-window mapping.
    private readonly Dictionary<string, IntPtr> _undoTargetWindows = new();
    // Per-chip accumulated reachable time, keyed by note id (insertion order is the chip's age).
    private readonly Dictionary<string, double> _chipReachedMs = new();
    // The chip's CAPTURED geometry (T6 fixup-06 G1): the text size it was composed at and the
    // measured column its text is drawn in. A pending Undo is a transient affordance the owner
    // may already be reaching for, and the chip is size-DEPENDENT now (its text reads at the
    // note's own size, so its card grows with it). Re-placing a chip on a later text-size
    // change would move the Undo cell out from under a resting gaze (R12's invariant) and
    // re-wrap a line the owner has already read, so the size is FROZEN with the chip instead.
    // The chip lives 8 s of reachable time; Undo restores the full card at the CURRENT size.
    private readonly Dictionary<string, ChipGeometry> _chipGeometry = new();

    /// <summary>The chip's frozen geometry: the text size it was composed at and the measured
    /// text column its text is drawn in (T6 fixup-06 G1).</summary>
    private readonly record struct ChipGeometry(double TextSizeDip, double ColumnWidthDip);

    /// <summary>
    /// Fixup-01 F1: one revealed note's arming clock. The revealed row stays disarmed until the
    /// gaze has spent <see cref="LeaveBeforeArmMs"/> of REACHABLE time continuously outside the
    /// at-rest card ∪ the reveal target. <see cref="LastSampleUs"/> is null when the previous
    /// sample carried no information (inside, or gated): the next valid sample then credits
    /// nothing for the absent interval.
    /// </summary>
    private sealed class RevealArming
    {
        public double OutsideMs;
        public long? LastSampleUs;
    }

    /// <summary>
    /// Fixup-01 F2 (belt and braces): the fired ✕'s cell, kept with the chip born while the gaze
    /// was inside it. The chip's ↺ stays disarmed until the gaze has been outside that cell for
    /// <see cref="LeaveBeforeArmMs"/> of reachable time. The geometry already keeps ↺ two cells
    /// away; this clock covers a future geometry change.
    /// </summary>
    private sealed class CloseCellArming
    {
        public Rect CellDip;
        public double OutsideMs;
        public long? LastSampleUs;
    }
    // The injected monotonic clock, in milliseconds. Production: Environment.TickCount64.
    private readonly Func<long> _clockMs;
    // The injected 100 ms UI timer; created lazily so tests and non-UI constructions never pay
    // for a DispatcherTimer (the production default is DispatcherNoteUndoTimer).
    private INoteUndoTimer? _undoTimer;
    // The clock's stamp at the previous tick; the tick credits the elapsed delta to reachable chips.
    private long _lastTickClockMs;
    // The clock's stamp of the last valid (non-gap, gate-passing) sample. Null before the first.
    private long? _lastValidSampleClockMs;

    // ── the reveal (D, sitting 11): a Display note is its text until a look reveals its row ──
    /// <summary>How long the gaze must rest on the at-rest card before the row appears (D3). A
    /// constant, NOT an adapted target: a reveal is not an action.</summary>
    internal const int NoteRevealDwellMs = NoteLayout.RevealDwellMs;

    /// <summary>How long the gaze must stay outside the revealed card plus
    /// <see cref="NoteRevealHideMarginDip"/> before the note returns to rest (D5).</summary>
    internal const int NoteRevealHideMs = NoteLayout.RevealHideMs;

    /// <summary>The margin around the revealed card that still counts as "on the note" for the
    /// hide clock (D5), in DIP.</summary>
    internal const double NoteRevealHideMarginDip = NoteLayout.RevealHideMarginDip;

    // The reveal target's own detector (D3): a fixed 500 ms, NOT registered with the adaptation
    // registry (a reveal is not an action and its time must not move), gated by the same
    // per-sample filter as every card target. One target per Display note at rest.
    private readonly DwellDetector _revealDetector;
    // The reveal targets' HWNDs, separate from _targetWindows for the same reason the chip's map
    // is: the full-replacement clears of the card registrations never drop a reveal target.
    private readonly Dictionary<string, IntPtr> _revealTargetWindows = new();
    // The ONE revealed note (D5: at most one) and its hide clock: the reachable time the gaze
    // has spent OUTSIDE the revealed card + margin, and the previous valid sample's timestamp.
    // Time is accumulated from valid samples only (D5: gated/gap samples are no information),
    // capped per interval like the dwell detector's silence clamp so a 3 s gaze loss cannot
    // count as 3 s away.
    private string? _revealedNoteId;
    private double _revealOutsideMs;
    private long? _revealLastSampleUs;
    // When the revealed row appeared, on the injected monotonic ms clock (D8's ms={visible}).
    private long _revealShownAtMs;
    // SEAM-ONLY (AddNoteTargetsForTest): windowless test notes whose row is registered as if
    // revealed. Production has at most one revealed note; the seam predates D and its callers
    // dwell several Display notes' rows in one test.
    private readonly HashSet<string> _seamRevealedNotes = new(StringComparer.Ordinal);
    // Fixup-01 F1: note id → its revealed-row arming clock. Present = every row target of that
    // note is disarmed right now; removed once the gaze has left the at-rest card ∪ reveal
    // target for 150 ms of reachable time. Production has at most one entry.
    private readonly Dictionary<string, RevealArming> _revealArming = new(StringComparer.Ordinal);
    // Fixup-01 F2's belt: note id → the fired ✕ cell its chip's Undo must stay disarmed under.
    private readonly Dictionary<string, CloseCellArming> _undoCloseCellArming = new(StringComparer.Ordinal);

    /// <summary>Whether the note presents its row: the one production reveal, or a seam note.</summary>
    private bool IsPresentedRevealed(string noteId) =>
        _revealedNoteId == noteId || _seamRevealedNotes.Contains(noteId);

    // ── placement and Move (P3) ──
    // Every note's derived placement (P3 D2): the window rect, the card rect and the side are
    // never stored on the note — they are computed by NoteLayout.Place from the note's anchor
    // and side, the card size, the work area and the (Edit) avoid rects, and cached here so the
    // target registrations, the chrome zones and the window all read the SAME answer.
    private readonly Dictionary<string, NotePlacement> _placements = new();
    // The work area in DIP (P3 D2.7), injected: production reads SystemParameters.WorkArea
    // (primary monitor in v1); headless tests inject a fixed 1920x1200.
    private readonly Func<Rect> _workAreaDip;
    // The test override for the provider above (null in production).
    private Func<Rect>? _workAreaDipOverride;
    // The note text-size setting (P4 D1/T8): the live instance the App settings carry, wired
    // once. Its TextSizeDip changes re-place every note (T8); the default instance covers the
    // headless constructions that pass nothing.
    private readonly NoteSettings _noteSettings;
    // The Move state (P3 D4): at most one note moves at a time — StartMove finishes any current
    // move first, and every exit path below clears this id together with the place-here window.
    private string? _movingNoteId;
    // fixup-01 m1 (review): the Other-side candidate RegisterMoveTargetsForNote JUDGED over the
    // T1 avoid set — OtherSideForMoving applies exactly it, never a fresh un-avoided re-derive
    // that could differ from what was offered. Refreshed on every move re-registration; cleared
    // with the move.
    private NotePlacement? _judgedOtherSideForMoving;
    private readonly NoteSettleTracker _settleTracker = new();
    // The accepted settle's centroid (physical px) the :place target will re-anchor to.
    private PhysicalPoint? _placeCentroid;
    // The Move log session (sitting 10 M3): armed at StartMove, the end line reports the move's
    // duration (on the injected ms clock) and what its accepted settles did. The settle lines
    // themselves ride the constructor's sink seam — Serilog's Information in production, a
    // capturing list in the headless tests.
    private long _moveStartedMs;
    private int _moveSettles;
    private int _moveHolds;
    private int _moveMoves;
    private readonly Action<string> _settleInfo;
    // The Place-here window (P3 D4): one per manager, created lazily, never in DI. It is NOT
    // click-through, so the own-window conjunct can resolve it; Dispose closes it.
    private NotePlaceHereWindow? _placeWindow;
    // The Move preview (sitting 9): ONE click-through overlay that draws the settle ring and the
    // dashed landing outline. One per manager, created lazily, never in DI; Dispose closes it.
    private NoteMovePreviewWindow? _movePreviewWindow;
    private bool _movePreviewShown;
    // The preview's card size, memoized on the inputs it derives from (the note, its text and the
    // current text size): the derivation MEASURES the wrapped text, and the preview asks for it on
    // every gaze sample of a settle. The key makes a text-size change re-measure, so the cache can
    // never hold a size the note no longer has.
    private string? _previewCardSizeKey;
    private Size _previewCardSize;
    // The capacity notice (P4 D4): one per manager, created lazily on the first at-capacity
    // pin, NEVER in DI (it is a state flash, not a service). It is click-through and carries
    // no dwell targets; its visible bounds are the note-gate occluder that keeps a gaze
    // resting on the notice from firing a note cell beneath it.
    private NoteNoticeWindow? _noticeWindow;

    private Rect _placeWindowRect;
    private bool _placeShown;

    public IReadOnlyList<NoteData> ActiveNotes => _notes.AsReadOnly();

    /// <summary>P2 D7: true while any note is in the Move state. The activation-offer
    /// coordinator reads this as its "note move" suspension reason — offers suspend for the
    /// whole Move, but (unlike the modal reasons) it does NOT turn ModalSuspended on, because
    /// that flag is the note gate's suspender input and a Move must not suspend the notes
    /// themselves.</summary>
    public bool IsMoving => _movingNoteId is not null;

    /// <summary>P2 D5's redefinition: Display + Edit + Move notes plus NON-yieldable chips. A
    /// <c>DiscardedExisting</c> chip counts as a live note (the saved text still exists behind
    /// it); a <c>Removed</c> or <c>DiscardedNew</c> chip yields its slot and does not count.</summary>
    public bool IsAtCapacity => LiveNoteCount >= MaxNotes;

    /// <summary>Display + Edit + Move notes plus non-yieldable chips (see <see cref="IsAtCapacity"/>).</summary>
    private int LiveNoteCount => _notes.Count(n =>
        n.State != NoteState.PendingUndo || n.UndoKind == PendingUndoKind.DiscardedExisting);

    /// <summary>Whether a chip kind yields its capacity slot (P2 D5's table): a chip whose note
    /// still owns saved text does not.</summary>
    internal static bool YieldsSlot(PendingUndoKind kind) => kind != PendingUndoKind.DiscardedExisting;

    /// <summary>True while some note is in Edit: the keyboard dispatchers route every output
    /// through <see cref="Apply"/> and none of it may become OS input (P1 D1).</summary>
    public bool IsEditing => _editingNote is not null;

    /// <summary>
    /// Diagnostics/testing hook: true when every active note window is currently shown.
    /// Guards against regressions where a note is created but never displayed.
    /// </summary>
    public bool AllWindowsVisible => _windows.All(w => w.IsDisplayed);

    /// <summary>
    /// The live note dwell-target rects, physical px (the D17 inventory's sixth source, review F1):
    /// the surfaces this manager's own detector is actually feeding — a note's Done/Show-keyboard
    /// buttons in Edit state, its close/edit/move buttons in display state, its <c>:place</c>
    /// target while moving. Reading the DETECTOR's snapshot (not re-deriving from note state) means
    /// the inventory can never disagree with what gaze is really evaluated against. Rect targets
    /// report their bounds; circular targets report centre ± hit radius as a covering rect.
    /// Same thread-discipline as the rest of this class (UI thread — the inventory calls from the
    /// UI thread; the snapshot itself is copy-on-write, so even a racing read is consistent).
    /// Returns a fresh list; callers may hold it.
    /// </summary>
    public IReadOnlyList<PhysicalRect> LiveTargetRects()
    {
        // Both card/chip detectors plus the reveal targets (D7): the inventory must also see the
        // at-rest reveal target - it is a note gaze consumer like every cell.
        var targets = _dwellDetector.Targets;
        var undo = _undoDetector.Targets;
        var reveal = _revealDetector.Targets;
        if (targets.Count == 0 && undo.Count == 0 && reveal.Count == 0)
            return Array.Empty<PhysicalRect>();
        var rects = new List<PhysicalRect>(targets.Count + undo.Count + reveal.Count);
        foreach (var target in targets.Concat(undo).Concat(reveal))
        {
            if (target.Bounds is { } bounds)
                rects.Add(bounds);
            else
                rects.Add(new PhysicalRect(
                    target.Center.X - target.Radius,
                    target.Center.Y - target.Radius,
                    target.Radius * 2,
                    target.Radius * 2));
        }
        return rects;
    }

    /// <summary>The production monotonic clock (P2 D5): uptime milliseconds.</summary>
    private static long DefaultClockMs() => Environment.TickCount64;

    /// <summary>Test seam (the note-window show seam, moved down by sync 4): whether the manager
    /// puts REAL note windows on the screen. True in production; the App test assembly switches
    /// it off ONCE in its module initializer, because a headless test runs on the OWNER machine
    /// and a topmost note window is exactly what must not appear there. With it off each window
    /// still builds its drawn tree, applies its placement, registers its targets and counts as
    /// displayed (<see cref="NoteWindow.IsDisplayed"/>) - only the HWND show is skipped.</summary>
    internal static bool RealNoteWindows = true;

    /// <summary>Test seam (the CI work-area fix, #227/#231/#234): the work area a manager uses when
    /// the caller injects none. Null in production, where the constructor reads
    /// <see cref="SystemParameters.WorkArea"/> as before; the App test assembly sets the OWNER's
    /// 1920x1152 DIP once in its module initializer, so placement-dependent tests do not follow
    /// the machine's screen (CI's is smaller and put a note on another side). An explicit
    /// <c>workAreaDip</c> argument always wins. No writer in <c>src/</c>.</summary>
    internal static Func<Rect>? DefaultWorkAreaDipForTests;

    public NoteManager(IMessenger messenger, IStruggleTelemetrySink? sink = null,
        IDwellDetectorRegistry? dwellRegistry = null, INoteHitTester? hitTester = null,
        Func<long>? clockMs = null, INoteUndoTimer? undoTimer = null,
        Func<Rect>? workAreaDip = null, NoteSettings? noteSettings = null,
        Action<string>? settleInfoLog = null,
        ILogger<NoteManager>? logger = null)
    {
        _messenger = messenger;
        _logger = logger;
        _dwellDetector = new DwellDetector(cooldownMs: 300, context: DwellContext.Notes, sink: sink);
        _dwellDetector.TargetActivated += OnDetectorTargetActivated;
        _dwellDetector.TargetProgress += OnTargetProgress;
        // P2 D5: the chip's detector — a fixed 600 ms dwell, no adaptation, same cooldown and
        // context so telemetry and the cue see one Notes surface (the census classifies the FILE).
        _undoDetector = new DwellDetector(cooldownMs: 300, context: DwellContext.Notes, sink: sink);
        _undoDetector.TargetActivated += OnUndoDetectorTargetActivated;
        _undoDetector.TargetProgress += OnTargetProgress;
        // D (sitting 11): the reveal target's own detector. Deliberately NOT registered with the
        // adaptation registry: NoteRevealDwellMs is a constant, and a reveal is not an action.
        // Fixup-01 F4: it also gets NO telemetry sink — reading glances and reveals must never
        // reach the Notes struggle signals the adaptation reads for the row's controls.
        _revealDetector = new DwellDetector(cooldownMs: 300, context: DwellContext.Notes, sink: null);
        _revealDetector.TargetActivated += OnRevealTargetActivated;
        _revealDetector.TargetProgress += OnTargetProgress;
        // P2 D5's injected clock: an absent argument IS the production clock.
        _clockMs = clockMs ?? DefaultClockMs;
        // M3 (sitting 10): the settle/move-end log lines' sink. A pre-formatted line per call —
        // the formats are invariant-culture const strings (see SettleShownLine and friends).
        _settleInfo = settleInfoLog ?? (static line => Serilog.Log.Information(line));
        _undoTimer = undoTimer;
        // P2 D2's production default (fixup 01 M2): null means "the real tester", never "none" —
        // the inactive case is the explicit sentinel, so it is visible at every call site.
        _hitTester = hitTester ?? new NoteHitTester();
        // P3 D2.7: an absent argument IS the production worker area — SystemParameters.WorkArea
        // (the primary monitor's working rect in v1).
        _workAreaDip = workAreaDip ?? DefaultWorkAreaDipForTests ?? (() => SystemParameters.WorkArea);
        // T8 (P3 D3): the text-size setting rides the constructor; a change re-places every
        // note through the same seam T6 uses. Unsubscribed in Dispose.
        _noteSettings = noteSettings ?? new NoteSettings();
        _noteSettings.PropertyChanged += OnNoteTextSizeChanged;
        _messenger.Register<GazeCursorMovedMessage>(this);
        _messenger.Register<EyeCareExerciseActiveMessage>(this);
        _messenger.Register<CalibrationStateChangedMessage>(this);
        // T6 (P3 D3): the geometry watcher's message joins the WPF SystemParameters subscription —
        // the message alone does not fire on a taskbar resize or auto-hide.
        _messenger.Register<DisplayGeometryChangedMessage>(this);
        SystemParameters.StaticPropertyChanged += OnSystemPropertyChanged;
        _dwellRegistry = dwellRegistry;
        // P2 D6: the explicit registration replaces ForDetector — an explicit 600 ms floor and a
        // NAMED no-op geometry applier. The no-op is the point: AdaptGeometry defaults on, with
        // margins up to 100 px, and a margin pushes hit cells outside their own window, breaking
        // P2 D2's layout rule 1. Note targets never take the adaptation geometry margin.
        _dwellRegistry?.Register(this, new DwellRoutingRegistration(
            DwellContext.Notes,
            MinDwellMs: NoteMinDwellMs,
            MaxDwellMs: AdaptationDwellRanges.StandardMaxDwellMs,
            Apply: _dwellDetector.SetEffectiveDwellTime,
            ApplyGeometry: NoteGeometryOptOut));
    }

    /// <summary>
    /// The named no-op geometry applier (P2 D6). Deliberately does nothing: note hit cells are
    /// fixed, because a margin can push a hit area outside its own window and off its alpha &gt; 0
    /// pixels — both layout rules the window gate depends on. It exists as a named method (not a
    /// lambda) so a stack trace or registration dump names the choice; the routing registry
    /// requires a non-null applier.
    /// </summary>
    private static void NoteGeometryOptOut(Func<double, double>? _) { }

    // ── placement derivation (P3 D2) ──

    /// <summary>The work area rect in DIP, the input every Place call here reads: the
    /// constructor's provider (SystemParameters by default) unless a test override was
    /// installed through <see cref="UseWorkAreaForTest"/>.</summary>
    private Rect WorkAreaDip() => _workAreaDipOverride?.Invoke() ?? _workAreaDip();

    /// <summary>The full card size for <paramref name="note">: the card width is fixed by
    /// the layout for the current text-size setting; the height derives from the note's
    /// actual text (the edit draft while editing) clamped by this work area's height.</summary>
    private Size CardSizeFor(NoteData note) => CardSizeFor(note, _noteSettings.TextSizeDip);

    /// <summary>The full card size for <paramref name="note"/> at an EXPLICIT text size
    /// (T6 fixup-06 G1): the chip's captured size, which the current setting may no longer be.
    /// <para>
    /// K2a: an EDITING note uses the compact Edit card (the text above, one row of three controls
    /// below). D (sitting 11): every card presentation is placed at the RESERVED (revealed) size —
    /// the four-cell row's footprint — and the at-rest card is its top part. Every placement, the
    /// drawn window, the dwell registrations and the chrome rects read this one answer.
    /// </para></summary>
    private Size CardSizeFor(NoteData note, double textSize)
    {
        double workHeight = WorkAreaDip().Height;
        string text = note.State == NoteState.Edit ? note.Draft : note.Text;
        var window = Window(note.Id);
        double textHeight = window?.MeasureTextHeightDip(text, textSize) ?? 0;
        return note.State == NoteState.Edit
            ? NoteLayout.EditCardSize(textSize, textHeight, workHeight)
            : DisplaySizesFor(window, text, textSize, workHeight).Reserved;
    }

    /// <summary>The Display card's two sizes at the current text size (D1/D2): the at-rest top
    /// part and the RESERVED (revealed) footprint every placement uses. The text height is the
    /// same capped measure the old card height used; the width is the MEASURED wrapped width, so
    /// a short note's card is short. The window measures with the same face the manager does.</summary>
    private (Size AtRest, Size Reserved) DisplaySizesFor(NoteData note, double textSize) =>
        DisplaySizesFor(Window(note.Id), note.State == NoteState.Edit ? note.Draft : note.Text,
            textSize, WorkAreaDip().Height);

    private static (Size AtRest, Size Reserved) DisplaySizesFor(NoteWindow? window, string text,
        double textSize, double workHeight)
    {
        double cap = NoteLayout.TextCapHeightDip(workHeight, textSize);
        double textHeight = Math.Min(window?.MeasureTextHeightDip(text, textSize) ?? 0, cap);
        double textWidth = window?.MeasureTextWidthDip(text, textSize) ?? 0;
        return (
            NoteLayout.DisplayRestSize(textWidth, textHeight),
            NoteLayout.DisplayReservedSize(textWidth, textHeight));
    }

    /// <summary>The at-rest Display card's size (D1) for a note — the manager's reveal-target and
    /// chrome math, measured with the same face and cap the placement used.</summary>
    private Size RestCardSizeFor(NoteData note) =>
        DisplaySizesFor(note, _noteSettings.TextSizeDip).AtRest;

    /// <summary>
    /// The chip's geometry at a text size (T6 fixup-06 G1): the size itself and the MEASURED
    /// column that holds every chip text in at most two lines.
    /// </summary>
    private static ChipGeometry ChipGeometryAt(double textSizeDip)
    {
        var texts = ChipTextCandidates();
        return new ChipGeometry(textSizeDip, NoteLayout.ChipTextWidthDip(textSizeDip, texts));
    }

    /// <summary>The chip's captured geometry, or the current one when the note is not a chip.</summary>
    private ChipGeometry ChipGeometryFor(NoteData note) =>
        _chipGeometry.TryGetValue(note.Id, out var geometry)
            ? geometry
            : ChipGeometryAt(_noteSettings.TextSizeDip);

    /// <summary>The chip placement (P4 D3, the owner default "the note window shrunk in place"):
    /// the chip card TRANSLATED so its top-left equals the RESERVED Display card's top-left — the
    /// text corner (D6, sitting 11). The chip is where the text was; its Undo disc sits at the
    /// chip's own corner, never where ✕ was in the row (the latch rule covers any near miss).
    /// The smaller rect at the same top-left stays inside the work area whenever the reserved one
    /// did.
    ///
    /// <para>
    /// The chip carries NO tail (T5b review-02 F1): the translation moves the chip window off
    /// the anchor by exactly the width and height the card lost, so a translated tip would point
    /// 155-338 DIP away from the anchor, at empty space — and re-deriving a bent tip toward it
    /// costs more than a transient Undo affordance justifies. The chip is where the card was;
    /// its anchor relation is not needed, and Undo of Removed restores the full card with its
    /// real anchor tail.
    /// </para></summary>
    private NotePlacement ChipPlacementFor(NoteData note)
    {
        // T6 fixup-06 G1: the chip's size is its CAPTURED one (the text size it was composed
        // at), so a later text-size change cannot resize the card under a resting gaze, and
        // the full card it is measured against is measured at that same size.
        var geometry = ChipGeometryFor(note);
        var full = PlaceFor(note, avoidKeyboard: false, textSizeDip: geometry.TextSizeDip);
        var chip = NoteLayout.Place(note.Anchor, full.Side,
            NoteLayout.ChipCardSize(geometry.TextSizeDip, ChipTextCandidates()), WorkAreaDip(),
            new List<Rect>(), DpiHelper.ScaleX)!;
        // D6: anchor the chip's card at the reserved card's top-left (the text corner).
        double dx = full.Card.Left - chip.Card.Left;
        double dy = full.Card.Top - chip.Card.Top;
        return new NotePlacement(
            chip.Side,
            new Rect(chip.Window.X + dx, chip.Window.Y + dy, chip.Window.Width, chip.Window.Height),
            new Rect(chip.Card.X + dx, chip.Card.Y + dy, chip.Card.Width, chip.Card.Height),
            TailTip: null);
    }




    // DIP-in-PhysicalPoint conversions: the Core's point type is axis-free; the SETTLE TRACKER
    // works in DIP (its DIP semantics ride the PhysicalPoint wrapper), while window rects and
    // the place-here math read SystemParameters' work area in DIP and the screen in physical px.
    private static PhysicalPoint ToDipPoint(PhysicalPoint p) =>
        new(DpiHelper.PhysicalToLogicalX(p.X), DpiHelper.PhysicalToLogicalY(p.Y));

    private static PhysicalPoint ToPhysicalPoint(PhysicalPoint dipPoint) =>
        new(DpiHelper.LogicalToPhysicalX(dipPoint.X), DpiHelper.LogicalToPhysicalY(dipPoint.Y));

    private static Rect PhysicalRectToDip(PhysicalRect r) => new(
        DpiHelper.PhysicalToLogicalX(r.X),
        DpiHelper.PhysicalToLogicalY(r.Y),
        DpiHelper.PhysicalToLogicalX(r.X + r.Width) - DpiHelper.PhysicalToLogicalX(r.X),
        DpiHelper.PhysicalToLogicalY(r.Y + r.Height) - DpiHelper.PhysicalToLogicalY(r.Y));

    private static PhysicalRect DipRectToPhysical(Rect r) => new(
        DpiHelper.LogicalToPhysicalX(r.Left),
        DpiHelper.LogicalToPhysicalY(r.Top),
        DpiHelper.LogicalToPhysicalX(r.Width),
        DpiHelper.LogicalToPhysicalY(r.Height));

    /// <summary>The avoid rects in DIP for one placement derivation: in Edit, the keyboard window
    /// and its edge bar through the probe (D2.5); otherwise empty.</summary>
    private List<Rect> AvoidRectsDip(bool avoidKeyboard)
    {
        if (!avoidKeyboard || _keyboardProbe is not { } probe)
            return new List<Rect>();
        return probe.Rects().Select(PhysicalRectToDip).ToList();
    }

    /// <summary>Derives the placement for a note (P3 D2): the pure <see cref="NoteLayout.Place"/>
    /// over the note's anchor and side, the card size, the injected work area and — only in the
    /// Edit state — the keyboard's avoid rects. Tests derive their expected geometry through the
    /// same closure via the stored placement (see <see cref="PlacementForTest"/>).</summary>
    private NotePlacement PlaceFor(NoteData note, bool avoidKeyboard) =>
        PlaceFor(note, avoidKeyboard, _noteSettings.TextSizeDip);

    /// <summary>Derives the placement for a note at an EXPLICIT text size (T6 fixup-06 G1: the
    /// chip's captured size).</summary>
    private NotePlacement PlaceFor(NoteData note, bool avoidKeyboard, double textSizeDip)
    {
        var placement = NoteLayout.Place(
            note.Anchor, note.Side, CardSizeFor(note, textSizeDip), WorkAreaDip(), AvoidRectsDip(avoidKeyboard),
            DpiHelper.ScaleX, exclude: null,
            // T6 fixup-06 G2: the displaced candidates are the EDIT state's alone. A note whose
            // anchor lies under the keyboard steps clear of it instead of landing on it; Display,
            // Move and the chips keep the placement they always had.
            allowDisplaced: avoidKeyboard,
            // T6 fixup-07 F2: the Edit rules are measured on the window that is DRAWN - the one
            // the number tag grows upward - so the tag never comes within a key's reach of the
            // keyboard, let alone overlaps it. Zero everywhere else, so those placements stay
            // byte-identical.
            topGrowthDip: avoidKeyboard ? NoteLayout.TagProtrusionDip(textSizeDip) : 0.0);
        return placement ?? throw new InvalidOperationException(
            "NoteLayout.Place returned a null placement without exclude — the clamped fallback guarantees a placement");
    }

    /// <summary>Applies a note's derived placement to its window and caches it: resize, redraw
    /// (the column follows the side, the tail appears) and position the window from the same
    /// rect, so drawing and the dwell registrations read one answer. Identical derivations are
    /// skipped: a trigger's re-place of an unmoved card must not rebuild the drawn tree (held
    /// drawn-tree references stay valid, review-01's drawn⌁registered invariant holds).</summary>
    private void ApplyPlacementToWindow(NoteData note)
    {
        NotePlacement placement;
        if (note.State == NoteState.PendingUndo)
        {
            placement = NoteLayout.WithTagClearance(ChipPlacementFor(note), _noteSettings.TextSizeDip);
        }
        else if (note.State == NoteState.Move)
        {
            // D6 (coordinator's option C): the moving card GROWS DOWNWARD by the Move hint's
            // block so the instruction is never clipped. The landing outline stays the reserved
            // rect (PreviewPlacementFor derives from CardSizeFor), so it shows what will land.
            placement = NoteLayout.WithTagClearance(
                MovePlacementFor(note, PlaceFor(note, avoidKeyboard: false)),
                _noteSettings.TextSizeDip);
        }
        else
        {
            placement = NoteLayout.WithTagClearance(
                PlaceFor(note, avoidKeyboard: note.State == NoteState.Edit), _noteSettings.TextSizeDip);
        }

        // Fixup-01 F3: a Display note's window must contain its whole reveal target, at rest
        // and revealed alike, so the enlarged band belongs to this window and its alpha-1 fill
        // is hit-testable. The CARD never moves; only the window surface grows.
        if (note.State == NoteState.Display)
            placement = NoteLayout.WithRevealTargetClearance(placement, RestCardSizeFor(note));

        if (_placements.TryGetValue(note.Id, out var current) && current == placement)
            return;
        _placements[note.Id] = placement;
        if (Window(note.Id) is not { } window)
            return;
        window.ApplyPlacement(placement);
        window.Left = placement.Window.Left;
        window.Top = placement.Window.Top;

        // T5 review-01 M3: a chip whose placement genuinely MOVED (a T6 clamp, a T1/T8 shift)
        // re-registers its Undo target at the NEW drawn centre - the ghost at the old spot must
        // die with the old geometry. AddTarget is an idempotent set, the window mapping is
        // refreshed, the detector resets, and the standard leave-before-arm rule latches the
        // target if the gaze sits on the new centre.
        if (note.State == NoteState.PendingUndo)
        {
            RegisterUndoTarget(note.Id);
            _undoDetector.ResetAllState();
            LatchTargetsUnderTheGaze();
        }
    }

    /// <summary>
    /// The DRAWN Move placement (D6, option C): the reserved card plus the Move hint's block
    /// between the body text and the ⇄/⊘ row. Only the drawn card/window grow; the reserved
    /// placement stays the basis for the landing outline. If the grown card would leave the
    /// inset work area it is shifted UP just enough to stay inside — the one case where the text
    /// moves during a Move; it never feeds placement, collisions or keyboard avoidance, because
    /// the note is already placed and does not re-place while moving.
    /// </summary>
    private NotePlacement MovePlacementFor(NoteData note, NotePlacement reserved)
    {
        if (Window(note.Id) is not { } window)
            return reserved; // windowless seam: nothing draws the hint, nothing grows
        double hint = window.MoveHintBlockHeightDip(reserved.Card.Width);
        if (hint <= 0)
            return reserved;

        var card = new Rect(reserved.Card.Left, reserved.Card.Top,
            reserved.Card.Width, reserved.Card.Height + hint);
        var inset = InsetWorkArea(WorkAreaDip());
        double dy = 0;
        if (card.Bottom > inset.Bottom)
            dy = inset.Bottom - card.Bottom;
        if (card.Top + dy < inset.Top)
            dy = inset.Top - card.Top; // taller than the inset: keep its top in, the bottom overflows
        if (dy != 0)
            card = new Rect(card.Left, card.Top + dy, card.Width, card.Height);

        return reserved with
        {
            Window = new Rect(reserved.Window.X, reserved.Window.Y + dy,
                reserved.Window.Width, reserved.Window.Height + hint),
            Card = card,
        };
    }

    /// <summary>The inset work area a placement must stay inside (P3 D2's edge margin).</summary>
    private static Rect InsetWorkArea(Rect workArea)
    {
        double margin = NoteLayout.Constants.EdgeMarginDip;
        return new Rect(
            workArea.Left + margin,
            workArea.Top + margin,
            Math.Max(0, workArea.Width - (2 * margin)),
            Math.Max(0, workArea.Height - (2 * margin)));
    }

    /// <summary>
    /// T6 (P3 D3): the display geometry or the work area changed — EVERY note re-derives its
    /// placement from the fresh work area (chips included: a chip's rect is a derived
    /// placement), the windows re-apply it in place and the registration is rebuilt so every
    /// dwell centre and the Move/Other side availability re-derive from the placements (<see
    /// cref="RegisterMoveTargetsForNote"/> reads <see cref="NoteLayout.Place"/> per call). The
    /// sole-editor rule is honoured by <see cref="ReRegisterAllTargets"/> itself.
    /// </summary>
    private void RePlaceEveryNote()
    {
        if (_disposed) return;
        foreach (var note in _notes.ToList())
            ApplyPlacementToWindow(note);
        ReRegisterAllTargets();
        RaiseTargetsChanged();
    }


    /// <summary>T8 (P3 D3): the note text-size setting changed. Every window re-renders at the
    /// new size first, then every note re-derives with it (the cards resize) and the
    /// registration is rebuilt, so every cell centre follows. Chips derive from the
    /// size-independent chip card, so a chip shrunk in place does not move.
    /// <para>
    /// T6 fixup-06 G1: the chip is NOT size-independent any more - its text reads at the note's
    /// own size and its card grows with it. A pending chip keeps the size it was COMPOSED at
    /// (see <see cref="_chipGeometry"/>): re-placing it here would move the Undo cell out from
    /// under a resting gaze (R12) and re-wrap a line the owner has already read, and the chip
    /// lives 8 s at most. Undo restores the full card at the current size.
    /// </para></summary>
    private void OnNoteTextSizeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.PropertyName is not (nameof(NoteSettings.TextSizeDip) or "")) return;
        foreach (var window in _windows.ToList())
            window.SetTextSize(_noteSettings.TextSizeDip);
        // R12: a chip keeps its captured size - it is not re-placed here (T6 fixup-06 G1).
        foreach (var note in _notes.Where(n => n.State != NoteState.PendingUndo).ToList())
            ApplyPlacementToWindow(note);
        // The Place-here window reads at the note's size too (T6 fixup-06 G1).
        RefreshPlaceWindow();
        ReRegisterAllTargets();
        RaiseTargetsChanged();
    }

    /// <summary>The next side in the fixed cycle (P3 D4's Other side walks one step).</summary>
    private static NoteSide NextSide(NoteSide side) => side switch
    {
        NoteSide.Right => NoteSide.Left,
        NoteSide.Left => NoteSide.Below,
        NoteSide.Below => NoteSide.Above,
        _ => NoteSide.Right,
    };

    /// <summary>Test/seam helper: the derived placement of a note, or null when the id is unknown.
    /// Headless geometry tests read the SAME placement the manager registered from instead of
    /// re-deriving it, so the accepted P4 layout constants cannot make a test's expectation and
    /// the manager's truth disagree.</summary>
    internal NotePlacement? PlacementForTest(string noteId) =>
        _placements.TryGetValue(noteId, out var p) ? p : null;


    /// <summary>Test seam (review-01 M3): redirects the work-area provider so a test can drive
    /// a T6 re-place that genuinely moves a note.</summary>
    internal void UseWorkAreaForTest(Func<Rect> provider)
    {
        _workAreaDipOverride = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    // ── typing into the note (P1 D1/D2) ──

    /// <summary>
    /// Applies one mapped edit to the editing note's draft and re-renders its card ONCE (a
    /// prediction is one <see cref="NotePrediction"/>, applied atomically). The caret is always
    /// at the end in v1, so every edit touches the tail of the draft. Defensive early return:
    /// the dispatchers only route while <see cref="IsEditing"/>, but Apply is public surface.
    /// </summary>
        public void Apply(NoteEdit edit)
    {
        if (_editingNote is not { } note)
            return;

        switch (edit)
        {
            case NoteAppend append:
                note.Draft += append.Text;
                break;
            case NoteNewline:
                note.Draft += "\n";
                break;
            case NoteBackspace:
                note.Draft = NoteText.DeleteLastTextElement(note.Draft);
                break;
            case NoteDeleteWord:
                note.Draft = NoteText.TrimLastWord(note.Draft);
                break;
            case NotePrediction prediction:
                note.Draft = NoteText.DeleteUnitsSnapped(note.Draft, prediction.DeleteUnits) + prediction.Text;
                break;
            default:
                return;
        }

        Window(note.Id)?.SetText(note.Draft);

        // T7 (P3 D3, P4 D2): a height change re-derives the card (Place re-runs with the
        // freshly measured height; identical placements are skipped) and re-registers so the
        // cells follow - exactly once per ACTUAL change, never per keystroke. The text-size
        // source (T8) arrives through the settings subscription instead.
        var before = _placements.TryGetValue(note.Id, out var previous) ? previous : null;
        ApplyPlacementToWindow(note);
        if (before is { } was && _placements[note.Id] != was)
        {
            if (_editingNote?.Id == note.Id)
            {
                RegisterSoleEditorTargets(note.Id);
                _dwellDetector.ResetAllState();
            }
            else
            {
                ReRegisterAllTargets();
            }
            RaiseTargetsChanged();
        }
    }


    // ── entering and leaving Edit (P1 D4/D8) ──

    public void CreateNoteAt(PhysicalPoint point)
    {
        // Commit-first BEFORE the capacity evaluation (P2 D5): the commit can free a slot (an
        // empty new note disappears) or turn a live note into a yieldable Removed chip (an
        // emptied existing note) — capacity is judged on the world AFTER the commit.
        ExitEditing(commit: true, handOver: true);

        if (IsAtCapacity)
            return; // at capacity the action stays silent until T5's notice window (P4 D4)

        if (_windows.Count >= MaxNotes)
        {
            // Not at capacity but 8 windows: the surplus is a yieldable chip. Finalize the
            // OLDEST one (as on its expiry — its Undo ends early, an accepted cost) so the
            // window total can never exceed 8.
            FinalizeOldestYieldableChip();
        }

        // D5/D6: this pin really enters Edit, so any revealed note returns to rest (why=edit).
        // (An at-capacity pin creates no editor, so it leaves the reveal alone.)
        ClearRevealedState("edit");

        var data = new NoteData { Anchor = point, Side = NoteSide.Right, State = NoteState.Edit, PreviousText = null, Draft = string.Empty, Number = LowestFreeNumber() };
        var window = new NoteWindow(data.Id);
        window.ControlInputActivated +=
            (suffix, kind, fromStylus) => RunControlInput(data.Id, suffix, kind, fromStylus); // T6 P5: caretaker touch/click
        // T6 fixup-02 m1: the window's mouse press must be made while paused, and only the
        // manager owns the pause poll — forwarded for the press half of the pending gate. The
        // lambda re-reads `_pausePoll` at call time, so a later SetPausePoll still applies.
        window.PausePollForInput = () => _pausePoll?.Invoke() ?? false;
        _notes.Add(data);
        _windows.Add(window);
        // K1: the FIRST placement is the Edit state's, so it must avoid the keyboard when the
        // keyboard is already up at creation. A new note can be created while the keyboard is
        // visible (the previous editor's commit-first hand-over keeps it up); BeginEditing below
        // then takes the "already visible" branch and requests nothing, so no probe raise follows
        // and this would be the ONLY placement the card ever gets. With an empty avoid list the
        // card stays under the keyboard until an unrelated keyboard move re-derives it.
        _placements[data.Id] = NoteLayout.WithTagClearance(
            PlaceFor(data, avoidKeyboard: true), _noteSettings.TextSizeDip);

        // Set the initial state before Show() so the first render builds the editor layout. The
        // placement the window renders first is the one its position comes from (P3 D2): apply
        // the placement before Show, and Show() must run here, not inside the window's Loaded
        // handler — Loaded only fires once the window is shown, so a Show() registered inside
        // Loaded would never execute. The ⌨ control's drawn visibility is seeded before Show so
        // the first render matches the registered targets.
        window.SetTextSize(_noteSettings.TextSizeDip);
        window.SetState(NoteState.Edit);
        window.ApplyPlacement(_placements[data.Id]);
        window.Left = _placements[data.Id].Window.Left;
        window.Top = _placements[data.Id].Window.Top;
        window.SetKeyboardControlVisible(KeyboardHidden);
        window.ShowForDisplay(RealNoteWindows);
        window.SetText(data.Draft);

        RegisterSoleEditorTargets(data.Id);
        _dwellDetector.ResetAllState();
        RaiseTargetsChanged();
        BeginEditing(data);
    }

    public void RemoveNote(string id)
    {
        // D6: a removed revealed note leaves the revealed state like ✕ (why=close).
        if (_revealedNoteId == id) ClearRevealedState("close");
        _seamRevealedNotes.Remove(id);
        _revealArming.Remove(id);
        _undoCloseCellArming.Remove(id);
        var note = _notes.FirstOrDefault(n => n.Id == id);
        if (note is { State: NoteState.PendingUndo })
        {
            // A chip has no card targets; a programmatic removal consumes it and deletes outright.
            ConsumeChip(note);
        }
        if (_movingNoteId == id && note is not null)
        {
            // P3 D4: a programmatic removal of a moving note exits its Move cleanly first.
            LeaveMove(note);
        }
        if (_editingNote?.Id == id)
        {
            // No P1 target reaches this (Edit registers :done/:keyboard, not :close); defensive
            // clean-up so a programmatic removal cannot strand the editing state or the keyboard.
            _editingNote = null;
            ReleaseKeyboard(handOver: false);
            _messenger.Send(new NoteEditingChangedMessage(false));
        }

        var window = _windows.FirstOrDefault(w => w.NoteId == id);
        if (window != null) { window.Close(); _windows.Remove(window); }
        if (note != null) _notes.Remove(note);
        ReRegisterAllTargets();
        RaiseTargetsChanged();
    }

    public void StartEdit(string id)
    {
        if (_editingNote?.Id == id)
            return; // already the single editor (P1 D8): a second ✎ cannot exist, and re-entry
                    // must not "commit" a note onto itself
        var note = _notes.FirstOrDefault(n => n.Id == id);
        var window = _windows.FirstOrDefault(w => w.NoteId == id);
        if (note == null || window == null) return;
        if (note.State == NoteState.PendingUndo)
        {
            // M1 (T3 review-01): a chip's only control is Undo — an unrelated programmatic call
            // must not finalize it, and carrying on with a note the expiry just deleted created
            // a ghost editor (probe R8: IsEditing with 0 notes, the keyboard captured). No-op:
            // the chip stays pending, untouched.
            return;
        }
        // Commit-first (P1 D8), then hand the keyboard over: if it is up it stays up and its
        // ownership moves; if not, BeginEditing summons it.
        // D5/D6: entering Edit leaves the revealed state (why=edit) and ends at rest. The
        // sole-editor registration clears the reveal detector right after.
        ClearRevealedState("edit");
        ExitEditing(commit: true, handOver: true);

        note.PreviousText = note.Text;
        note.Draft = note.Text;
        BeginEditOn(note);
    }

    /// <summary>
    /// The shared body of EVERY entry into Edit (P1 D8): the state flip, the window's re-render,
    /// the sole editor's target registration (latched under the held gaze), the targets-changed
    /// raise and the one <see cref="NoteEditingChangedMessage"/> the entry sends. Callers set up
    /// <see cref="NoteData.PreviousText"/>/<see cref="NoteData.Draft"/> first — a ✎ entry copies
    /// the saved text, an Undo-of-Discarded entry keeps the chip's draft.
    /// </summary>
    private void BeginEditOn(NoteData note)
    {
        // D5/D6: an editor takes the screen; any revealed note returns to rest (why=edit). The
        // sole-editor registration below then clears the reveal detector itself.
        ClearRevealedState("edit");
        note.State = NoteState.Edit;
        // T1-T5's state-driven avoid (P3 D3): entering Edit re-derives against the keyboard's
        // rects — Place keeps the saved side when it already clears them and moves only when it
        // intersects, so the restore after Done is the same derivation with the avoid gone.
        ApplyPlacementToWindow(note);
        var window = Window(note.Id);
        if (window != null)
        {
            window.SetState(NoteState.Edit);
            window.SetKeyboardControlVisible(KeyboardHidden);
            window.SetText(note.Draft);
        }
        RegisterSoleEditorTargets(note.Id);
        _dwellDetector.ResetAllState();
        RaiseTargetsChanged();
        BeginEditing(note);
    }

    public void FinishEdit(string id, bool commit)
    {
        if (_editingNote is not { } note || note.Id != id) return;
        ExitEditing(commit, handOver: false);
    }

    /// <summary>
    /// Enters Edit on <paramref name="note"/> (P1 D5/D8): the manager's editor state, the keyboard
    /// summon when hidden (a visible keyboard is left exactly as it is — its ownership simply
    /// moves), and the ONE <see cref="NoteEditingChangedMessage(true)"/> every entry sends.
    /// </summary>
    private void BeginEditing(NoteData note)
    {
        _editingNote = note;
        bool visible = _keyboardProbe?.IsVisible ?? false;
        // Mutant B (review 01): this seed is near-equivalent — SetKeyboardProbe seeds the field
        // and every visibility flip raises Changed, so it only matters for a flip landing between
        // two raises. It is kept because it makes the first transition read exact for free.
        _keyboardWasVisible = visible;
        if (!visible)
        {
            // Fixup M1 (execution deviations): the claim is taken ONLY when this entry summons
            // the keyboard. On a hand-over ReleaseKeyboard(handOver: true) left the previous
            // editor's claim in place and it moves here untouched; on a fresh entry onto the
            // owner's keyboard nobody claims it — Done must not hide what the note did not open.
            _messenger.Send(new KeyboardShowRequestedMessage());
            _editingOwnsKeyboard = true;
        }
        _messenger.Send(new NoteEditingChangedMessage(true));
    }

    /// <summary>
    /// The shared exit from Edit (P1 D4/D8, fixup M2; final P2 D5 rows). <paramref name="commit"/>
    /// (✓ Done) runs the Done table: a draft with text saves; an emptied EXISTING note becomes a
    /// <c>PendingUndo(Removed)</c> chip whose Undo restores the saved text; a brand-new note
    /// whose draft is empty was never saved and disappears. <paramref name="commit"/>: false
    /// (↩ Discard) runs P2 D5's final Discard rows: a new note with text goes through a
    /// <c>DiscardedNew</c> chip; a CHANGED draft with text goes through a <c>DiscardedExisting</c>
    /// chip; an unchanged or emptied existing note goes straight back to Display (the saved text
    /// stays); a new empty note disappears. EVERY exit — the disappearing new note included —
    /// falls through to the shared tail: release the keyboard, then, unless this is a hand-over,
    /// send the one <see cref="NoteEditingChangedMessage(false)"/>. Skipping it anywhere leaves
    /// "→ Note" lit on a keyboard that is typing into the OS. <paramref name="handOver"/> is the
    /// commit-first case: no message and no release — ownership moves to the next editor inside
    /// its BeginEditing, so the switch produces exactly one (true) and no hide/show bounce
    /// (P1 D6/D8). A chip entry releases the keyboard exactly like a Display exit does — the chip
    /// is not an editor — and the exit's re-registration latches every target under the held gaze.
    /// </summary>
    private void ExitEditing(bool commit, bool handOver)
    {
        if (_editingNote is not { } note)
            return;
        _editingNote = null;

        var trimmed = NoteText.TrimBlankEdgeLines(note.Draft);

        if (commit)
        {
            if (trimmed.Length == 0 && note.PreviousText is null)
            {
                // New note, draft empty (P1 D4 Done table): nothing was ever saved — it disappears.
                // RemoveNoteInternal re-registers every survivor, whose tail latches every target
                // under the held gaze (P2 D4) — the "empty-new exit" latch the T2 tests pin.
                RemoveNoteInternal(note);
                ReleaseKeyboard(handOver);
                if (!handOver)
                    _messenger.Send(new NoteEditingChangedMessage(false));
                return;
            }

            if (trimmed.Length == 0)
            {
                // Done on an EMPTIED EXISTING note (P2 D5): PendingUndo(Removed) — Undo restores
                // the saved text, expiry removes it. This replaces P1's interim silent restore.
                EnterPendingUndo(note, PendingUndoKind.Removed);
                ReleaseKeyboard(handOver);
                if (!handOver)
                    _messenger.Send(new NoteEditingChangedMessage(false));
                return;
            }

            note.Text = trimmed;
        }
        else
        {
            // ↩ Discard (P2 D5's final rows, replacing P1's interim restore).
            if (note.PreviousText is null)
            {
                if (trimmed.Length == 0)
                {
                    // A new note whose draft is empty: nothing was ever saved — it disappears.
                    RemoveNoteInternal(note);
                    ReleaseKeyboard(handOver);
                    if (!handOver)
                        _messenger.Send(new NoteEditingChangedMessage(false));
                    return;
                }

                // Review-01's owner decision: Discard of a new note's draft with text goes
                // through Undo — a single look must not destroy typed work.
                note.Draft = trimmed;
                EnterPendingUndo(note, PendingUndoKind.DiscardedNew);
                ReleaseKeyboard(handOver);
                if (!handOver)
                    _messenger.Send(new NoteEditingChangedMessage(false));
                return;
            }

            if (trimmed.Length == 0)
                note.Text = note.PreviousText!; // emptied: straight back; the saved text stays
            else if (trimmed != note.PreviousText)
            {
                // A CHANGED draft with text → PendingUndo(DiscardedExisting): Undo → Edit with
                // the draft, expiry → Display with the previous text, and the chip does NOT
                // yield its capacity slot (P2 D5 — the saved text still exists behind it).
                note.Draft = trimmed;
                EnterPendingUndo(note, PendingUndoKind.DiscardedExisting);
                ReleaseKeyboard(handOver);
                if (!handOver)
                    _messenger.Send(new NoteEditingChangedMessage(false));
                return;
            }
            // else: unchanged → straight back to Display (Text already holds PreviousText).
        }

        note.Draft = note.Text;
        note.State = NoteState.Display;
        // T1-T5's shared restore (P3 D3): straight back to Display re-derives without the avoid —
        // the card returns to its saved side exactly as the chip paths did in EnterPendingUndo.
        ApplyPlacementToWindow(note);
        var window = Window(note.Id);
        if (window != null)
        {
            window.SetState(NoteState.Display);
            window.SetText(note.Text);
        }
        ReleaseKeyboard(handOver);
        ReRegisterAllTargets();
        RaiseTargetsChanged();
        if (!handOver)
            _messenger.Send(new NoteEditingChangedMessage(false));
    }

    /// <summary>
    /// Leave-before-arm (P2 D4, generalising T1's Edit-exit latch): EVERY registered target that
    /// contains the last gaze sample is latched. A latched target stays ineligible (clearing any
    /// accumulated dwell) until its release rule fires: the gaze continuously outside its circle
    /// for <see cref="LeaveBeforeArmMs"/> — one jitter sample past the edge is noise, not a leave
    /// (fixup F1). Called after EVERY (re)registration (state flips, Undo/restore paths, re-layout,
    /// probe flips, gate recovery) and before that from the Edit exits, so a target that appears or
    /// moves under a held gaze is never live until the gaze has demonstrably left it once.
    /// <para>
    /// T4 note: the P3 :place target is NOT exempt and never needed to be. The old
    /// gaze-chasing :place re-centred on every sample (a latch could never release); P3's place
    /// target sits on the Place-here window, which appears beside a judged settle — a fixed
    /// target that can legitimately be born under (or near) a held gaze, so it latches like any
    /// other registration.
    /// </para>
    /// Called AFTER the registration it follows, so the scan sees the fresh targets; a held sample
    /// is by definition still inside the circles it must leave.
    /// <para>
    /// Also the C10 fixup-04 hide-latch seam (public through <see cref="INoteManager"/>): the
    /// blocked-input banner leaves the screen while the gaze still rests where its emergency
    /// buttons were, so the note targets under that spot must wait for a 150 ms leave too.
    /// </para>
    /// </summary>
    public void LatchTargetsUnderTheGaze()
    {
        if (_disposed) return;
        // All three detectors: a chip's Undo target and an at-rest note's reveal target born
        // under a held gaze wait for a leave too (P2 D4 covers every (re)registration).
        foreach (var target in _dwellDetector.Targets.Concat(_undoDetector.Targets).Concat(_revealDetector.Targets))
        {
            if (TargetContains(target, _lastGazePoint))
                _targetsAwaitingGazeLeave.TryAdd(target.Id, null);
        }
    }

    /// <summary>Advances the leave timers (fixup F1). A target whose circle contains the sample
    /// resets its timer; otherwise the timer runs from the first outside sample and the target
    /// re-arms at <see cref="LeaveBeforeArmMs"/> of continuous leave. A target whose dwell
    /// registration vanished (note removed, state flip) is dropped from the latch too. GATED
    /// samples are "no information" — the caller skips them entirely, so they neither advance
    /// nor reset a timer (fixup F4's conjunct keeps the gate inside the latch).</summary>
    private void AdvanceLeaveTimers(PhysicalPoint sample, long timestamp)
    {
        foreach (var id in _targetsAwaitingGazeLeave.Keys.ToList())
        {
            var target = FindTarget(id);
            if (target is null)
            {
                _targetsAwaitingGazeLeave.Remove(id);
                continue;
            }
            if (TargetContains(target, sample))
            {
                _targetsAwaitingGazeLeave[id] = null; // back inside: the leave clock restarts
                continue;
            }
            // Outside: run the clock from the first outside sample (µs → ms).
            var since = _targetsAwaitingGazeLeave[id] ??= timestamp;
            if (timestamp - since >= LeaveBeforeArmMs * 1000)
                _targetsAwaitingGazeLeave.Remove(id);
        }
    }

    // ── fixup-01 F1/F2: the revealed row's arming clock and the chip's close-cell belt ──

    /// <summary>
    /// Whether <paramref name="targetId"/> is one of the freshly revealed note's row cells
    /// (✎/↕/✕) and its arming clock is still pending. The reveal target itself is never gated
    /// by this (it is the thing that reveals), and other notes' rows are untouched.
    /// </summary>
    private static bool RowDisarmedByReveal(string targetId, Dictionary<string, RevealArming> arming)
    {
        if (arming.Count == 0) return false;
        int colon = targetId.LastIndexOf(':');
        if (colon < 0) return false;
        if (targetId[(colon + 1)..] is not ("edit" or "move" or "close")) return false;
        return arming.ContainsKey(targetId[..colon]);
    }

    /// <summary>Whether the chip's Undo target is still disarmed because it was born under the
    /// fired ✕'s cell (fixup-01 F2's belt).</summary>
    private static bool UndoDisarmedByCloseCell(string targetId, Dictionary<string, CloseCellArming> arming)
    {
        if (arming.Count == 0) return false;
        if (!targetId.EndsWith(":undo", StringComparison.Ordinal)) return false;
        return arming.ContainsKey(targetId[..^5]);
    }

    /// <summary>
    /// Advances the revealed row's arming clock (fixup-01 F1). The region is the at-rest card ∪
    /// its reveal target (the target contains the card, but both are tested so a future geometry
    /// change cannot silently shrink the protected area). Inside resets the clock; outside
    /// accumulates reachable valid-sample time (an interval longer than 150 ms is no information
    /// and starts the clock over), and the row re-arms at <see cref="LeaveBeforeArmMs"/>. A note
    /// that stopped being Display (or lost its placement) is dropped.
    /// </summary>
    private void AdvanceRevealArming(PhysicalPoint sample, long timestamp)
    {
        if (_revealArming.Count == 0) return;
        double x = DpiHelper.PhysicalToLogicalX(sample.X);
        double y = DpiHelper.PhysicalToLogicalY(sample.Y);
        var point = new Point(x, y);
        List<string>? armed = null;
        foreach (var id in _revealArming.Keys.ToList())
        {
            var note = FindNote(id);
            if (note is not { State: NoteState.Display }
                || !_placements.TryGetValue(id, out var placement))
            {
                (armed ??= new()).Add(id);
                continue;
            }
            var rest = NoteLayout.RestCardRect(placement, RestCardSizeFor(note));
            var target = NoteLayout.RevealTargetRect(rest);
            var state = _revealArming[id];
            if (rest.Contains(point) || target.Contains(point))
            {
                state.OutsideMs = 0;
                state.LastSampleUs = timestamp;
                continue;
            }
            // Credit only a genuine valid-sample interval. A gap longer than the 150 ms
            // threshold is no information (a gap sample never reaches OnGazeMoved), and the
            // clock starts over at this sample instead of crediting the absence.
            if (state.LastSampleUs is { } last && timestamp > last
                && timestamp - last <= 150_000L)
            {
                state.OutsideMs += (timestamp - last) / 1000.0;
            }
            state.LastSampleUs = timestamp;
            if (state.OutsideMs >= LeaveBeforeArmMs)
                (armed ??= new()).Add(id);
        }
        if (armed is not null)
        {
            foreach (var id in armed)
                _revealArming.Remove(id);
        }
    }

    /// <summary>A gated sample is no information for the reveal arming clock: neither advance
    /// nor reset it, and credit nothing for the interval it covers.</summary>
    private void PauseRevealArming()
    {
        foreach (var state in _revealArming.Values)
            state.LastSampleUs = null;
    }

    /// <summary>
    /// Advances the chip's close-cell belt (fixup-01 F2). A gaze still inside the fired ✕'s cell
    /// keeps the ↺ disarmed; outside accumulates reachable time and releases it after 150 ms.
    /// </summary>
    private void AdvanceCloseCellArming(PhysicalPoint sample, long timestamp)
    {
        if (_undoCloseCellArming.Count == 0) return;
        var point = new Point(
            DpiHelper.PhysicalToLogicalX(sample.X),
            DpiHelper.PhysicalToLogicalY(sample.Y));
        List<string>? armed = null;
        foreach (var id in _undoCloseCellArming.Keys.ToList())
        {
            var state = _undoCloseCellArming[id];
            if (state.CellDip.Contains(point))
            {
                state.OutsideMs = 0;
                state.LastSampleUs = timestamp;
                continue;
            }
            if (state.LastSampleUs is { } last && timestamp > last
                && timestamp - last <= 150_000L)
            {
                state.OutsideMs += (timestamp - last) / 1000.0;
            }
            state.LastSampleUs = timestamp;
            if (state.OutsideMs >= LeaveBeforeArmMs)
                (armed ??= new()).Add(id);
        }
        if (armed is not null)
        {
            foreach (var id in armed)
                _undoCloseCellArming.Remove(id);
        }
    }

    private void PauseCloseCellArming()
    {
        foreach (var state in _undoCloseCellArming.Values)
            state.LastSampleUs = null;
    }

    /// <summary>The Display row's <paramref name="slot"/> cell in screen DIP, read before a
    /// state flip (fixup-01 F2's belt). Empty when the note has no placement.</summary>
    private Rect DisplayCellRectDip(string noteId, int slot)
    {
        if (!_placements.TryGetValue(noteId, out var placement))
            return Rect.Empty;
        var (lx, ly) = NoteLayout.ButtonCenter(placement, slot, NoteState.Display);
        double half = LayoutConstants.NoteCellDip / 2;
        return new Rect(
            placement.Window.Left + lx - half,
            placement.Window.Top + ly - half,
            LayoutConstants.NoteCellDip,
            LayoutConstants.NoteCellDip);
    }

    private static bool InsideDipRect(Rect rect, PhysicalPoint physical) =>
        rect.Contains(new Point(
            DpiHelper.PhysicalToLogicalX(physical.X),
            DpiHelper.PhysicalToLogicalY(physical.Y)));

    /// <summary>
    /// The keyboard release half of an exit (P1 D5, fixup m1): only a keyboard the EDITING note
    /// summoned is hidden, and only when the keyboard is still up. On a hand-over the release
    /// never runs — the ownership moves to the next editor and no hide or show is sent (P1 D8),
    /// so fixation suppression is never released in between. There is NO own-vs-foreign hide
    /// flag (execution deviations 2026-10-16): the note's own hide always follows the ownership
    /// clear, so the flag was observationally inert — any visible→hidden transition clears the
    /// claim instead.
    /// </summary>
    private void ReleaseKeyboard(bool handOver)
    {
        if (handOver)
            return;
        if (_editingOwnsKeyboard && (_keyboardProbe?.IsVisible ?? false))
            _messenger.Send(new KeyboardHideRequestedMessage());
        _editingOwnsKeyboard = false;
    }

    // ── PendingUndo: the chip, its detector and its clock (P2 D5) ──

    /// <summary>
    /// Enters <see cref="NoteState.PendingUndo"/> (P2 D5): the window re-renders IN PLACE as a
    /// chip, the card's targets are unregistered (a chip note registers nothing in the main
    /// detector — <see cref="RegisterTargetsForState"/> skips it), and a single
    /// <c>{id}:undo</c> target is registered in the second detector at the drawn ↺ disc (slot 0)
    /// — latched immediately if the gaze sits on it (P2 D4: every (re)registration latches), so a
    /// stationary gaze over ✕ → chip → Undo never oscillates. The countdown starts here: the
    /// injected timer runs only while a chip is pending.
    /// </summary>
    private void EnterPendingUndo(NoteData note, PendingUndoKind kind)
    {
        // Fixup-01 F2's belt: when the chip is born from a fired ✕ while the gaze still sits in
        // that ✕'s cell, remember the cell. The ↺ stays disarmed until the gaze has been outside
        // it for 150 ms — the geometry keeps ↺ two cells away already, so this only matters if a
        // future change brings them closer. Captured before the state flip (the cell reads the
        // Display row).
        bool fromRevealedRow = note.State == NoteState.Display && IsPresentedRevealed(note.Id);
        Rect? firedCloseCell = fromRevealedRow ? DisplayCellRectDip(note.Id, slot: 3) : null;

        // D6: ✕ from the revealed row leaves the revealed state (why=close); the chip's own
        // registration below replaces every target. A DIFFERENT note's chip entry does not
        // touch a revealed note (chips do not suspend notes).
        if (_revealedNoteId == note.Id) ClearRevealedState("close");
        if (firedCloseCell is { } closeCell && InsideDipRect(closeCell, _lastGazePoint))
        {
            _undoCloseCellArming[note.Id] = new CloseCellArming { CellDip = closeCell };
        }
        note.State = NoteState.PendingUndo;
        note.UndoKind = kind;
        if (kind == PendingUndoKind.Removed)
            note.Draft = note.Text; // the saved text is Undo's payload; Discarded* keep their draft

        // T6 fixup-06 G1: the chip's size is CAPTURED here, before the first placement, so the
        // chip is composed at the note's current text size and stays that way (see
        // _chipGeometry). A text-size change while the chip is pending leaves it alone.
        var geometry = ChipGeometryAt(_noteSettings.TextSizeDip);
        _chipGeometry[note.Id] = geometry;

        // T1-T5's shared restore (P3 D3): out of Edit, the re-derivation drops the keyboard avoid
        // and the chip returns to its anchor-based rect (the saved side) - every chip kind walks
        // through here, so this one call covers Done-Removed, DiscardedNew and DiscardedExisting.
        ApplyPlacementToWindow(note);

        var window = Window(note.Id);
        if (window != null)
        {
            window.SetState(NoteState.PendingUndo);
            window.SetChipText(ChipText(kind), geometry.TextSizeDip, geometry.ColumnWidthDip);
        }
        ReRegisterAllTargets();
        RegisterUndoTarget(note.Id);
        _undoDetector.ResetAllState();
        _chipReachedMs[note.Id] = 0;
        StartUndoTimerIfNeeded();
        RaiseTargetsChanged();
        LatchTargetsUnderTheGaze();
    }

    /// <summary>The chip's text, kind-dependent, through the localization table (P2 D5's
    /// "Note removed · Undo" / "Changes discarded · Undo" and their German strings). The
    /// no-break space before the middot is applied HERE, in code (T6 fixup-07): the strings
    /// themselves are never edited.</summary>
    private static string ChipText(PendingUndoKind kind) => NoteLayout.NoBreakBeforeDot(
        StringTable.Current.T(
            kind == PendingUndoKind.Removed
                ? "overlays.note.chip.removed"
                : "overlays.note.chip.discarded"));

    /// <summary>
    /// Every chip text the column must hold, in EVERY shipped language (T6 fixup-06 G1): the
    /// measured column is the max over them, so the longest one still fits in two lines and a
    /// language switch can never leave a chip's text outside its column. The German resolves
    /// only where the shipped table carries it and the overlays surface is German-approved;
    /// otherwise it falls back to the English text, which is the text that will be drawn.
    /// <para>
    /// The same no-break space the DRAWN text gets is applied here (T6 fixup-07), so the texts
    /// the column is measured from are the texts that are drawn.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> ChipTextCandidates()
    {
        var table = StringTable.Current;
        var texts = new List<string>(4);
        void Add(string text)
        {
            text = NoteLayout.NoBreakBeforeDot(text);
            if (!texts.Contains(text)) texts.Add(text);
        }

        foreach (var language in new[] { UiLanguage.English, UiLanguage.German })
        {
            Add(table.T(language, "overlays.note.chip.removed"));
            Add(table.T(language, "overlays.note.chip.discarded"));
        }
        return texts;
    }

    /// <summary>Registers the chip's Undo dwell target in <see cref="_undoDetector"/> over the
    /// drawn ↺ disc (slot 0 of the chip render — the same NoteLayout math as every other
    /// button), with its own window mapping so the full-replacement clears of the main
    /// detector's registrations never drop it.</summary>
    private void RegisterUndoTarget(string noteId)
    {
        var note = _notes.FirstOrDefault(n => n.Id == noteId);
        if (note == null) return;
        AddColumnTarget(_undoDetector, _undoTargetWindows, noteId, ":undo", slot: 0);
    }

    /// <summary>Registers one dwell target over the drawn column button #<paramref name="slot"/>
    /// (P3 D2.6: the column follows the side, so the target centre derives from the note's stored
    /// placement via <see cref="DwellTargetFor"/>). The detector and its window map arrive as
    /// arguments because the chip targets live in the second detector with their own mapping.</summary>
    private void AddColumnTarget(DwellDetector detector, Dictionary<string, IntPtr> windowMap,
        string noteId, string suffix, int slot)
    {
        detector.AddTarget(DwellTargetFor(noteId, suffix, slot));
        windowMap[noteId + suffix] = WindowHandleOrZero(noteId);
    }

    /// <summary>The ✕ dwell (P2 D5): a Display note becomes a <c>PendingUndo(Removed)</c> chip.
    /// Only a Display card carries a ✕ target; anything else here is a defensive no-op.</summary>
    private void RemoveForUndo(string noteId)
    {
        var note = _notes.FirstOrDefault(n => n.Id == noteId);
        if (note is null || note.State != NoteState.Display) return;
        EnterPendingUndo(note, PendingUndoKind.Removed);
    }

    /// <summary>The Undo dwell (P2 D5's table), with its post-effect cue exactly like the card
    /// targets' (P2 D9; the census classifies this file as a cue surface).</summary>
    private void OnUndoDetectorTargetActivated(string targetId)
    {
        LogFiredControl(targetId);
        OnUndoActivated(targetId);
        _messenger.Send(new DwellActivationMessage());
    }

    private void OnUndoActivated(string targetId)
    {
        const string suffix = ":undo";
        if (!targetId.EndsWith(suffix, StringComparison.Ordinal)) return;
        var noteId = targetId[..^suffix.Length];
        var note = _notes.FirstOrDefault(n => n.Id == noteId);
        if (note is not { State: NoteState.PendingUndo }) return;
        var kind = note.UndoKind!.Value;
        ConsumeChip(note);

        if (kind == PendingUndoKind.Removed)
        {
            // Undo → Display with the saved text (P2 D5). The chip ends; the card's targets come
            // back (and latch under whatever the gaze now holds, P2 D4).
            note.State = NoteState.Display;
            note.Draft = note.Text;
            // The shrunk chip placement dies with the chip: re-derive the full card (P4 D3).
            ApplyPlacementToWindow(note);
            var window = Window(noteId);
            if (window != null)
            {
                window.SetState(NoteState.Display);
                window.SetText(note.Text);
            }
            ReRegisterAllTargets();
            RaiseTargetsChanged();
            return;
        }

        // Discarded (new or existing): Undo → Edit WITH THE DRAFT, through the P1 D8 entry rule —
        // commit any current editor first, then hand the keyboard over.
        ExitEditing(commit: true, handOver: true);
        BeginEditOn(note);
    }

    /// <summary>Finalizes a chip as on its expiry (P2 D5's table). <c>Removed</c> and
    /// <c>DiscardedNew</c> end gone; a <c>DiscardedExisting</c> chip's note returns to Display
    /// with its previous text. Also the "finalize the oldest yieldable chip" path Pin note uses
    /// at capacity (P2 D5) — an Undo that ends early, an accepted cost.</summary>
    private void ExpireChip(NoteData note)
    {
        var kind = note.UndoKind!.Value;
        ConsumeChip(note);
        if (kind != PendingUndoKind.DiscardedExisting)
        {
            RemoveNoteInternal(note); // gone
            return;
        }

        note.Text = note.PreviousText!;
        note.Draft = note.Text;
        note.State = NoteState.Display;
        // Back to the full card - the chip placement was the shrunk one (P4 D3).
        ApplyPlacementToWindow(note);
        var window = Window(note.Id);
        if (window != null)
        {
            window.SetState(NoteState.Display);
            window.SetText(note.Text);
        }
        ReRegisterAllTargets();
        RaiseTargetsChanged();
    }

    /// <summary>Tears down one chip's bookkeeping: the Undo target, its window mapping, its
    /// latch entry and its elapsed time. The timer stops with the last chip (P2 D5: it runs only
    /// while a chip is pending).</summary>
    private void ConsumeChip(NoteData note)
    {
        _chipReachedMs.Remove(note.Id);
        _chipGeometry.Remove(note.Id);
        _undoCloseCellArming.Remove(note.Id);
        _undoDetector.RemoveTarget($"{note.Id}:undo");
        _undoTargetWindows.Remove($"{note.Id}:undo");
        _targetsAwaitingGazeLeave.Remove($"{note.Id}:undo");
        note.UndoKind = null;
        if (_chipReachedMs.Count == 0)
            _undoTimer?.Stop();
    }

    private void StartUndoTimerIfNeeded()
    {
        if (_chipReachedMs.Count == 0) return;
        _lastTickClockMs = _clockMs();
        _undoTimer ??= new DispatcherNoteUndoTimer();
        _undoTimer.Start(OnUndoTimerTick);
    }

    /// <summary>
    /// The injected timer's callback (P2 D5): one 100 ms step of the chip clock. Conditions 1–2 —
    /// not paused, no suspender — are the same gate the samples obey, read per tick; the pause
    /// POLL is what sees a pause entered from the tray while no samples flow. Condition 3: a
    /// valid, non-gap sample arrived within the last 500 ms, stamped with THIS clock's now (so
    /// looking away, or turning to the caretaker, stops the clock). Condition 4 is per chip: its
    /// Undo target registered, its rect clear of live occluders (a failing source is no
    /// information → unreachable, fail closed), and its centre resolving to its own window — one
    /// <c>RootWindowAt</c> per tick, so a chip covered by another note or a third-party topmost
    /// window does not count down.
    /// </summary>
    private void OnUndoTimerTick()
    {
        if (_disposed || _chipReachedMs.Count == 0)
        {
            _undoTimer?.Stop();
            return;
        }

        var now = _clockMs();
        var elapsedMs = now - _lastTickClockMs;
        _lastTickClockMs = now;
        if (elapsedMs <= 0) return;

        bool paused = _pausePoll?.Invoke() ?? false;
        bool suspended = (_suspendPredicate?.Invoke() ?? false) || _exerciseActive || _calibrating;
        if (paused || suspended)
            return;

        if (_lastValidSampleClockMs is not { } stamp || now - stamp > ClockFreshSampleMs)
            return;

        foreach (var note in _notes.Where(n => n.State == NoteState.PendingUndo).ToList())
        {
            if (!_chipReachedMs.TryGetValue(note.Id, out var reached))
                continue; // consumed by an earlier chip's expiry in this same tick
            if (!ChipIsReachable(note))
                continue;
            reached += elapsedMs;
            _chipReachedMs[note.Id] = reached;
            if (reached >= UndoWindowMs)
                ExpireChip(note);
        }
    }

    /// <summary>P2 D5's clock condition 4 for one chip: the Undo target is registered, its
    /// covering rect intersects no live occluder, and the chip's centre resolves to the chip's
    /// own window. Windowless chips (the test seams) and the sentinel tester are exempt from the
    /// own-window half, mirroring <see cref="WindowOwns"/>.</summary>
    private bool ChipIsReachable(NoteData note)
    {
        var targetId = $"{note.Id}:undo";
        var target = _undoDetector.Targets.FirstOrDefault(t => t.Id == targetId);
        if (target is null)
            return false;

        var rect = target.Bounds ?? new PhysicalRect(
            target.Center.X - target.Radius,
            target.Center.Y - target.Radius,
            target.Radius * 2,
            target.Radius * 2);
        if (AnyOccluderIntersects(rect))
            return false;

        if (!_undoTargetWindows.TryGetValue(targetId, out var own) || own == IntPtr.Zero)
            return true;
        if (_hitTester is InactiveNoteHitTester)
            return true;
        return _hitTester.RootWindowAt(target.Center) == own;
    }

    /// <summary>Whether any live occluder rect intersects <paramref name="rect"/> (the chip
    /// clock's condition 4). FAIL CLOSED, like the sample gate: a throwing source is no
    /// information — the chip is treated as unreachable for this tick, and the failure is logged
    /// once per source per episode (the same episode flags the sample gate uses). The
    /// blocked-input banner (C10 fixup-04) is read here too: a strip over the Undo cell stops the
    /// countdown the same way any other occluder does.</summary>
    private bool AnyOccluderIntersects(PhysicalRect rect)
    {
        bool Intersects(PhysicalRect r) =>
            r.X < rect.X + rect.Width && rect.X < r.X + r.Width &&
            r.Y < rect.Y + rect.Height && rect.Y < r.Y + r.Height;

        if (!TryReadOccluderRects(KnownOccluderSources.KeyboardProbe,
                () => _keyboardProbe?.Rects() ?? (IReadOnlyList<PhysicalRect>)Array.Empty<PhysicalRect>(),
                Intersects, out var hit) || hit)
            return true;

        if (!TryReadOccluderRects(KnownOccluderSources.LagMark,
                () => _lagMarkRects?.Invoke() ?? (IReadOnlyList<PhysicalRect>)Array.Empty<PhysicalRect>(),
                Intersects, out hit) || hit)
            return true;

        if (!TryReadOccluderRects(KnownOccluderSources.BannerSlot,
                () => _slotOccupantBounds?.Invoke() is { } slotOccupant
                    ? new[] { slotOccupant }
                    : (IReadOnlyList<PhysicalRect>)Array.Empty<PhysicalRect>(),
                Intersects, out hit) || hit)
            return true;

        if (!TryReadOccluderRects(KnownOccluderSources.TrackStatus,
                () => _trackStatusBounds?.Invoke() is { } trackStatus
                    ? new[] { trackStatus }
                    : (IReadOnlyList<PhysicalRect>)Array.Empty<PhysicalRect>(),
                Intersects, out hit) || hit)
            return true;

        if (!TryReadOccluderRects(KnownOccluderSources.NoteNotice,
                () => _noteNoticeBounds?.Invoke() is { } notice
                    ? new[] { notice }
                    : (IReadOnlyList<PhysicalRect>)Array.Empty<PhysicalRect>(),
                Intersects, out hit) || hit)
            return true;

        if (!TryReadOccluderRects(KnownOccluderSources.StatusStrip,
                () => _statusStripBounds?.Invoke() is { } strip
                    ? new[] { strip }
                    : (IReadOnlyList<PhysicalRect>)Array.Empty<PhysicalRect>(),
                Intersects, out hit) || hit)
            return true;

        // C10 fixup-04: the whole blocked-input banner intersects the chip's Undo cell - the chip
        // cannot count down while an emergency strip covers the reach it needs.
        if (!TryReadOccluderRects(KnownOccluderSources.BlockedInputBanner,
                () => _blockedBannerBounds?.Invoke() is { } blocked
                    ? new[] { blocked }
                    : (IReadOnlyList<PhysicalRect>)Array.Empty<PhysicalRect>(),
                Intersects, out hit) || hit)
            return true;

        return false;
    }

    /// <summary>Runs one occluder RECT read for the chip clock. A throw is contained: logged once
    /// per source per failure episode (the same mechanism <see cref="TryReadOccluder"/> uses) and
    /// reported as a FAILED source — the chip's countdown stops for the tick.</summary>
    private bool TryReadOccluderRects(string name, Func<IReadOnlyList<PhysicalRect>> read,
        Func<PhysicalRect, bool> intersects, out bool hit)
    {
        hit = false;
        try
        {
            foreach (var r in read())
            {
                if (!intersects(r)) continue;
                hit = true;
                break;
            }
            _occluderFailureLogged.Remove(name); // a healthy read re-arms the once-per-episode log
            return true;
        }
        catch (Exception ex)
        {
            if (_occluderFailureLogged.Add(name))
                Serilog.Log.Error(ex,
                    "The note chip clock's {OccluderSource} occluder source threw; failing closed — "
                    + "the chip's countdown stops until the source recovers", name);
            return false;
        }
    }

    private void RemoveNoteInternal(NoteData note)
    {
        // D6: a removed revealed note leaves the revealed state like ✕ (why=close).
        if (_revealedNoteId == note.Id) ClearRevealedState("close");
        _seamRevealedNotes.Remove(note.Id);
        _revealArming.Remove(note.Id);
        _undoCloseCellArming.Remove(note.Id);
        var window = Window(note.Id);
        if (window != null) { window.Close(); _windows.Remove(window); }
        _notes.Remove(note);
        _placements.Remove(note.Id); // P3 D2: a removed note's derived rect dies with it
        ReRegisterAllTargets();
        RaiseTargetsChanged();
    }

    /// <summary>
    /// The at-capacity pin (P4 D4): instead of staying silent, the manager shows the capacity
    /// notice beside the pin point, placed by the same <see cref="NoteLayout.Place"/> search
    /// the cards use with EVERY note card and chip rect as avoid - and clamped, so the notice
    /// is never off-screen. Click-through and target-free: nothing to dwell, nothing to fire.
    /// </summary>
    public void ShowCapacityNotice(PhysicalPoint pinPoint)
    {
        if (_disposed) return;
        var avoid = new List<Rect>();
        foreach (var note in _notes)
        {
            if (_placements.TryGetValue(note.Id, out var notePlacement))
                avoid.Add(notePlacement.Card);
        }
        var size = new Size(NoteLayout.NoticeCardWidthDip, NoteLayout.NoticeCardHeightDip);
        var noticePlacement = NoteLayout.Place(pinPoint, NoteSide.Right, size, WorkAreaDip(), avoid, DpiHelper.ScaleX)!;
        _noticeWindow ??= new NoteNoticeWindow { ShowRealWindow = NoticeWindowShowsForTest };
        _noticeWindow.ShowAt(noticePlacement.Window, NoticeWindowShowsForTest && RealNoteWindows);
    }

    /// <summary>Test seam: whether the notice puts a REAL window on the screen. Headless tests
    /// on the owner machine set this false BEFORE the first ShowCapacityNotice.</summary>
    internal bool NoticeWindowShowsForTest { get; set; } = true;

    /// <summary>Test seam: the notice window instance, once created (null before the first
    /// at-capacity pin).</summary>
    internal NoteNoticeWindow? NoticeWindowForTest => _noticeWindow;

    /// <summary>Test/wiring seam: the occluder source the composition root registered for
    /// the notice - non-null once <see cref="NoteOccluderWiring.Wire"/> ran, and the thing the
    /// note gate actually reads (so a real-container test can pin the connection).</summary>
    internal Func<PhysicalRect?>? NoticeBoundsForTest => _noteNoticeBounds;

    /// <summary>Test/wiring seam (C10 fixup-04): the occluder source the composition root
    /// registered for the blocked-input banner - non-null once <see cref="NoteOccluderWiring.Wire"/>
    /// ran, so a real-container test pins the connection and the bounds it answers.</summary>
    internal Func<PhysicalRect?>? BlockedBannerBoundsForTest => _blockedBannerBounds;
    /// <summary>The notice visible bounds while it shows (P2 D3) - the source the occluder
    /// wiring passes to <see cref="SetOccluderSources"/> at the composition root.</summary>
    internal bool TryGetNoticeVisibleBounds(out PhysicalRect bounds)
    {
        bounds = default;
        return _noticeWindow is { } window && window.TryGetVisibleBounds(out bounds);
    }

    /// <summary>Finalizes the OLDEST yieldable chip as on its expiry (P2 D5): the first chip in
    /// note order whose kind yields its slot. Called by Pin note when the total is at 8 but the
    /// live count is below it. Defensive no-op when none exists (the invariant says one does).</summary>
    private void FinalizeOldestYieldableChip()
    {
        var chip = _notes.FirstOrDefault(n =>
            n.State == NoteState.PendingUndo && n.UndoKind is { } kind && YieldsSlot(kind));
        if (chip is null) return;
        ExpireChip(chip);
    }

    // ── the keyboard probe (P1 D5) ──

    /// <summary>Wired once from EventCoordinator, like <see cref="SetSuspendPredicate"/>.</summary>
    public void SetKeyboardProbe(IKeyboardProbe probe)
    {
        if (_keyboardProbe is not null)
            _keyboardProbe.Changed -= OnKeyboardProbeChanged;
        _keyboardProbe = probe ?? throw new ArgumentNullException(nameof(probe));
        _keyboardProbe.Changed += OnKeyboardProbeChanged;
        _keyboardWasVisible = _keyboardProbe.IsVisible;
    }

    /// <summary>
    /// The probe's change handler. A visible→hidden transition is a HIDE, ours or not — the claim
    /// clears either way (fixup m1: the note's own hide always runs after the ownership clear, so
    /// no distinction exists to make). The ⌨ Show-keyboard control registers in slot 1 exactly
    /// while the keyboard is hidden, so the raise re-registers the editing targets too.
    /// <para>
    /// K1 fixup-01 F2: when the raise actually MOVES the editing card, the activation chrome zones
    /// are stale — they are re-published only on <c>NoteTargetsChangedMessage</c>, and the probe's
    /// raise never sent one. Offers were then refused over the card's old spot and allowed over
    /// the card's new one. A raise that leaves the placement unchanged stays silent, matching
    /// <see cref="ApplyPlacementToWindow"/>'s identical-placement skip.
    /// </para>
    /// </summary>
    private void OnKeyboardProbeChanged()
    {
        if (_disposed) return; // the probe may raise once more than the manager's lifetime (P1 Risks)
        bool visible = _keyboardProbe?.IsVisible ?? false;
        bool wasVisible = _keyboardWasVisible;
        _keyboardWasVisible = visible;

        if (!visible && wasVisible)
            _editingOwnsKeyboard = false;

        if (_editingNote is { } note)
        {
            // T1-T5 (P3 D3): the probe's five sources collapse at this one raise — re-derive the
            // editing card against the keyboard's CURRENT rects (state-driven avoid) BEFORE the
            // re-registration so the targets read the fresh placement. Place keeps the saved
            // side when it clears the rects and picks the first fitting of the cycle otherwise.
            var before = _placements.TryGetValue(note.Id, out var previous) ? previous : null;
            ApplyPlacementToWindow(note);
            bool moved = _placements.TryGetValue(note.Id, out var applied) && applied != before;
            Window(note.Id)?.SetKeyboardControlVisible(!visible); // mutant A's guard (review 01)
            RegisterSoleEditorTargets(note.Id);
            _dwellDetector.ResetAllState();
            if (moved)
                RaiseTargetsChanged();
        }
        else if (_movingNoteId is not null)
        {
            // T1-T5 while Move (P3 D4): Other side's availability re-derives from
            // Place(..., exclude: current) inside the re-registration, latched as always.
            ReRegisterAllTargets();
        }
    }

    /// <summary>The ⌨ dwell: re-summon the keyboard mid-Edit and take ownership again (P1 D8).</summary>
    private void ShowKeyboardForEditing(string noteId)
    {
        if (_editingNote?.Id != noteId) return;
        _messenger.Send(new KeyboardShowRequestedMessage());
        _editingOwnsKeyboard = true;
    }

    /// <summary>
    /// Enters Move (P3 D4): the card dims to 0.6 in place — it never chases the gaze now — and
    /// the manager registers the move set: Other side (slot 0, only while another side of the
    /// anchor fits) and Cancel (slot 1), both latched under the held gaze. A fresh Move has no
    /// place-here window yet; that appears on the first accepted settle. The commit-first rule
    /// applies BEFORE the chip guard can be evaluated in reverse: any editor is committed, then
    /// the note re-fetched (an emptied new note's committed exit removes it). Programmatic
    /// re-entry while already moving is a no-op.
    /// </summary>
    public void StartMove(string id)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        if (_movingNoteId == id)
            return; // already moving this note
        var window = _windows.FirstOrDefault(w => w.NoteId == id);
        if (note == null || window == null) return;
        if (note.State == NoteState.PendingUndo)
        {
            // M1 (T3 review-01), the StartMove twin: on a chip this must be a no-op. The chip
            // stays pending, untouched, and the guard runs BEFORE ExitEditing so a
            // chip-addressed no-op cannot commit the current editor.
            return;
        }
        // fixup-01 M1: Move is exclusive. A different current Move ends first — the earlier
        // note returns to Display through the shared exit; without this, two notes sit in
        // Move at once and the first one stays dimmed and stuck (its Cancel dwell sinks on
        // `_movingNoteId != A`). A no-op on a chip must stay complete: the guard above
        // already returned, so this line only ends a REAL fresh Move.
        if (_movingNoteId is { } otherId && otherId != id && FindNote(otherId) is { } other)
            LeaveMove(other);
        // The commit-first rule (P1 D8): entering Move must not strand the editing state.
        // After ExitEditing the note may be gone (an emptied new note's committed exit) — re-fetch.
        ExitEditing(commit: true, handOver: true);
        note = _notes.FirstOrDefault(n => n.Id == id);
        if (note == null) return;

        // D5/D6: entering Move leaves the revealed state (why=move). At most one note is
        // revealed, so any revealed note — not just this one — returns to rest here.
        ClearRevealedState("move");
        note.State = NoteState.Move;
        // D6 (option C): the drawn Move card grows downward by the Move hint's block; the row
        // targets and the chrome read this grown placement, so ⇄/⊘ sit where they are drawn.
        ApplyPlacementToWindow(note);
        window.SetState(NoteState.Move); // dims to 0.6, shows Cancel (+ Other side when it fits)
        HidePlaceHere();                 // a fresh Move never inherits a stale place window
        HideMovePreview();               // ...nor a stale settle ring or landing outline
        _settleTracker.Reset();
        _movingNoteId = id;
        _moveStartedMs = _clockMs();     // the Move log session (sitting 10 M3) starts here
        _moveSettles = _moveHolds = _moveMoves = 0;
        _dwellDetector.ClearTargets();
        _targetWindows.Clear();
        // D: Move is exclusive - the other notes' reveal targets freeze with their cards.
        _revealDetector.ClearTargets();
        _revealTargetWindows.Clear();
        RegisterMoveTargetsForNote(note);
        _dwellDetector.ResetAllState();
        LatchTargetsUnderTheGaze();
        RaiseTargetsChanged();
    }

    /// <summary>Registers the moving note's local set: Other side (P3 D4) when the NEXT side of
    /// the cycle fits beside the current placement (the exclusion rule forbids re-offering the
    /// side the note is already on), and Cancel always. NOT a full replacement — the StartMove
    /// caller clears first, and <see cref="RegisterTargetsForState"/>'s Move case relies on that
    /// being the only earlier mutation.</summary>
    private void RegisterMoveTargetsForNote(NoteData note)
    {
        if (!_placements.TryGetValue(note.Id, out var placement))
            return;
        var next = NextSide(placement.Side);
        // P3 D3 row T1: the availability derives over the same avoid-rect set the placement
        // itself used — a keyboard-covered alternative must not offer (the ⊢ window cannot be
        // reached without leaving the gaze inside the keyboard's dwell space). The probe's
        // raise re-derives through the Move re-registration below (OnKeyboardProbeChanged), so
        // Other side re-appears the moment T1 frees the room back.
        var other = NoteLayout.Place(note.Anchor, next, CardSizeFor(note), WorkAreaDip(),
            AvoidRectsDip(avoidKeyboard: true), DpiHelper.ScaleX, exclude: placement.Side,
            // T6 fixup-06 G2: the Move state never displaces - the "Other side" offer must be a
            // side that EXISTS beside the note, and a displaced window is neither (the Edit
            // state is the only one that opts in).
            allowDisplaced: false);
        _judgedOtherSideForMoving = other;
        bool otherFits = other is not null;
        Window(note.Id)?.SetOtherSideAvailable(otherFits);
        if (otherFits)
            AddColumnTarget(_dwellDetector, _targetWindows, note.Id, ":otherside", slot: 0);
        AddColumnTarget(_dwellDetector, _targetWindows, note.Id, ":cancel", slot: 1);
    }

    /// <summary>The shared exit from Move (P3 D4): the place-here window goes away, the tracker
    /// resets, the note returns to Display (or, with <paramref name="registerDisplay"/> false, a
    /// caller that re-registers itself first suppresses the double registration), and the
    /// full re-registrations re-latch everything under the held gaze. Sends the targets-changed
    /// raise so the activation offers re-publish (the Move suspension reason may be ending).
    /// <para>D: the note ends AT REST — the drawn Move card (grown by the hint, option C) shrinks
    /// back to the reserved rect the note was placed at, so the text is where it was before the
    /// Move unless the grown card had to shift up at the work-area edge (that shift is transient
    /// and this re-derivation puts the card back on the placement).</para></summary>
    private void LeaveMove(NoteData note)
    {
        HidePlaceHere(); // must run while _movingNoteId still names this move
        HideMovePreview();
        _movingNoteId = null;
        _judgedOtherSideForMoving = null;
        _settleTracker.Reset();
        note.State = NoteState.Display;
        Window(note.Id)?.SetState(NoteState.Display);
        // D6: shrink the drawn card back to the reserved rect (a no-op when nothing grew).
        ApplyPlacementToWindow(note);
        ReRegisterAllTargets();
        RaiseTargetsChanged();
    }

    /// <summary>The ↘ Cancel dwell (P3 D4): the note returns to Display where it already is —
    /// its anchor and side are unchanged.</summary>
    private void CancelMove(string noteId)
    {
        if (_movingNoteId != noteId) return;
        if (FindNote(noteId) is not { } note) return;
        WriteMoveEndLine("cancelled"); // M3 (sitting 10)
        LeaveMove(note);
    }

    /// <summary>The Other side dwell (P3 D4): one step of the side cycle, applied immediately —
    /// the note re-places on the new side and returns to Display there. Defensive no-ops keep
    /// the method honest when the placement could not be rebuilt.</summary>
    private void OtherSideForMoving(string noteId)
    {
        if (_movingNoteId != noteId) return;
        if (FindNote(noteId) is not { } note) return;
        if (!_placements.TryGetValue(noteId, out var current)) return;
        WriteMoveEndLine("otherside"); // M3 (sitting 10): the move ends here, flip or defensive exit
        // fixup-02 nit 2, review-03 tidy-up: apply the candidate the offer judged available,
        // whatever cycle step the avoid set landed it on. The old re-derive fallback was dead:
        // the Other-side target registers ONLY while _judgedOtherSideForMoving is non-null
        // (RegisterMoveTargetsForNote) and every Move re-registration refreshes it, so the
        // dwell can never reach a null judged value. The defensive exit keeps a race harmless:
        // the note stays exactly where it is.
        var other = _judgedOtherSideForMoving;
        if (other is null)
        {
            LeaveMove(note);
            return;
        }
        note.Side = other.Side;
        var grown = NoteLayout.WithTagClearance(other, _noteSettings.TextSizeDip);
        _placements[noteId] = grown;
        if (Window(noteId) is { } window)
        {
            window.ApplyPlacement(grown);
            window.Left = grown.Window.Left;
            window.Top = grown.Window.Top;
        }
        LeaveMove(note);
    }

    /// <summary>
    /// The Place-here dwell (P3 D4): re-anchors the note at the accepted settle's centroid — NOT
    /// the last gaze point (that could have moved on between the settle and the dwell). The
    /// anchor moves; the side preference stays, and the placement (which may pick another side)
    /// is derived fresh. The window map entry for :place belongs to the place-here window, not
    /// the note card — the cleared own-window mapping is not needed here.
    /// </summary>
    public void FinishMove(string id, PhysicalPoint newAnchor)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        var window = _windows.FirstOrDefault(w => w.NoteId == id);
        if (note == null || window == null) return;
        WriteMoveEndLine("placed"); // M3 (sitting 10): the session is over — before any teardown
        note.Anchor = newAnchor;
        HidePlaceHere(); // must run while _movingNoteId still names this move
        HideMovePreview();
        _movingNoteId = null;
        _judgedOtherSideForMoving = null;
        _settleTracker.Reset();
        note.State = NoteState.Display;
        var placement = NoteLayout.WithTagClearance(NoteLayout.Place(note.Anchor, note.Side, CardSizeFor(note), WorkAreaDip(),
            new List<Rect>(), DpiHelper.ScaleX)!, _noteSettings.TextSizeDip);
        _placements[id] = placement;
        window.ApplyPlacement(placement);
        window.Left = placement.Window.Left;
        window.Top = placement.Window.Top;
        window.SetState(NoteState.Display);
        ReRegisterAllTargets();
        RaiseTargetsChanged();
    }

    /// <summary>
    /// Broadcasts <see cref="NoteTargetsChangedMessage"/> after every structural change to the
    /// detector's target set (create/remove/state flips) so the D17 inventory re-runs the preview
    /// panel's collision check (review F1) and the activation-offer coordinator re-publishes its
    /// chrome zones (P2 D7). NOT called from the gaze-driven <c>:place</c> centre updates — see
    /// the message's doc for why that would be a per-sample walk.
    /// </summary>
    private void RaiseTargetsChanged()
    {
        // T6 P5: every membership or state change funnels through here (create, remove, chip
        // entry, Undo, expiry), so the badges re-derive at the same point the targets do.
        RefreshBadges();
        _messenger.Send(new NoteTargetsChangedMessage());
    }

    /// <inheritdoc />
    public IReadOnlyList<PhysicalRect> ChromeRects()
    {
        var rects = new List<PhysicalRect>(_notes.Count * 4 + 1);
        // T6 fixup-01: the tag's straddled half rides ABOVE the card — outside every other
        // chrome rect — so activation offers and keep-clears must see it. It follows the exact
        // badge-visibility rule (RefreshBadges): shown on card notes only while TWO OR MORE
        // notes are non-chips; a lone card note or a chip carries none.
        bool badged = _notes.Count(n => n.State != NoteState.PendingUndo) >= 2;
        foreach (var note in _notes)
        {
            bool revealed = IsPresentedRevealed(note.Id);
            // D7: the chrome carries the CURRENTLY DRAWN rect — the at-rest top part until a
            // look reveals the row, then the reserved rect; Edit/Move/the chip draw their card.
            if (_placements.TryGetValue(note.Id, out var placement))
            {
                var drawn = note.State is NoteState.Edit or NoteState.Move or NoteState.PendingUndo
                    ? placement.Card
                    : NoteLayout.DrawnCardRect(placement, RestCardSizeFor(note), revealed);
                rects.Add(DipRectToPhysical(drawn));
                if (badged && note.State != NoteState.PendingUndo)
                {
                    var tagLocal = NoteLayout.TagRect(placement, _noteSettings.TextSizeDip);
                    rects.Add(DipRectToPhysical(new Rect(
                        placement.Window.X + tagLocal.X, placement.Window.Y + tagLocal.Y,
                        tagLocal.Width, tagLocal.Height)));
                }
            }

            // The occupied cells only (P4 D3, D1): a Display note at rest draws no cell, and the
            // gap cell is chrome for nobody.
            double half = DpiHelper.LogicalToPhysicalX(LayoutConstants.NoteCellDip / 2);
            foreach (var slot in NoteLayout.DrawnSlotsFor(note.State, revealed))
            {
                if (!TryColumnCentre(note.Id, slot, note.State, out var centre)) continue;
                rects.Add(new PhysicalRect(centre.X - half, centre.Y - half, half * 2, half * 2));
            }
        }
        // D7: an at-rest note's reveal target is a gaze consumer of its own (it can grow above
        // the drawn card), so no activation offer may sit on it either.
        foreach (var target in _revealDetector.Targets)
        {
            if (target.Bounds is { } bounds)
                rects.Add(bounds);
            else
                rects.Add(new PhysicalRect(
                    target.Center.X - target.Radius, target.Center.Y - target.Radius,
                    target.Radius * 2, target.Radius * 2));
        }
        // P3 D4: a shown Place-here window is chrome — the activation offers must not overlap it.
        if (_placeShown)
            rects.Add(DipRectToPhysical(_placeWindowRect));
        return rects;
    }

    /// <summary>The drawn control for <paramref name="slot"/> in <paramref name="state"/>'s
    /// centre in physical px (P3 D2; K2a: Edit's controls sit in their own row), derived from the
    /// note's stored placement. False when the note has no placement (a windowless test seam note
    /// before its first derivation — the test seams compute one at registration).</summary>
    private bool TryColumnCentre(string noteId, int slot, NoteState state, out PhysicalPoint centre)
    {
        centre = default;
        if (!_placements.TryGetValue(noteId, out var placement))
            return false;
        var (lx, ly) = NoteLayout.ButtonCenter(placement, slot, state);
        centre = new PhysicalPoint(
            DpiHelper.LogicalToPhysicalX(placement.Window.Left + lx),
            DpiHelper.LogicalToPhysicalY(placement.Window.Top + ly));
        return true;
    }

    public void OnGazeMoved(PhysicalPoint gazePoint, long timestamp)
    {
        _lastGazePoint = gazePoint;

        // Gate conjunct 4 (P2 D1/D2/D3), its sample-level half. The occluder walk runs whenever
        // live targets exist — NOT only inside the hit-area union. That is conservative, not
        // load-bearing: a gaze outside the union is outside every hit circle (so no latch release
        // could be wrong there — the gaze genuinely left), and an occluded target cannot fire
        // anyway because the filter keeps !gated. The extra reads only ever make a latch release
        // LATER, never earlier — a latch that waits out a gaze parked on the keyboard is the
        // intended cost. Union-gating the reads would be stricter still; per-sample evaluation
        // keeps the code simple and needs no cache. The one RootWindowAt call, on the other hand,
        // is skipped unless the gaze is inside the union of the live hit areas, some target
        // actually has a window, and the conjunct is active (it is off for the InactiveNoteHitTester
        // sentinel): outside every hit area no target can be the nearest, so the call would buy
        // nothing (P2 D2's cost cap). With a FAILED source but no genuine cover, the call still
        // runs — ✓ Done is exempt from the failure answer and must still pass the own-window
        // conjunct honestly (fixup 03 m1).
        bool genuineOcclusion = false;
        bool occluderFailed = false;
        IntPtr rootWindow = IntPtr.Zero;
        if (_dwellDetector.Targets.Count > 0 || _undoDetector.Targets.Count > 0
            || _revealDetector.Targets.Count > 0)
        {
            (genuineOcclusion, occluderFailed) = EvaluateOccluders(gazePoint);
            if (!genuineOcclusion && OwnWindowConjunctActive && AnyWindowedTarget && GazeInsideTargetUnion(gazePoint))
                rootWindow = _hitTester.RootWindowAt(gazePoint);
        }

        // The leave-before-arm latch (fixups B2/F1; P2 D4): advance the leave timers, then keep
        // the still-latched ones ineligible for this sample (the filter also clears whatever dwell
        // they accumulated while latched). A genuinely occluded sample OR a sample from a failing
        // source carries no leave information — we cannot know what was over the spot, so a latch
        // is never released on such evidence (F4, conservative).
        if (_targetsAwaitingGazeLeave.Count > 0 && !genuineOcclusion && !occluderFailed)
            AdvanceLeaveTimers(gazePoint, timestamp);

        // Fixup-01 F1: the freshly revealed row's own arming clock. Gated samples are no
        // information: they neither advance nor reset it (the next valid sample credits
        // nothing for the gated interval).
        if (_revealArming.Count > 0)
        {
            if (genuineOcclusion || occluderFailed)
                PauseRevealArming();
            else
                AdvanceRevealArming(gazePoint, timestamp);
        }

        // Fixup-01 F2's belt: the same clock for a ↺ born under the fired ✕'s cell.
        if (_undoCloseCellArming.Count > 0)
        {
            if (genuineOcclusion || occluderFailed)
                PauseCloseCellArming();
            else
                AdvanceCloseCellArming(gazePoint, timestamp);
        }

        Func<DwellTarget, bool>? filter = null;
        if (genuineOcclusion || occluderFailed || _targetsAwaitingGazeLeave.Count > 0
            || _revealArming.Count > 0 || _undoCloseCellArming.Count > 0 || OwnWindowConjunctActive)
        {
            var disarmed = _targetsAwaitingGazeLeave;
            var revealArming = _revealArming;
            var closeCellArming = _undoCloseCellArming;
            filter = t => GateOpenFor(t, genuineOcclusion, occluderFailed)
                && !disarmed.ContainsKey(t.Id)
                && !RowDisarmedByReveal(t.Id, revealArming)
                && !UndoDisarmedByCloseCell(t.Id, closeCellArming)
                && WindowOwns(t, rootWindow);
        }
        _dwellDetector.Update(gazePoint, timestamp, filter);
        // The chip's Undo target is gated EXACTLY like the card targets (P2 D5): same occluders,
        // same latch, same own-window conjunct — the one filter serves both detectors.
        _undoDetector.Update(gazePoint, timestamp, filter);
        // D: the reveal target is gated EXACTLY like ✎ — the same filter, the same occluders and
        // latch, and a reveal is not an action (its activation sends no cue).
        _revealDetector.Update(gazePoint, timestamp, filter);
        // D5's hide clock: a valid sample outside the revealed card + 24 DIP runs it; gated
        // samples are no information (neither advance nor reset), which is why the flag is
        // passed rather than read inside.
        AdvanceRevealHideClock(gazePoint, timestamp, genuineOcclusion || occluderFailed);

        // P3 D4: a moving note never chases the gaze. Every valid sample feeds the settle
        // tracker (DIP); an accepted settle shows the Place-here window beside the centroid.
        // Sitting 9: the same samples drive the visible feedback — the settle ring and the
        // dashed landing outline — so the move "taking hold" is something the owner sees.
        if (_movingNoteId is { } movingId && FindNote(movingId) is { } moving)
        {
            // The tracker's "DIP" samples ride the PhysicalPoint wrapper (pure Core type system);
            // the caller's conversion decides the meaning.
            _settleTracker.AddSample(ToDipPoint(gazePoint), timestamp);
            if (_settleTracker.Settled && _settleTracker.Centroid is { } centroidDip)
            {
                _settleTracker.Reset();
                OnSettled(moving, centroidDip);
            }
            else
            {
                UpdateMovePreview(moving);
            }
        }
    }

    /// <summary>The moving note's manager lookup.</summary>
    private NoteData? FindNote(string id) => _notes.FirstOrDefault(n => n.Id == id);

    /// <summary>
    /// One accepted settle of the moving note's gaze (P3 D4): the centroid is judged against the
    /// note's card (looking at the note you are holding is evidence, not intent) and, once the
    /// Place-here window is up, against the disc's margin (sitting 9) and the ACCEPTED SPOT
    /// (sitting 10 M1): a settle within <see cref="LayoutConstants.NotePlaceHereHoldRadiusDip"/>
    /// of the centroid that placed the disc is ignored, so the disc, its dwell and the outline
    /// hold still — only a genuinely new spot (beyond the hold radius) relocates the disc.
    /// Otherwise the Place-here window appears beside the centroid and is dwelled to re-anchor
    /// the note. Every accepted settle writes one content-free log line (sitting 10 M3).
    /// </summary>
    private void OnSettled(NoteData moving, PhysicalPoint centroidDip)
    {
        _moveSettles++;
        var centroid = ToPhysicalPoint(centroidDip);
        if (_placements.TryGetValue(moving.Id, out var placement)
            && DipRectToPhysical(placement.Card).Contains(centroid))
        {
            // Looking at the note you are holding is evidence, not intent. An accepted outline
            // already showing at the place disc's spot is left alone.
            if (!_placeShown)
                HideMovePreview();
            _settleInfo(SettleOnCardLine(centroidDip));
            return;
        }

        // P3 D4's settle-within-place guard: a shown Place-here window holds still through the
        // settles in its margin (the disc's hit radius + detour, physical px) — the dwell on it
        // accumulates and the window is not re-shown to a different spot on every new settle.
        // Sitting 9: the margin is measured around the DISC's centre, the control the gaze must
        // reach, not around the window's middle (the label hangs below the disc).
        if (_placeShown)
        {
            double detour =
                DpiHelper.LogicalToPhysicalX(LayoutConstants.NotePlaceHereDetourDip)
                + DpiHelper.LogicalToPhysicalX(LayoutConstants.NotePlaceHereHitRadiusDip);
            var disc = PlaceDiscCentrePhysical();
            if (new PhysicalRect(
                    disc.X - detour, disc.Y - detour,
                    detour * 2, detour * 2).Contains(centroid))
            {
                _moveHolds++;
                _settleInfo(SettleHeldLine(centroidDip, DistanceToAcceptedSpotDip(centroidDip)));
                return;
            }

            // Sitting 10 M1: the disc relocates only when the new settle is genuinely ELSEWHERE —
            // beyond the hold radius from the accepted spot, the centroid that placed the disc
            // (_placeCentroid). The owner's look-back at the spot where the note should go sat
            // ~115 DIP from the disc (past the margin, well inside this radius) and kept moving
            // the disc away; inside the radius the disc, its dwell and the outline hold still.
            if (_placeCentroid is { } accepted)
            {
                var acceptedDip = ToDipPoint(accepted);
                double distanceDip = DipDistance(acceptedDip, centroidDip);
                if (distanceDip <= LayoutConstants.NotePlaceHereHoldRadiusDip)
                {
                    _moveHolds++;
                    _settleInfo(SettleHeldLine(centroidDip, distanceDip));
                    return;
                }
                _moveMoves++;
                _settleInfo(SettleMovedLine(centroidDip, acceptedDip, distanceDip));
                ShowPlaceHere(moving, centroid); // the disc relocates; the accepted spot moves with it
                return;
            }
            // _placeShown without a centroid cannot happen (ShowPlaceHere sets both), but a
            // defensive fall-through re-shows rather than throwing on a wiring anomaly.
        }

        _settleInfo(SettleShownLine(centroidDip));
        ShowPlaceHere(moving, centroid);
    }

    // ── the Move log (sitting 10 M3): one content-free line per settle outcome and per move end ──

    /// <summary>The line formats. Invariant culture by construction (the static formatters below
    /// pass <see cref="CultureInfo.InvariantCulture"/>), positions in DIP at one decimal place,
    /// no note text — a settle line names only the outcome and where the gaze rested.</summary>
    private const string SettleOnCardLineFormat = "NOTE-SETTLE out=on-card x={0:F1} y={1:F1}";
    private const string SettleShownLineFormat = "NOTE-SETTLE out=shown x={0:F1} y={1:F1}";
    private const string SettleHeldLineFormat = "NOTE-SETTLE out=held x={0:F1} y={1:F1} d={2:F1}";
    private const string SettleMovedLineFormat = "NOTE-SETTLE out=moved x={0:F1} y={1:F1} ox={2:F1} oy={3:F1} d={4:F1}";
    private const string MoveEndLineFormat = "NOTE-MOVE end={0} ms={1:0} settles={2} holds={3} moves={4}";

    /// <summary>Settle on the moving note's own card — ignored (looking at the note you are
    /// holding is evidence, not intent).</summary>
    internal static string SettleOnCardLine(PhysicalPoint centroidDip) =>
        string.Format(CultureInfo.InvariantCulture, SettleOnCardLineFormat, centroidDip.X, centroidDip.Y);

    /// <summary>First settle off the card: the Place-here disc appears beside the spot.</summary>
    internal static string SettleShownLine(PhysicalPoint centroidDip) =>
        string.Format(CultureInfo.InvariantCulture, SettleShownLineFormat, centroidDip.X, centroidDip.Y);

    /// <summary>Settle inside the disc's margin or within the hold radius of the accepted spot:
    /// ignored, the disc holds still. The distance to the accepted spot (DIP) rides the line.</summary>
    internal static string SettleHeldLine(PhysicalPoint centroidDip, double distanceDip) =>
        string.Format(CultureInfo.InvariantCulture, SettleHeldLineFormat,
            centroidDip.X, centroidDip.Y, distanceDip);

    /// <summary>Settle beyond the hold radius: the disc relocates to the new spot. The old spot
    /// (ox/oy) and the relocation distance (d, DIP) ride the line.</summary>
    internal static string SettleMovedLine(PhysicalPoint newSpotDip, PhysicalPoint oldSpotDip, double distanceDip) =>
        string.Format(CultureInfo.InvariantCulture, SettleMovedLineFormat,
            newSpotDip.X, newSpotDip.Y, oldSpotDip.X, oldSpotDip.Y, distanceDip);

    /// <summary>The move's end: placed, cancelled or otherside, with the move's duration (ms on
    /// the injected clock) and the accepted settles' tally (total, held, moved).</summary>
    internal static string MoveEndLine(string end, double durationMs, int settles, int holds, int moves) =>
        string.Format(CultureInfo.InvariantCulture, MoveEndLineFormat,
            end, durationMs, settles, holds, moves);

    /// <summary>The move session's end line (sitting 10 M3), written while
    /// <see cref="_movingNoteId"/> still names the move and the session counters are current.</summary>
    private void WriteMoveEndLine(string end) =>
        _settleInfo(MoveEndLine(end,
            Math.Max(0, _clockMs() - _moveStartedMs), _moveSettles, _moveHolds, _moveMoves));

    /// <summary>The settle's distance from the accepted spot (the centroid that placed the disc)
    /// in DIP — the measure M1 holds on and the held lines carry. 0 when no disc is up (the
    /// margin arm never runs then).</summary>
    private double DistanceToAcceptedSpotDip(PhysicalPoint centroidDip) =>
        _placeCentroid is { } accepted ? DipDistance(ToDipPoint(accepted), centroidDip) : 0.0;

    private static double DipDistance(PhysicalPoint a, PhysicalPoint b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>(P3 D4) Puts the Place-here window beside an accepted settle's centroid —
    /// clamped into the work area, offset along +X — and swaps the :place dwell target onto it.
    /// The target's own-window mapping points at the Place-here window itself, NOT the moving
    /// note's card: the conjunct resolves the topmost hit-testable window under the gaze, and
    /// the gaze is on the place window here.
    /// <para>
    /// Sitting 9: the control is the DISC at the window's top, so the disc — not the window's
    /// middle — is level with the accepted centroid, and the label hangs below it. The accepted
    /// landing preview is (re)placed at the accepted spot at the same time, so the dashed
    /// outline the owner saw while settling is exactly where the note will land.
    /// </para>
    /// </summary>
    private void ShowPlaceHere(NoteData moving, PhysicalPoint centroidPhysical)
    {
        var work = WorkAreaDip();
        // T6 fixup-07 F3: the window is created once and only hidden afterwards, so it can carry
        // a stale size from a text-size change made while it was hidden. Re-apply the current
        // size before its size is read - the call is idempotent.
        _placeWindow ??= new NotePlaceHereWindow(_noteSettings.TextSizeDip);
        _placeWindow.SetTextSize(_noteSettings.TextSizeDip);
        var size = _placeWindow.SizeDip;
        var disc = _placeWindow.DiscCentreDip;
        var centroidDip = ToDipPoint(centroidPhysical);
        double x = centroidDip.X + LayoutConstants.NotePlaceHereOffsetDip;
        double y = centroidDip.Y - disc.Y;
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - size.Width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - size.Height));

        _placeWindowRect = new Rect(x, y, size.Width, size.Height);
        _placeWindow.Left = x;
        _placeWindow.Top = y;
        _placeWindow.ShowForDisplay(RealNoteWindows);
        RegisterPlaceTarget(moving.Id);
        _placeCentroid = centroidPhysical;
        _placeShown = true;
        // The accepted spot holds while the disc is up: outline only, no ring (the settle is over).
        ShowMovePreview(moving, centroidDip, progress: 1.0, ring: false);
        _dwellDetector.ResetAllState();
        LatchTargetsUnderTheGaze();
    }

    /// <summary>
    /// The Place-here window follows the note's text size (T6 fixup-06 G1): its label is
    /// re-composed at the new size, the rect follows the REAL window size, and the dwell target
    /// is re-registered over the new DISC centre — a target left on the old centre would sit off
    /// the control and the dwell would never fire.
    /// </summary>
    private void RefreshPlaceWindow()
    {
        if (_placeWindow is null || !_placeShown || _movingNoteId is not { } movingId) return;
        _placeWindow.SetTextSize(_noteSettings.TextSizeDip);
        var size = _placeWindow.SizeDip;
        var work = WorkAreaDip();
        double x = Math.Clamp(_placeWindowRect.X, work.Left, Math.Max(work.Left, work.Right - size.Width));
        double y = Math.Clamp(_placeWindowRect.Y, work.Top, Math.Max(work.Top, work.Bottom - size.Height));
        _placeWindowRect = new Rect(x, y, size.Width, size.Height);
        _placeWindow.Left = x;
        _placeWindow.Top = y;
        RegisterPlaceTarget(movingId);
    }

    /// <summary>
    /// Registers (or re-registers) the {noteId}:place dwell target over the current Place-here
    /// window's DISC (P3 D4; sitting 9: the disc is the control). The target lives in the main
    /// detector and its own-window mapping points at the Place-here window itself, NOT the moving
    /// note's card: the conjunct resolves the topmost hit-testable window under the gaze, and the
    /// gaze is on the place window here. Used on every show and on every full re-registration that
    /// would otherwise drop a live <c>:place</c>.
    /// </summary>
    private void RegisterPlaceTarget(string noteId)
    {
        if (_placeWindow is null) return;
        var centre = PlaceDiscCentrePhysical();
        _dwellDetector.RemoveTarget($"{noteId}:place");
        _dwellDetector.AddTarget(new DwellTarget($"{noteId}:place", centre,
            DpiHelper.LogicalToPhysicalX(LayoutConstants.NotePlaceHereHitRadiusDip), DwellTimeMs));
        _targetWindows[$"{noteId}:place"] = new WindowInteropHelper(_placeWindow).EnsureHandle();
    }

    /// <summary>The Place-here disc's centre in physical px (sitting 9): read from the real
    /// window's geometry and the rect the manager placed it in, so the :place target and the
    /// settle-within-place margin sit on the DRAWN disc — not on the window's middle, where the
    /// label below the disc pulls the centre off the control.</summary>
    private PhysicalPoint PlaceDiscCentrePhysical()
    {
        var disc = _placeWindow?.DiscCentreDip ?? new Point(_placeWindowRect.Width / 2, _placeWindowRect.Height / 2);
        return new PhysicalPoint(
            DpiHelper.LogicalToPhysicalX(_placeWindowRect.X + disc.X),
            DpiHelper.LogicalToPhysicalY(_placeWindowRect.Y + disc.Y));
    }

    /// <summary>Hides the Place-here window and unregisters its dwell target and window map
    /// entry. Keeps the window instance for the next Move (one per manager; Dispose closes).</summary>
    private void HidePlaceHere()
    {
        _placeWindow?.Hide();
        if (_movingNoteId is { } moveId)
        {
            _dwellDetector.RemoveTarget($"{moveId}:place");
            _targetWindows.Remove($"{moveId}:place");
        }
        _placeShown = false;
        _placeCentroid = null;
    }

    // ── the Move preview (sitting 9): the settle ring and the dashed landing outline ──

    /// <summary>The settle window's share that must have held before the preview appears
    /// (150 ms of the 500 ms settle): the eyes scan while they look for a spot, and a preview
    /// that flashed on every short rest would be noise.</summary>
    private const double PreviewHoldProgress =
        LayoutConstants.NoteMovePreviewHoldMs / (double)NoteSettleTracker.SettleMs;

    /// <summary>
    /// Drives the accumulating preview from the settle tracker (sitting 9): once the current
    /// window has held for <see cref="PreviewHoldProgress"/>, the ring appears at the window's
    /// anchor and its arc fills with the tracker's progress, and the dashed outline shows where
    /// the card would land for that anchor. A restart (an out-of-radius sample), a gap or a
    /// closed gate drops the tracker's progress to zero, which hides the preview here.
    /// <para>
    /// While the place disc is up the accepted outline holds at the accepted spot (placed by
    /// <see cref="ShowPlaceHere"/>): new samples never move it. Looking at the card itself is
    /// not a landing spot — <see cref="OnSettled"/> hides the preview there.
    /// </para>
    /// </summary>
    private void UpdateMovePreview(NoteData moving)
    {
        if (_placeShown)
            return;
        if (_settleTracker.Progress < PreviewHoldProgress || _settleTracker.Anchor is not { } anchorDip)
        {
            HideMovePreview();
            return;
        }

        ShowMovePreview(moving, anchorDip, _settleTracker.Progress, ring: true);
    }

    /// <summary>Places and shows the preview at a settle point in DIP: the ring at the point and
    /// the dashed outline around the card <see cref="PreviewPlacementFor"/> derives — the same
    /// card the real re-anchor will use. The window goes through the note show seam, so a
    /// headless test never paints it.</summary>
    private void ShowMovePreview(NoteData moving, PhysicalPoint anchorDip, double progress, bool ring)
    {
        var placement = PreviewPlacementFor(moving, anchorDip);
        _movePreviewWindow ??= new NoteMovePreviewWindow();
        _movePreviewWindow.Place(new Point(anchorDip.X, anchorDip.Y), placement.Card, progress, ring);
        _movePreviewWindow.ShowForDisplay(RealNoteWindows);
        _movePreviewShown = true;
    }

    /// <summary>
    /// The landing preview's placement: EXACTLY the derivation <see cref="FinishMove"/> applies
    /// when the note re-anchors — <see cref="NoteLayout.Place"/> over the same anchor, saved side,
    /// card size, work area and (no avoid rects, no displacement), plus the tag clearance — so
    /// the dashed outline and the real result cannot disagree.
    /// </summary>
    private NotePlacement PreviewPlacementFor(NoteData note, PhysicalPoint anchorDip) =>
        NoteLayout.WithTagClearance(
            NoteLayout.Place(ToPhysicalPoint(anchorDip), note.Side, PreviewCardSizeFor(note), WorkAreaDip(),
                new List<Rect>(), DpiHelper.ScaleX)!,
            _noteSettings.TextSizeDip);

    /// <summary>The preview's card size, memoized on the note, its text and the current text size.
    /// <see cref="CardSizeFor(NoteData)"/> MEASURES the wrapped text, and the preview asks for the
    /// placement on every gaze sample of a settle; the key re-measures the moment any input it was
    /// derived from changes, so the memo can never report a size the note no longer has.</summary>
    private Size PreviewCardSizeFor(NoteData note)
    {
        string key = $"{note.Id}|{_noteSettings.TextSizeDip}|{note.Text}";
        if (!string.Equals(key, _previewCardSizeKey, StringComparison.Ordinal))
        {
            _previewCardSize = CardSizeFor(note);
            _previewCardSizeKey = key;
        }
        return _previewCardSize;
    }

    /// <summary>Hides the preview window and its seam state. Keeps the instance for the next
    /// Move (one per manager; Dispose closes it).</summary>
    private void HideMovePreview()
    {
        _movePreviewWindow?.Hide();
        _movePreviewShown = false;
    }

    /// <summary>Gate conjunct 4's occluder half (P2 D3): the keyboard window and its edge bar
    /// (through the probe), the lag mark, the bottom-centre banner slot's live occupant (B4: drift
    /// notice, caregiver alert, future cards - one aggregate source), the track-status card, and
    /// (C10 fixup-04) the blocked-input banner's whole visible window.
    /// Each source is read live; nulls contribute nothing.
    /// <para>
    /// FAIL CLOSED (T2 fixup 02), with the ✓ Done exemption (T2 fixup 03 m1): a throwing source
    /// means the sample is treated as OCCLUDED for every target EXCEPT <c>{id}:done</c>, so no
    /// destructive or state-changing control can fire under an overlay the gate cannot see past.
    /// Done is exempt because it is non-destructive AND P1's only Edit exit — a broken source
    /// must never trap the owner in Edit with the keyboard captured. A GENUINE occluder (a
    /// healthy source answering true) gates Done like everything else. A wiring slip then
    /// degrades to "destructive notes controls dead under that source" instead of a per-sample
    /// exception that starves the gaze broadcast. The failure is logged once per source per
    /// failure episode (a successful read re-arms the log, so a recovered-then-refailed source
    /// is loud again) — never once per sample.
    /// </para></summary>
    /// <returns>Whether a source GENUINELY occludes the gaze, and whether any source FAILED (the
    /// fail-closed answer, which Done is exempt from).</returns>
    private (bool GenuineOcclusion, bool Failed) EvaluateOccluders(PhysicalPoint gazePoint)
    {
        bool genuine = false, failed = false;

        if (!TryReadOccluder(KnownOccluderSources.KeyboardProbe,
                () => _keyboardProbe?.Rects().Any(rect => rect.Contains(gazePoint)) == true, out var covers))
            failed = true;
        else
            genuine |= covers;

        if (!TryReadOccluder(KnownOccluderSources.LagMark,
                () => _lagMarkRects is { } lagMark && lagMark().Any(rect => rect.Contains(gazePoint)), out covers))
            failed = true;
        else
            genuine |= covers;

        if (!TryReadOccluder(KnownOccluderSources.BannerSlot,
                () => _slotOccupantBounds is { } slotOccupant && slotOccupant.Invoke() is { } slotBounds
                    && slotBounds.Contains(gazePoint), out covers))
            failed = true;
        else
            genuine |= covers;

        if (!TryReadOccluder(KnownOccluderSources.TrackStatus,
                () => _trackStatusBounds is { } trackStatus && trackStatus.Invoke() is { } trackStatusBounds
                    && trackStatusBounds.Contains(gazePoint), out covers))
            failed = true;
        else
            genuine |= covers;

        if (!TryReadOccluder(KnownOccluderSources.NoteNotice,
                () => _noteNoticeBounds is { } notice && notice.Invoke() is { } noticeBounds
                    && noticeBounds.Contains(gazePoint), out covers))
            failed = true;
        else
            genuine |= covers;

        if (!TryReadOccluder(KnownOccluderSources.StatusStrip,
                () => _statusStripBounds is { } strip && strip.Invoke() is { } stripBounds
                    && stripBounds.Contains(gazePoint), out covers))
            failed = true;
        else
            genuine |= covers;

        // C10 fixup-04: the blocked-input banner's whole visible window. Its buttons ARE the
        // emergency exits; a note ✕ under one is a control the owner did not aim at, and the
        // note's 600 ms dwell beats the banner's 700 ms.
        if (!TryReadOccluder(KnownOccluderSources.BlockedInputBanner,
                () => _blockedBannerBounds is { } blockedBanner
                    && blockedBanner.Invoke() is { } blockedBannerBounds
                    && blockedBannerBounds.Contains(gazePoint), out covers))
            failed = true;
        else
            genuine |= covers;

        return (genuine, failed);
    }

    /// <summary>The per-target gate answer: a GENUINE occluder closes the gate for everyone; a
    /// FAILED source closes it for every target except <c>{id}:done</c> (fixup 03 m1 — Done is
    /// non-destructive and the only Edit exit; everything destructive stays shut).</summary>
    private static bool GateOpenFor(DwellTarget target, bool genuineOcclusion, bool occluderFailed) =>
        !(genuineOcclusion || (occluderFailed && !target.Id.EndsWith(":done", StringComparison.Ordinal)));

    /// <summary>The occluder sources' stable names, for the fail-closed log (one entry per source
    /// per failure episode).</summary>
    internal static class KnownOccluderSources
    {
        public const string KeyboardProbe = "keyboard probe";
        public const string LagMark = "lag mark";
        public const string BannerSlot = "banner slot";
        public const string TrackStatus = "track status";
        public const string NoteNotice = "note notice";
        public const string StatusStrip = "status strip";
        public const string BlockedInputBanner = "blocked input banner";
    }

    /// <summary>The logged-failure episode flags, one per source name: a failed source logs once,
    /// and a successful read clears its flag so a later failure is loud again.</summary>
    private readonly HashSet<string> _occluderFailureLogged = new(StringComparer.Ordinal);

    /// <summary>Runs one occluder read. A throw is contained: logged once per source per failure
    /// episode, and reported to the caller as a FAILED source — the fail-closed answer applies to
    /// every target except ✓ Done (fixup 03 m1, see <see cref="GateOpenFor"/>).</summary>
    /// <returns>False when the read threw (the failure is logged here); true with the result
    /// otherwise.</returns>
    private bool TryReadOccluder(string name, Func<bool> read, out bool covers)
    {
        try
        {
            covers = read();
            _occluderFailureLogged.Remove(name); // a healthy read re-arms the once-per-episode log
            return true;
        }
        catch (Exception ex)
        {
            covers = false;
            if (_occluderFailureLogged.Add(name))
                Serilog.Log.Error(ex,
                    "The note gate's {OccluderSource} occluder source threw; failing closed — "
                    + "destructive note controls stay gated until the source recovers", name);
            return false;
        }
    }

    /// <summary>The target with this id, across the card detector, the chips and the reveal
    /// targets. Ids never collide (the suffixes differ).</summary>
    private DwellTarget? FindTarget(string id) =>
        _dwellDetector.Targets.FirstOrDefault(t => t.Id == id)
        ?? _undoDetector.Targets.FirstOrDefault(t => t.Id == id)
        ?? _revealDetector.Targets.FirstOrDefault(t => t.Id == id);

    /// <summary>Whether the point lies in a target hit area (P4 D3): rect containment for the
    /// note hit cells AND for the chip Undo cell (it is a cell too); circle distance for every
    /// remaining target - only the Place-here dwell keeps its circle.</summary>
    private static bool TargetContains(DwellTarget target, PhysicalPoint point)
    {
        if (target.Bounds is { } bounds)
            return bounds.Contains(point);
        double dx = point.X - target.Center.X;
        double dy = point.Y - target.Center.Y;
        return dx * dx + dy * dy <= (double)target.Radius * target.Radius;
    }

    /// <summary>Whether the gaze lies inside the union of the live targets hit areas (P2 D2
    /// cost cap), over BOTH detectors: rect targets by their bounds, circular targets by their
    /// circle.</summary>

    private bool GazeInsideTargetUnion(PhysicalPoint gazePoint)
    {
        foreach (var target in _dwellDetector.Targets.Concat(_undoDetector.Targets).Concat(_revealDetector.Targets))
        {
            if (target.Bounds is { } bounds)
            {
                if (bounds.Contains(gazePoint))
                    return true;
                continue;
            }
            double dx = gazePoint.X - target.Center.X;
            double dy = gazePoint.Y - target.Center.Y;
            if (dx * dx + dy * dy <= (double)target.Radius * target.Radius)
                return true;
        }
        return false;
    }

    private bool AnyWindowedTarget =>
        _targetWindows.Values.Any(handle => handle != IntPtr.Zero)
        || _undoTargetWindows.Values.Any(handle => handle != IntPtr.Zero)
        || _revealTargetWindows.Values.Any(handle => handle != IntPtr.Zero);

    /// <summary>Whether the own-window conjunct is in force: it switches off only for the named
    /// <see cref="InactiveNoteHitTester"/> sentinel that headless tests pass (fixup 01 M2) — the
    /// production default is the real tester, so an unwired argument cannot disable the gate.</summary>
    private bool OwnWindowConjunctActive => _hitTester is not InactiveNoteHitTester;

    /// <summary>Gate conjunct 4's own-window half (P2 D2): the topmost hit-testable window under
    /// the gaze must be the window that draws the target — a card target's window or a chip's
    /// (P2 D5). The conjunct is inactive for the sentinel tester (headless tests), and a
    /// windowless target (registered with a Zero handle — the test seams) is exempt, exactly as
    /// before the chip work; a chip born windowless is exempt the same way.</summary>
    private bool WindowOwns(DwellTarget target, IntPtr rootWindow)
    {
        if (!OwnWindowConjunctActive)
            return true;
        bool hasCard = _targetWindows.TryGetValue(target.Id, out var own) && own != IntPtr.Zero;
        bool hasUndo = _undoTargetWindows.TryGetValue(target.Id, out var undoOwn) && undoOwn != IntPtr.Zero;
        bool hasReveal = _revealTargetWindows.TryGetValue(target.Id, out var revealOwn) && revealOwn != IntPtr.Zero;
        if (!hasCard && !hasUndo && !hasReveal)
            return true; // windowless (the test seams)
        var expected = hasCard ? own : hasUndo ? undoOwn : revealOwn;
        return rootWindow != IntPtr.Zero && rootWindow == expected;
    }

    public void Receive(GazeCursorMovedMessage message)
    {
        // P2 D9: a message delivered after Dispose is ignored, not resurrected from.
        if (_disposed) return;

        // Gate row 1 (P2 D1): held (blink) samples repeat the frozen last valid position — they
        // must not advance a note's close/edit/move dwell, and a note being placed simply stays
        // put until the eyes reopen. P3 D4: a gap also invalidates any running settle (its
        // dispersion window must span continuous samples only).
        if (message.IsInGap)
        {
            _settleTracker.Invalidate();
            if (!_placeShown)
                HideMovePreview(); // a gap is no information: the ring's window restarts
            return;
        }

        // Gate row 2 (P2 D1): paused — the sample's own flag OR the polled interaction state.
        // The poll is what catches a pause entered from the tray while no sample carried the flag.
        // Gate row 3: the suspend predicate (modal surfaces, interaction modes, modes menu — the
        // composed lambda EventCoordinator wires) plus the manager's own flags: the eye-care
        // exercise (click-through, full-screen — WindowFromPoint sees through it) and calibration
        // (click-through from Collecting on). While any of them holds, every sample is dropped and
        // the notes are frozen.
        bool paused = message.IsPaused || (_pausePoll?.Invoke() ?? false);
        bool suspended = (_suspendPredicate?.Invoke() ?? false) || _exerciseActive || _calibrating;
        if (paused || suspended)
        {
            _gateClosed = true;
            _settleTracker.Invalidate(); // P3 D4: gated samples are no information — disperse, restart
            if (!_placeShown)
                HideMovePreview(); // sitting 9: the accumulating ring goes with the window
            return;
        }
        if (_gateClosed)
        {
            // Recovery (P2 D1 rows 2–3): reset the detector — its clock is timestamp-based, so
            // without the reset the first clear sample on the same target would be credited time
            // it did not spend there (review 03 F5) — then arm-latch every target under the gaze:
            // a target that sat under held eyes while the gate was shut must not fire on the
            // first clear sample.
            _gateClosed = false;
            _dwellDetector.ResetAllState();
            _undoDetector.ResetAllState();
            // Fixup-01 F8: the reveal detector's clock is timestamp-based too; the latch below
            // covers the same spot, but the rule is the same as the other two detectors.
            _revealDetector.ResetAllState();
            _settleTracker.Reset(); // P3 D4: a settle interrupted by the gate starts over, too
            _lastGazePoint = message.Position;
            LatchTargetsUnderTheGaze();
        }

        // P2 D5's clock condition 3: THIS is a valid, non-gap, gate-passing sample — stamp it
        // with the injected clock's own now, so the chip countdown's freshness reads the same
        // monotonic source the tick does. Gap and gated samples never reach this line.
        _lastValidSampleClockMs = _clockMs();

        OnGazeMoved(message.Position, message.TimestampMicroseconds);
    }

    /// <summary>Gate row 3's interaction-state half (P2 D1): the states whose surfaces own the
    /// screen while they last. ActionMenuVisible and the two ManualSelection states show the menu
    /// and the full-screen magnifier, ScrollMode/Dragging/RepeatMode pin their own controls at the
    /// live gaze point (review 02 confirmed the set is complete). The keyboard is deliberately NOT
    /// here — Done must stay live while typing; a covered note is caught by conjunct 4.</summary>
    internal static bool InteractionStateSuspendsNotes(InteractionState state) =>
        state is InteractionState.ActionMenuVisible
            or InteractionState.ManualSelectionActive
            or InteractionState.ManualSelectionActionMenuVisible
            or InteractionState.ScrollMode
            or InteractionState.Dragging
            or InteractionState.RepeatMode;

    /// <summary>The exercise flag's message (P2 D1). Idempotent: EyeCareService sends it once per
    /// transition; a duplicate changes nothing.</summary>
    public void Receive(EyeCareExerciseActiveMessage message)
    {
        if (_disposed) return; // P2 D9: a late message is ignored
        _exerciseActive = message.Active;
    }

    /// <summary>The calibration flag's message (P2 D1, review-03 B-2): Collecting/Training hold the
    /// full-screen overlay up; every other state — including Failed and Cancelled — takes it down.</summary>
    public void Receive(CalibrationStateChangedMessage message)
    {
        if (_disposed) return; // P2 D9: a late message is ignored
        _calibrating = message.State is CalibrationState.Collecting or CalibrationState.Training;
    }

    /// <summary>T6's message trigger (P3 D3): the display geometry or work area changed. The
    /// payload is not read — the re-derivation reads the work-area seam, which IS the current
    /// truth in production (SystemParameters.WorkArea) and under test (the injected provider).</summary>
    public void Receive(DisplayGeometryChangedMessage message) => RePlaceEveryNote();

    /// <summary>
    /// T6 P5: the LOWEST free positive integer among the live notes' numbers. Computed BEFORE a
    /// new note joins <see cref="_notes"/>, so its own number never collides. A chip still holds
    /// its number (it stays in <c>_notes</c> until its final removal), so Undo restores the same
    /// numeral and a note pinned over a chip never takes the chip's number — release happens
    /// only when <see cref="RemoveNoteInternal"/> drops the note.
    /// </summary>
    private int LowestFreeNumber()
    {
        var used = new HashSet<int>(_notes.Select(n => n.Number));
        for (int candidate = 1; ; candidate++)
            if (!used.Contains(candidate))
                return candidate;
    }

    /// <summary>
    /// T6 P5: pushes every window's badge numeral. Badges show on the card notes only while
    /// TWO OR MORE notes are NOT chips (Display, Edit or Move); chips never show one and never
    /// count. Idempotent per window (<c>SetBadge</c> early-returns unchanged values) and cheap
    /// (≤ MaxNotes windows), so riding <see cref="RaiseTargetsChanged"/> — the funnel every
    /// membership and state change already walks — cannot hot-loop it.
    /// </summary>
    private void RefreshBadges()
    {
        if (_disposed) return;
        bool visible = _notes.Count(n => n.State != NoteState.PendingUndo) >= 2;
        foreach (var note in _notes)
        {
            int? number = visible && note.State != NoteState.PendingUndo ? note.Number : null;
            Window(note.Id)?.SetBadge(number);
        }
    }

    /// <summary>T6's SystemParameters trigger (P3 D3): the DisplayGeometryChangedMessage does
    /// not fire on a taskbar resize or auto-hide; this does.</summary>
    private void OnSystemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.WorkArea))
            RePlaceEveryNote();
    }

    /// <summary>Test seam (fixup-01 G2): drives the SystemParameters half of T6 the way the
    /// static property-changed handler does, without touching global WPF statics.</summary>
    internal void OnWorkAreaChangedForTest() =>
        OnSystemPropertyChanged(null, new(nameof(SystemParameters.WorkArea)));

    /// <summary>The pause poll seam (P2 D1 row 2), configured once from EventCoordinator.</summary>
    public void SetPausePoll(Func<bool> poll) =>
        _pausePoll = poll ?? throw new ArgumentNullException(nameof(poll));

    /// <summary>The suspend seam (build plan C15), configured once from EventCoordinator.
    /// The predicate is evaluated per delivered sample; nothing else changes while it holds.</summary>
    public void SetSuspendPredicate(Func<bool> predicate) =>
        _suspendPredicate = predicate ?? throw new ArgumentNullException(nameof(predicate));

    /// <summary>The occluder seams (P2 D3), configured once from the App wiring. Delegates are
    /// evaluated per sample, only for samples that reach conjunct 4 — there is no cache to
    /// invalidate, and a source attached later (the lag-mark bridge) needs no re-wire. The
    /// banner slot (B4) is ONE aggregate source for every bottom-centre banner. The track-status
    /// card joined in fixup 01 (review M1): opaque, click-through, parked top-right for as long as
    /// TrackStatusSettings.Enabled. C10 fixup-04 added the blocked-input banner: a note fires from
    /// its own gaze dwell, so a click-through banner does not stop one beneath it.</summary>
    public void SetOccluderSources(
        Func<IReadOnlyList<PhysicalRect>>? lagMarkRects = null,
        Func<PhysicalRect?>? slotOccupantBounds = null,
        Func<PhysicalRect?>? trackStatusBounds = null,
        Func<PhysicalRect?>? noteNoticeBounds = null,
        Func<PhysicalRect?>? statusStripBounds = null,
        Func<PhysicalRect?>? blockedBannerBounds = null)
    {
        _lagMarkRects = lagMarkRects;
        _slotOccupantBounds = slotOccupantBounds;
        _trackStatusBounds = trackStatusBounds;
        _noteNoticeBounds = noteNoticeBounds;
        _statusStripBounds = statusStripBounds;
        _blockedBannerBounds = blockedBannerBounds;
    }

    /// <summary>Test/DI-pin seam (fixup 01 M2): the hit tester this manager actually uses. The
    /// container-wired manager must report the production <see cref="NoteHitTester"/>.</summary>
    internal INoteHitTester HitTesterForTest => _hitTester;

    /// <summary>Test seam (the CI work-area fix): the work area this manager derives every note
    /// placement from - the injected provider, else the test default, else the real one.</summary>
    internal Rect WorkAreaDipForTest => _workAreaDip();

    /// <summary>
    /// Test seam (review 03 F5): registers a Display note's dwell targets without constructing a
    /// <see cref="NoteWindow"/> — <see cref="CreateNoteAt"/> shows and focuses a real window, which
    /// W5(d) must not do. Returns the generated note id.
    /// <para>
    /// D (sitting 11): the seam's note is the REVEALED Display note — its row (✎ ↕ ✕) is what
    /// every caller of this seam wants to dwell. The at-rest path (only the reveal target) is
    /// covered by the real-window reveal tests; a caller that needs it uses a real note.
    /// </para>
    /// </summary>
    internal string AddNoteTargetsForTest(PhysicalPoint anchor)
    {
        var data = new NoteData { Anchor = anchor, Side = NoteSide.Right, State = NoteState.Display };
        _placements[data.Id] = NoteLayout.WithRevealTargetClearance(
            NoteLayout.WithTagClearance(PlaceFor(data, avoidKeyboard: false), _noteSettings.TextSizeDip),
            RestCardSizeFor(data));
        _notes.Add(data);
        _seamRevealedNotes.Add(data.Id);
        RegisterDwellTargets(data.Id);
        _dwellDetector.ResetAllState();
        return data.Id;
    }

    /// <summary>
    /// Test seam (P1 tests): enters Edit on a windowless note — the BeginEditing state, the
    /// Done/Show-keyboard targets and the (true) message, without the real window
    /// <see cref="CreateNoteAt"/> would show. Summon/hide assertions still read the messenger.
    /// Returns the generated note id.
    /// </summary>
    internal string AddEditingNoteTargetsForTest(PhysicalPoint anchor)
    {
        ExitEditing(commit: true, handOver: true);
        var data = new NoteData { Anchor = anchor, Side = NoteSide.Right, State = NoteState.Edit, PreviousText = null, Draft = string.Empty };
        _placements[data.Id] = NoteLayout.WithTagClearance(PlaceFor(data, avoidKeyboard: false), _noteSettings.TextSizeDip);
        _notes.Add(data);
        RegisterSoleEditorTargets(data.Id);
        _dwellDetector.ResetAllState();
        BeginEditing(data);
        return data.Id;
    }

    /// <summary>Test seam: whether the editing note currently owns the keyboard claim (P1 D5's
    /// OwnsKeyboard). The claim has no user-visible P1 difference outside a hand-over, so state
    /// is exposed for the ownership tests instead of inferred.</summary>
    internal bool EditingOwnsKeyboardForTest => _editingOwnsKeyboard;

    /// <summary>Test seam (P2 D7's coordinator wiring): places a windowless NoteData DIRECTLY in
    /// the Move state with the move set registered and the targets-changed raise broadcast —
    /// StartMove's body where its real NoteWindow would exist (the drawn-state mutations are the
    /// window's). Returns the generated note id.</summary>
    internal string AddMovingNoteTargetsForTest(PhysicalPoint anchor)
    {
        var data = new NoteData { Anchor = anchor, Side = NoteSide.Right, State = NoteState.Move };
        _placements[data.Id] = NoteLayout.WithTagClearance(PlaceFor(data, avoidKeyboard: false), _noteSettings.TextSizeDip);
        _notes.Add(data);
        _movingNoteId = data.Id;
        _settleTracker.Reset();
        _moveStartedMs = _clockMs();     // the Move log session arms with StartMove's body (M3)
        _moveSettles = _moveHolds = _moveMoves = 0;
        RegisterMoveTargetsForNote(data);
        _dwellDetector.ResetAllState();
        LatchTargetsUnderTheGaze();
        RaiseTargetsChanged();
        return data.Id;
    }

    /// <summary>Test seam (P2 D5): runs exactly what the chip's Undo dwell would run — the same
    /// activation the second detector's TargetActivated handler dispatches, without the 600 ms
    /// of gaze. Non-chip ids are the same defensive no-op the real path is.</summary>
    internal void UndoChipForTest(string noteId) => OnUndoActivated($"{noteId}:undo");

    /// <summary>Test seams (fixup-01 m1): the Other-side dwell's activation (the same switch the
    /// detector dispatches, without the 600 ms gaze) and the candidate the offer judged — read
    /// back so a test can compare what was offered against what OtherSideForMoving applied.</summary>
    internal void OtherSideForTest(string noteId) => OnTargetActivated($"{noteId}:otherside");
    internal NotePlacement? JudgedOtherSideForTest => _judgedOtherSideForMoving;

    /// <summary>Test seams (P3 D4's Move acceptance): the Place-here state the settle path
    /// drives — the shown flag, the accepted centroid (physical) and the window rect (DIP) it
    /// raised beside that centroid. Read-only mirrors of the private Move-state fields.</summary>
    internal bool PlaceShownForTest => _placeShown;
    internal PhysicalPoint? PlaceCentroidForTest => _placeCentroid;
    internal Rect PlaceWindowRectForTest => _placeWindowRect;

    /// <summary>Test accessor (T6 fixup-06 G1): the Place-here window itself, so a test can
    /// check that the rect the dwell target was registered from IS the window's real size.</summary>
    internal NotePlaceHereWindow? PlaceWindowForTest => _placeWindow;

    /// <summary>Test seams (sitting 9): the Move preview the settle path drives — the window
    /// itself (its card rect, ring progress and ring visibility) and whether it counts as shown.</summary>
    internal NoteMovePreviewWindow? MovePreviewWindowForTest => _movePreviewWindow;
    internal bool MovePreviewShownForTest => _movePreviewShown;

    /// <summary>Test seam: the live window of a note, or null. The drawn-vs-registered ⌨ check
    /// (review 01 mutant A) needs the real window the manager drives on probe changes.</summary>
    internal NoteWindow? WindowForTest(string noteId) => Window(noteId);

    /// <summary>
    /// Clears and re-registers dwell targets for every note according to its current state.
    /// Called after a note is removed or returns to Display so surviving notes regain their
    /// controls, then resets accumulated dwell state to avoid a spurious immediate activation,
    /// and latches every target under the held gaze (P2 D4: every (re)registration latches).
    /// While a Move is active the re-registration hands back ONLY the moving note's move set
    /// (fixup-01 M1): a mid-Move raise (T1–T5, T6, a chip expiry, an undo) must not make the
    /// other notes' card controls live again while their note sits dimmed under the Move.
    /// </summary>
    private void ReRegisterAllTargets()
    {
        _dwellDetector.ClearTargets();
        _targetWindows.Clear();
        // D: the reveal targets are replaced with the row on a reveal and with a fresh reveal
        // target on a hide; every full replacement rebuilds them here too.
        _revealDetector.ClearTargets();
        _revealTargetWindows.Clear();
        if (_movingNoteId is { } movingId && FindNote(movingId) is { } moving)
        {
            // Move is exclusive: the moving note's Move set plus a live :place, and nothing
            // else — the other notes' ✎ ↕ ✕ stay frozen until the Move ends.
            RegisterMoveTargetsForNote(moving);
            if (_placeShown)
                RegisterPlaceTarget(movingId); // a full re-registration must not drop a live :place
        }
        else if (_editingNote is { } editor)
        {
            // Fixup F5: the single-editor rule wins — a re-register while a note edits hands back
            // ONLY the editor's targets, never every note's card targets. No P1 path reaches this
            // (RemoveNote/FinishMove are unreachable while editing), but P2's Undo chips add
            // callers, and a quiet rule break here would be a trap.
            RegisterEditTargets(editor.Id);
        }
        else
        {
            foreach (var n in _notes)
                RegisterTargetsForState(n);
        }
        _dwellDetector.ResetAllState();
        _revealDetector.ResetAllState();
        LatchTargetsUnderTheGaze();
    }

    private void RegisterTargetsForState(NoteData note)
    {
        switch (note.State)
        {
            case NoteState.Edit:
                RegisterEditTargets(note.Id);
                break;
            case NoteState.Move:
                RegisterMoveTargetsForNote(note);
                if (_placeShown && _movingNoteId == note.Id)
                    RegisterPlaceTarget(note.Id); // a full re-registration must not drop a live :place
                break;
            case NoteState.PendingUndo:
                // The chip registers NOTHING here (P2 D5): its Undo target lives in the second
                // detector and must survive every full-replacement registration of the cards'.
                break;
            default: // Display
                RegisterDwellTargets(note.Id);
                break;
        }
    }

    /// <summary>Whether the eye keyboard is believed hidden. No probe (unit tests, pre-wire) reads
    /// as hidden: the ⌨ control registers, and an entry into Edit still summons.</summary>
    private bool KeyboardHidden => !(_keyboardProbe?.IsVisible ?? false);

    /// <summary>
    /// The Edit state's targets (P1 D4/D7, final P2 layout): ✓ Done in slot 0 always, ⌨
    /// Show-keyboard in slot 1 only while the keyboard is hidden, ↩ Discard in slot 2 (P2 D5/D8
    /// — one slot from Done, its destructive counterpart's fail-closed gate applies). There is
    /// NO :close, :edit or :move while editing — Done (and, from P2, Discard) are the Edit
    /// exits, and no other note's card targets are registered (the single-editor rule). A pure
    /// PER-NOTE registrar (fixup n3): it adds this note's targets and touches nothing else — a
    /// ClearTargets hidden in here would wipe other notes' targets depending on
    /// <see cref="ReRegisterAllTargets"/>'s loop order. Callers that need a full replacement use
    /// <see cref="RegisterSoleEditorTargets"/>.
    /// </summary>
    private void RegisterEditTargets(string noteId)
    {
        var note = _notes.FirstOrDefault(n => n.Id == noteId);
        if (note == null) return;

        _dwellDetector.AddTarget(DwellTargetFor(noteId, ":done", slot: 0));
        _targetWindows[$"{noteId}:done"] = WindowHandleOrZero(noteId);
        if (KeyboardHidden)
        {
            _dwellDetector.AddTarget(DwellTargetFor(noteId, ":keyboard", slot: 1));
            _targetWindows[$"{noteId}:keyboard"] = WindowHandleOrZero(noteId);
        }
        _dwellDetector.AddTarget(DwellTargetFor(noteId, ":discard", slot: 3)); // P4 D3: the gap cell pushes Discard to the bottom row
        _targetWindows[$"{noteId}:discard"] = WindowHandleOrZero(noteId);
    }

    /// <summary>The owning note window's HWND (P2 D2), or <see cref="IntPtr.Zero"/> for a
    /// windowless note — the test seams register targets without a window, and a Zero handle
    /// marks them exempt from the own-window conjunct (see <see cref="WindowOwns"/>).</summary>
    private IntPtr WindowHandleOrZero(string noteId)
    {
        var window = Window(noteId);
        return window is null ? IntPtr.Zero : new WindowInteropHelper(window).EnsureHandle();
    }

    /// <summary>The single editor's set is the ONLY live set (P1 D8), so full replacement:
    /// clear, then register, then latch under the held gaze (P2 D4 — a probe flip or an Edit
    /// entry must not leave a target live under eyes that never left it). Used by every entry
    /// into Edit and by the probe's re-register.</summary>
    private void RegisterSoleEditorTargets(string noteId)
    {
        _dwellDetector.ClearTargets();
        _targetWindows.Clear();
        // D: an editor takes the screen; no Display note may reveal while it does.
        _revealDetector.ClearTargets();
        _revealTargetWindows.Clear();
        RegisterEditTargets(noteId);
        LatchTargetsUnderTheGaze();
    }

    private void RegisterDwellTargets(string noteId)
    {
        if (IsPresentedRevealed(noteId))
            RegisterDisplayRowTargets(noteId);
        else
            RegisterRevealTarget(noteId);
    }

    /// <summary>
    /// The revealed Display row (D4): ✎ ↕ · ✕ in the four cells under the text — the same
    /// control table slots as before (0, 1, 3), now in the row. Registered only while the note
    /// is revealed; the reveal itself is not an action, so the row is the first actionable set.
    /// </summary>
    private void RegisterDisplayRowTargets(string noteId)
    {
        var note = _notes.FirstOrDefault(n => n.Id == noteId);
        if (note == null) return;

        _dwellDetector.AddTarget(DwellTargetFor(noteId, ":edit", slot: 0));
        _dwellDetector.AddTarget(DwellTargetFor(noteId, ":move", slot: 1));
        _dwellDetector.AddTarget(DwellTargetFor(noteId, ":close", slot: 3));
        var handle = WindowHandleOrZero(noteId);
        _targetWindows[$"{noteId}:edit"] = handle;
        _targetWindows[$"{noteId}:move"] = handle;
        _targetWindows[$"{noteId}:close"] = handle;
    }

    /// <summary>
    /// The at-rest Display note's ONE target (D1/D3): the at-rest card rect enlarged to at least
    /// one hit cell, growing upward and sideways only, registered in the reveal detector at the
    /// constant 500 ms. No action target exists at rest, so a look held anywhere on the card can
    /// only reveal — the row is registered afterwards and latched (D4).
    /// </summary>
    private void RegisterRevealTarget(string noteId)
    {
        var note = _notes.FirstOrDefault(n => n.Id == noteId);
        if (note == null || !_placements.TryGetValue(noteId, out var placement))
            return;
        var rest = RestCardSizeFor(note);
        var restCard = NoteLayout.RestCardRect(placement, rest);
        var target = NoteLayout.RevealTargetRect(restCard);
        _revealDetector.AddTarget(new DwellTarget(
            noteId + ":reveal", DipRectToPhysical(target), NoteRevealDwellMs));
        _revealTargetWindows[noteId + ":reveal"] = WindowHandleOrZero(noteId);
    }

    /// <summary>Builds a dwell target over the note's drawn control #<paramref name="slot"/>
    /// (P3 D2.6; K2a: an editing note's controls sit in their row), read from the note's stored
    /// placement so the targets cannot drift from what the window draws.</summary>
    private DwellTarget DwellTargetFor(string noteId, string suffix, int slot)
    {
        if (!_placements.TryGetValue(noteId, out var placement))
            throw new InvalidOperationException($"No placement derived for note {noteId} - derive one before registering its targets");
        var state = FindNote(noteId)?.State ?? NoteState.Display;
        var (lx, ly) = NoteLayout.ButtonCenter(placement, slot, state);
        var center = new PhysicalPoint(
            DpiHelper.LogicalToPhysicalX(placement.Window.Left + lx),
            DpiHelper.LogicalToPhysicalY(placement.Window.Top + ly));
        // P4 D3, A2: the hit area is the whole 112x112 DIP cell (rect bounds), not the 88 DIP
        // disc - the forgiving cell around the smaller visible disc.
        double half = DpiHelper.LogicalToPhysicalX(LayoutConstants.NoteCellDip / 2);
        return new DwellTarget(noteId + suffix,
            new PhysicalRect(center.X - half, center.Y - half, half * 2, half * 2), DwellTimeMs);
    }


    private NoteWindow? Window(string noteId) => _windows.FirstOrDefault(w => w.NoteId == noteId);

    // ── the reveal (D, sitting 11) ──

    /// <summary>
    /// The reveal target fired (D3/D4). Only a Display note reveals, and a reveal is NOT an
    /// action: no <see cref="DwellActivationMessage"/>, no click cue, no sound. The row is
    /// registered through the normal path and latched under the held gaze, so the gaze that
    /// revealed it cannot fire a control without leaving first.
    /// </summary>
    private void OnRevealTargetActivated(string targetId)
    {
        if (_disposed) return;
        const string suffix = ":reveal";
        if (!targetId.EndsWith(suffix, StringComparison.Ordinal)) return;
        var noteId = targetId[..^suffix.Length];
        if (FindNote(noteId) is not { State: NoteState.Display } note) return;
        if (_revealedNoteId == noteId) return;
        RevealNote(note);
    }

    /// <summary>
    /// Reveals <paramref name="note"/> (D3/D4): at most one note is revealed, so any other one
    /// returns to rest at once (why=other); the row is registered through the normal
    /// registration path and then latched, and <see cref="RaiseTargetsChanged"/> publishes the
    /// new chrome/census state. The 500 ms reveal dwell is not an action, so it sends no cue.
    /// </summary>
    private void RevealNote(NoteData note)
    {
        if (_revealedNoteId == note.Id) return;
        ClearRevealedState("other");
        _revealedNoteId = note.Id;
        _revealOutsideMs = 0;
        _revealLastSampleUs = null;
        _revealShownAtMs = _clockMs();
        // Fixup-01 F1: the whole row is born DISARMED. It re-arms only after the gaze has been
        // continuously outside the at-rest card ∪ the reveal target for 150 ms; until then no
        // row target is eligible, so a drift on the card (which the geometry keeps above the
        // cells anyway) can never fire ✎/↕/✕.
        _revealArming[note.Id] = new RevealArming();
        Window(note.Id)?.SetRevealed(true);
        // D4: register the row through the normal path — it clears the reveal target, rebuilds
        // the row and ends with LatchTargetsUnderTheGaze (the existing 150 ms leave rule).
        ReRegisterAllTargets();
        RaiseTargetsChanged();
        _settleInfo(RevealShownLine(note.Number, NoteRevealDwellMs));
    }

    /// <summary>
    /// Returns the revealed note to rest (D5/D6) and re-registers, so the row's targets are
    /// replaced by the reveal target and everything is latched under the held gaze. The reason
    /// is one of D8's closed set: away|other|edit|move|close.
    /// </summary>
    private void HideRevealedNote(string why)
    {
        if (_revealedNoteId is null) return;
        ClearRevealedState(why);
        ReRegisterAllTargets();
        RaiseTargetsChanged();
    }

    /// <summary>
    /// Clears the revealed state and the hide clock without touching the detectors: the caller
    /// is a transition that runs its own registration path (✎/↕/✕, removal, another reveal), so
    /// re-registering here would only double the work. Logs the hidden line exactly once, with
    /// the visible time on the injected clock.
    /// </summary>
    private void ClearRevealedState(string why)
    {
        if (_revealedNoteId is not { } id) return;
        _revealedNoteId = null;
        _revealOutsideMs = 0;
        _revealLastSampleUs = null;
        // Fixup-01 F1/F2: neither clock outlives the revealed row it armed.
        _revealArming.Remove(id);
        _undoCloseCellArming.Remove(id);
        var note = FindNote(id);
        Window(id)?.SetRevealed(false);
        if (note is not null)
            _settleInfo(RevealHiddenLine(note.Number,
                Math.Max(0, _clockMs() - _revealShownAtMs), why));
    }

    /// <summary>
    /// D5's hide clock: the gaze has been outside the revealed card inflated by
    /// <see cref="NoteRevealHideMarginDip"/> for <see cref="NoteRevealHideMs"/> of REACHABLE
    /// time. A sample inside resets the clock; a gated sample is no information and contributes
    /// nothing (the per-interval cap also keeps a 3 s gaze loss from counting as 3 s away).
    /// </summary>
    private void AdvanceRevealHideClock(PhysicalPoint gazePoint, long timestamp, bool noInformation)
    {
        if (_revealedNoteId is not { } id) return;
        if (noInformation)
        {
            // Gated sample: no information. Move the stamp (so the next valid sample cannot
            // credit the gated interval) but neither advance nor reset the clock.
            _revealLastSampleUs = timestamp;
            return;
        }
        if (!_placements.TryGetValue(id, out var placement)) return;

        // Credit only the interval since the previous VALID sample, capped at the dwell
        // detector's own 150 ms silence clamp: a gap or a closed gate is "no information", and
        // the first sample after it must not hand the clock the whole absence.
        long deltaUs = _revealLastSampleUs is { } last ? Math.Max(0, timestamp - last) : 0;
        _revealLastSampleUs = timestamp;
        double creditMs = Math.Min(deltaUs, 150_000L) / (double)MathConstants.MicrosecondsPerMillisecond;

        double x = DpiHelper.PhysicalToLogicalX(gazePoint.X);
        double y = DpiHelper.PhysicalToLogicalY(gazePoint.Y);
        double margin = NoteRevealHideMarginDip;
        var card = placement.Card; // the revealed rect
        // Fixup-01 F5: the hide rect is union(revealed card, reveal target) + 24 DIP. The target
        // reaches above/beside the card, so the very spot that revealed the row (inside that
        // target) must keep it — otherwise a motionless gaze hid it 2 s later and flickered.
        var note = FindNote(id);
        var target = note is null
            ? card
            : NoteLayout.RevealTargetRect(
                NoteLayout.RestCardRect(placement, RestCardSizeFor(note)));
        bool inside = (x >= card.Left - margin && x <= card.Right + margin
                && y >= card.Top - margin && y <= card.Bottom + margin)
            || (x >= target.Left - margin && x <= target.Right + margin
                && y >= target.Top - margin && y <= target.Bottom + margin);
        if (inside)
        {
            _revealOutsideMs = 0;
            return;
        }

        _revealOutsideMs += creditMs;
        if (_revealOutsideMs >= NoteRevealHideMs)
            HideRevealedNote("away");
    }

    // ── the reveal log (D8): content-free, invariant culture, note NUMBER only ──

    /// <summary>D8's reveal line: the note's number and the dwell that revealed it. NEVER the
    /// note's text; the format is a const so the text-literal ratchet records it (category log).</summary>
    private const string RevealShownLineFormat = "NOTE-REVEAL out=shown note={0} ms={1}";

    /// <summary>D8's hide line: the note's number, the visible time and the closed reason set
    /// (away|other|edit|move|close). NEVER the note's text.</summary>
    private const string RevealHiddenLineFormat = "NOTE-REVEAL out=hidden note={0} ms={1} why={2}";

    internal static string RevealShownLine(int number, int dwellMs) =>
        string.Format(CultureInfo.InvariantCulture, RevealShownLineFormat, number, dwellMs);

    internal static string RevealHiddenLine(int number, long visibleMs, string why) =>
        string.Format(CultureInfo.InvariantCulture, RevealHiddenLineFormat, number, visibleMs, why);

    // ── the reveal's test seams ──

    /// <summary>The chip's Undo target's centre (D6): the chip placement the removal will use,
    /// without changing the note. The chip anchors at the reserved card's top-left, so its ↺ is
    /// read from that geometry — a test can park the gaze where the ↺ will be born.</summary>
    internal PhysicalPoint ChipUndoCentreForTest(string noteId)
    {
        var note = FindNote(noteId) ?? throw new InvalidOperationException($"no note {noteId}");
        var chip = ChipPlacementFor(note);
        var (lx, ly) = NoteLayout.ButtonCenter(chip, 0, NoteState.PendingUndo);
        return new PhysicalPoint(
            DpiHelper.LogicalToPhysicalX(chip.Window.Left + lx),
            DpiHelper.LogicalToPhysicalY(chip.Window.Top + ly));
    }

    /// <summary>The hidden-reveal clock's reachable outside time (D5's test seam), in ms.</summary>
    internal double RevealOutsideMsForTest => _revealOutsideMs;

    /// <summary>The one revealed note's id, or null (D5: at most one).</summary>
    internal string? RevealedNoteIdForTest => _revealedNoteId;

    /// <summary>Whether <paramref name="noteId"/> is currently revealed.</summary>
    internal bool IsRevealedForTest(string noteId) => IsPresentedRevealed(noteId);

    /// <summary>The note's currently DRAWN card rect in screen DIP (D7): the at-rest top part, or
    /// the revealed/grown rect. The chrome zones and this seam read the same answer.</summary>
    internal Rect DrawnCardForTest(string noteId)
    {
        if (FindNote(noteId) is not { } note || !_placements.TryGetValue(noteId, out var placement))
            return default;
        if (note.State is NoteState.Edit or NoteState.Move or NoteState.PendingUndo)
            return placement.Card;
        bool expanded = IsPresentedRevealed(noteId);
        return NoteLayout.DrawnCardRect(placement, RestCardSizeFor(note), expanded);
    }

    /// <summary>The reveal target's rect in screen DIP, or null when the note has none.</summary>
    internal Rect? RevealTargetForTest(string noteId)
    {
        var target = _revealDetector.Targets.FirstOrDefault(t => t.Id == $"{noteId}:reveal");
        if (target?.Bounds is not { } bounds)
            return null;
        return PhysicalRectToDip(bounds);
    }

    /// <summary>The reveal target's accumulated dwell progress (0..1): fixup-01 F8's pin reads
    /// it to prove the gate recovery reset the reveal detector.</summary>
    internal double RevealProgressForTest(string noteId) =>
        _revealDetector.GetProgress($"{noteId}:reveal");

    /// <summary>Whether a chip's Undo target has left the fired ✕'s cell belt (fixup-01 F2):
    /// true when it is not disarmed by the close-cell clock.</summary>
    internal bool ChipUndoArmedForTest(string noteId) => !_undoCloseCellArming.ContainsKey(noteId);

    // Post-effect publish (spec D8): the note action runs first — including the malformed-id
    // early return, whose no-op-cue trade-off is ACCEPTED (spec §Pause) — then the cue fires.
    // Hoisted to a named method (not a straight method-group to OnTargetActivated) so Dispose
    // can -= the same handler it +='d.
    private void OnDetectorTargetActivated(string targetId)
    {
        LogFiredControl(targetId);
        OnTargetActivated(targetId);
        _messenger.Send(new DwellActivationMessage());
    }

    /// <summary>
    /// A4: one Information line per fired note control, written BEFORE the action runs — the
    /// action may remove the note (✕ → chip) or flip the state, and the number the line needs is
    /// still there now. Only the DETECTOR's activations call this: a caretaker touch or click has
    /// no dwell and no gaze distance to report, so it is not a misfire signal.
    /// <para>
    /// The distance is the CURRENT sample's (the one that completed the dwell) to the registered
    /// target's centre, converted to whole DIP; the dwell is the target's effective threshold
    /// (adaptation may lengthen it). Both are integers by construction — see
    /// <see cref="NoteControlFiredLogFormat"/> for why.
    /// </para>
    /// </summary>
    private void LogFiredControl(string targetId)
    {
        if (_logger is null) return;
        var parts = targetId.Split(':');
        if (parts.Length != 2) return;
        var (noteId, control) = (parts[0], parts[1]);
        if (Array.IndexOf(LoggedControls, control) < 0) return;
        var note = FindNote(noteId);
        if (note is null) return;
        var target = FindTarget(targetId);
        if (target is null) return;

        double dx = DpiHelper.PhysicalToLogicalX(target.Center.X)
            - DpiHelper.PhysicalToLogicalX(_lastGazePoint.X);
        double dy = DpiHelper.PhysicalToLogicalY(target.Center.Y)
            - DpiHelper.PhysicalToLogicalY(_lastGazePoint.Y);
        int distanceDip = (int)Math.Round(Math.Sqrt((dx * dx) + (dy * dy)));
        int dwellMs = (int)(target.DwellTimeMicroseconds / MathConstants.MicrosecondsPerMillisecond);
        _logger.LogInformation(NoteControlFiredLogFormat, control, note.Number, distanceDip, dwellMs);
    }

    private void OnTargetActivated(string targetId)
    {
        var parts = targetId.Split(':');
        if (parts.Length != 2) return;
        var (noteId, suffix) = (parts[0], parts[1]);

        switch (suffix)
        {
            case "close": RemoveForUndo(noteId); break;
            case "edit": StartEdit(noteId); break;
            case "move": StartMove(noteId); break;
            // P3 D4: place re-anchors on the ACCEPTED settle's centroid — the dwell started on
            // the Place-here window, and the last gaze point could be anywhere in its radius.
            // Defensive fallback to the last gaze only when a centroid is somehow missing.
            case "place": FinishMove(noteId, _placeCentroid ?? _lastGazePoint); break;
            case "otherside": OtherSideForMoving(noteId); break;
            case "cancel": CancelMove(noteId); break;
            case "done": FinishEdit(noteId, commit: true); break;
            case "discard": FinishEdit(noteId, commit: false); break;
            case "keyboard": ShowKeyboardForEditing(noteId); break;
        }
    }

    private void OnTargetProgress(string targetId, double progress)
    {
        var parts = targetId.Split(':');
        if (parts.Length != 2) return;
        var (noteId, suffix) = (parts[0], parts[1]);
        // Sitting 9: the place disc carries its own arc. The old path handed {id}:place to the
        // NOTE's window, which draws no :place arc, so the dwell on "Hier platzieren" was
        // invisible; route it to the window that actually draws the disc.
        if (suffix == "place")
        {
            _placeWindow?.UpdateDwellProgress(progress);
            return;
        }
        // D: the reveal is not a control; it draws no dwell arc.
        if (suffix == "reveal") return;
        var window = _windows.FirstOrDefault(w => w.NoteId == noteId);
        window?.UpdateDwellProgress(targetId, progress);
    }

    /// <summary>T6 P5: a caretaker touch or click on a drawn control. The decision to act is the
    /// pure <see cref="NoteInputPolicy"/>'s; its inputs are read HERE because the manager owns
    /// them — the registration is the detector's truth (both detectors, so the chip's :undo
    /// qualifies) and visibility is the drawn control's. Acting runs the SAME activation the
    /// dwell path runs — <see cref="OnTargetActivated"/>, or <see cref="OnUndoActivated"/> for
    /// the chip's control — plus the dwell's post-effect cue, so every Undo protection
    /// (✕/Discard) and commit-first (✎) behaves exactly as it does by gaze.</summary>
    internal void RunControlInput(string noteId, string suffix, NoteInputKind kind, bool fromStylus)
    {
        if (_disposed) return;
        var targetId = $"{noteId}:{suffix}";
        bool registered = FindTarget(targetId) is not null;
        var window = Window(noteId);
        bool visible = window?.IsControlDrawn(suffix) ?? false;
        bool paused = _pausePoll?.Invoke() ?? false;
        if (!NoteInputPolicy.Accepts(kind, paused, fromStylus, visible, registered))
            return;

        if (suffix == "undo")
            OnUndoActivated(targetId);
        else
            OnTargetActivated(targetId);
        _messenger.Send(new DwellActivationMessage());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unregister BEFORE detaching the detector events: the registry entry holds the detector
        // through its applier, and a disposing manager must not receive any further routed maps.
        _dwellRegistry?.Unregister(this);
        _dwellDetector.TargetActivated -= OnDetectorTargetActivated;
        _dwellDetector.TargetProgress -= OnTargetProgress;
        // P2 D9 (chip part): detach the undo detector's handlers, and stop and dispose the
        // injected timer — a chip pending at teardown must not keep ticking into a dead manager.
        _undoDetector.TargetActivated -= OnUndoDetectorTargetActivated;
        _undoDetector.TargetProgress -= OnTargetProgress;
        // D: the reveal detector's handlers go too; a dying manager must not reveal.
        _revealDetector.TargetActivated -= OnRevealTargetActivated;
        _revealDetector.TargetProgress -= OnTargetProgress;
        _undoTimer?.Stop();
        _undoTimer?.Dispose();
        _undoTimer = null;
        _messenger.Unregister<GazeCursorMovedMessage>(this);
        // P2 D9 (gate part): the gate's new message inputs go with the manager — a flag update
        // delivered after Dispose must not resurrect state (the handlers also _disposed-guard).
        _messenger.Unregister<EyeCareExerciseActiveMessage>(this);
        _messenger.Unregister<CalibrationStateChangedMessage>(this);
        // T6 (P3 D3): the geometry subscriptions die with the manager.
        _messenger.Unregister<DisplayGeometryChangedMessage>(this);
        SystemParameters.StaticPropertyChanged -= OnSystemPropertyChanged;
        _noteSettings.PropertyChanged -= OnNoteTextSizeChanged;

        // P3 D4: the Place-here window is manager-owned (never in DI) — a teardown closes it.
        _placeWindow?.Close();
        _placeWindow = null;

        // Sitting 9: the Move preview is manager-owned the same way.
        _movePreviewWindow?.Close();
        _movePreviewWindow = null;

        // P4 D4: the capacity notice is manager-owned (never in DI) - a teardown closes it.
        _noticeWindow?.Close();
        _noticeWindow = null;

        // P1 D8: a Dispose mid-Edit tells the keyboard editing is over — the "→ Note" indicator
        // must not survive the manager. Deliberately NO keyboard hide request: the app teardown
        // owns the keyboard window, and a hide fired from a dying manager would race it.
        if (_editingNote is not null)
        {
            _editingNote = null;
            _messenger.Send(new NoteEditingChangedMessage(false));
        }
        if (_keyboardProbe is not null)
            _keyboardProbe.Changed -= OnKeyboardProbeChanged;

        foreach (var window in _windows.ToList())
            window.Close();
        _windows.Clear();
        _notes.Clear();
        _placements.Clear();
        _dwellDetector.ClearTargets();
        _targetWindows.Clear();
        _undoDetector.ClearTargets();
        _undoTargetWindows.Clear();
        // D: the reveal targets and their clock die with the manager.
        _revealDetector.ClearTargets();
        _revealTargetWindows.Clear();
        _revealedNoteId = null;
        _revealOutsideMs = 0;
        _revealLastSampleUs = null;
        _seamRevealedNotes.Clear();
        _revealArming.Clear();
        _undoCloseCellArming.Clear();
        _chipReachedMs.Clear();
        _movingNoteId = null;
        _placeShown = false;
        _placeCentroid = null;
    }
}

