namespace EyeCtrl.App.Services;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Messages;

public class NoteManager : INoteManager, IRecipient<GazeCursorMovedMessage>, IDisposable
{
    private readonly List<NoteWindow> _windows = new();
    private readonly List<NoteData> _notes = new();
    private readonly DwellDetector _dwellDetector;
    private readonly IMessenger _messenger;
    private readonly IDwellDetectorRegistry? _dwellRegistry;
    private PhysicalPoint _lastGazePoint;
    private bool _disposed;
    private Func<bool>? _suspendPredicate;
    // True once a sample was dropped by _suspendPredicate. The first sample after the gate opens
    // resets the detector (see Receive).
    private bool _suspendedByPredicate;
    private const int MaxNotes = 8;
    private const int DwellTimeMs = 600;

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

    public IReadOnlyList<NoteData> ActiveNotes => _notes.AsReadOnly();
    public bool IsAtCapacity => _windows.Count >= MaxNotes;

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
        var targets = _dwellDetector.Targets;
        if (targets.Count == 0)
            return Array.Empty<PhysicalRect>();
        var rects = new List<PhysicalRect>(targets.Count);
        foreach (var target in targets)
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

    /// <summary>Test seam (the note-window show seam, moved down by sync 4): whether the manager
    /// puts REAL note windows on the screen. True in production; the App test assembly switches
    /// it off ONCE in its module initializer, because a headless test runs on the OWNER machine
    /// and a topmost note window is exactly what must not appear there. With it off each window
    /// still builds its drawn tree, applies its placement, registers its targets and counts as
    /// displayed (<see cref="NoteWindow.IsDisplayed"/>) - only the HWND show is skipped.</summary>
    internal static bool RealNoteWindows = true;

    public NoteManager(IMessenger messenger, IStruggleTelemetrySink? sink = null,
        IDwellDetectorRegistry? dwellRegistry = null)
    {
        _messenger = messenger;
        _dwellDetector = new DwellDetector(cooldownMs: 300, context: DwellContext.Notes, sink: sink);
        _dwellDetector.TargetActivated += OnDetectorTargetActivated;
        _dwellDetector.TargetProgress += OnTargetProgress;
        _messenger.Register<GazeCursorMovedMessage>(this);
        _dwellRegistry = dwellRegistry;
        _dwellRegistry?.Register(this, DwellRoutingRegistration.ForDetector(
            _dwellDetector, DwellContext.Notes,
            AdaptationDwellRanges.StandardMinDwellMs, AdaptationDwellRanges.StandardMaxDwellMs));
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
    }

    // ── entering and leaving Edit (P1 D4/D8) ──

    public void CreateNoteAt(PhysicalPoint point)
    {
        if (IsAtCapacity) return;
        // Commit-first (P1 D8): a pin while another note edits saves that note first. Nothing
        // is lost, and the keyboard, if up, hands over instead of bouncing.
        ExitEditing(commit: true, handOver: true);

        var data = new NoteData { Position = point, State = NoteState.Edit, PreviousText = null, Draft = string.Empty };
        var window = new NoteWindow(data.Id);
        _notes.Add(data);
        _windows.Add(window);

        // Set the initial state before Show() so the first render builds the editor layout.
        window.SetState(NoteState.Edit);
        window.Left = DpiHelper.PhysicalToLogicalX(point.X) - LayoutConstants.NoteWindowWidth / 2;
        window.Top = DpiHelper.PhysicalToLogicalY(point.Y) - LayoutConstants.NoteAnchorOffsetY;

        // Show() must run here, not inside the window's Loaded handler: Loaded only fires once the
        // window is shown, so a Show() registered inside Loaded would never execute. The ⌨
        // control's drawn visibility is seeded before Show so the first render matches the
        // registered targets.
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
        if (_editingNote?.Id == id)
        {
            // No P1 target reaches this (Edit registers :done/:keyboard, not :close); defensive
            // clean-up so a programmatic removal cannot strand the editing state or the keyboard.
            _editingNote = null;
            ReleaseKeyboard(handOver: false);
            _messenger.Send(new NoteEditingChangedMessage(false));
        }

        var window = _windows.FirstOrDefault(w => w.NoteId == id);
        var note = _notes.FirstOrDefault(n => n.Id == id);
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
        // Commit-first (P1 D8), then hand the keyboard over: if it is up it stays up and its
        // ownership moves; if not, BeginEditing summons it.
        ExitEditing(commit: true, handOver: true);

        note.State = NoteState.Edit;
        note.PreviousText = note.Text;
        note.Draft = note.Text;
        window.SetState(NoteState.Edit);
        window.SetKeyboardControlVisible(KeyboardHidden);
        window.SetText(note.Draft);

        RegisterSoleEditorTargets(id);
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
    /// The shared exit from Edit (P1 D4/D8, fixup M2). <paramref name="commit"/> runs the P1 Done
    /// table: a draft with text saves; an emptied EXISTING note restores <see cref="NoteData.PreviousText"/>
    /// (interim — P2 turns this into PendingUndo); a brand-new note whose draft is empty was never
    /// saved and disappears. EVERY exit — the disappearing new note included — falls through to the
    /// shared tail: release the keyboard, then, unless this is a hand-over, send the one
    /// <see cref="NoteEditingChangedMessage(false)"/>. Skipping it anywhere leaves "→ Note" lit on
    /// a keyboard that is typing into the OS. <paramref name="handOver"/> is the commit-first case:
    /// no message and no release — ownership moves to the next editor inside its BeginEditing, so
    /// the switch produces exactly one (true) and no hide/show bounce (P1 D6/D8).
    /// </summary>
    private void ExitEditing(bool commit, bool handOver)
    {
        if (_editingNote is not { } note)
            return;
        _editingNote = null;

        if (commit)
        {
            var trimmed = NoteText.TrimBlankEdgeLines(note.Draft);
            if (trimmed.Length == 0 && note.PreviousText is null)
            {
                // New note, draft empty (P1 D4 Done table): nothing was ever saved — it disappears.
                RemoveNoteInternal(note);
                if (!handOver)
                    LatchTargetsUnderTheGaze();
                ReleaseKeyboard(handOver);
                if (!handOver)
                    _messenger.Send(new NoteEditingChangedMessage(false));
                return;
            }

            note.Text = trimmed.Length > 0 ? trimmed : note.PreviousText!;
        }

        note.Draft = note.Text;
        note.State = NoteState.Display;
        var window = Window(note.Id);
        if (window != null)
        {
            window.SetState(NoteState.Display);
            window.SetText(note.Text);
        }
        ReleaseKeyboard(handOver);
        ReRegisterAllTargets();
        if (!handOver)
            LatchTargetsUnderTheGaze();
        RaiseTargetsChanged();
        if (!handOver)
            _messenger.Send(new NoteEditingChangedMessage(false));
    }

    /// <summary>
    /// Leave-before-arm for the Edit → Display hand-off (fixups B2/F1/F2; T2 generalises, P2 D4).
    /// ✓ Done and ✕ are the SAME slot-0 circle, and a neighbour pinned close by can put its own
    /// ✕ under the same held gaze — so EVERY registered target that contains the last gaze sample
    /// is latched, not just the finishing note's. A latched target stays ineligible (clearing any
    /// accumulated dwell) until its release rule fires: the gaze continuously outside its circle
    /// for <see cref="LeaveBeforeArmMs"/> — one jitter sample past the edge is noise, not a leave
    /// (fixup F1). Called AFTER <see cref="ReRegisterAllTargets"/> so the scan sees the fresh
    /// targets; a held sample is by definition still inside the circles it must leave.
    /// </summary>
    private void LatchTargetsUnderTheGaze()
    {
        foreach (var target in _dwellDetector.Targets)
        {
            double dx = _lastGazePoint.X - target.Center.X;
            double dy = _lastGazePoint.Y - target.Center.Y;
            if (dx * dx + dy * dy <= (double)target.Radius * target.Radius)
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
            var target = _dwellDetector.Targets.FirstOrDefault(t => t.Id == id);
            if (target is null)
            {
                _targetsAwaitingGazeLeave.Remove(id);
                continue;
            }
            double dx = sample.X - target.Center.X;
            double dy = sample.Y - target.Center.Y;
            if (dx * dx + dy * dy <= (double)target.Radius * target.Radius)
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

    private void RemoveNoteInternal(NoteData note)
    {
        var window = Window(note.Id);
        if (window != null) { window.Close(); _windows.Remove(window); }
        _notes.Remove(note);
        ReRegisterAllTargets();
        RaiseTargetsChanged();
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
            Window(note.Id)?.SetKeyboardControlVisible(!visible); // mutant A's guard (review 01)
            RegisterSoleEditorTargets(note.Id);
            _dwellDetector.ResetAllState();
        }
    }

    /// <summary>The ⌨ dwell: re-summon the keyboard mid-Edit and take ownership again (P1 D8).</summary>
    private void ShowKeyboardForEditing(string noteId)
    {
        if (_editingNote?.Id != noteId) return;
        _messenger.Send(new KeyboardShowRequestedMessage());
        _editingOwnsKeyboard = true;
    }

    public void StartMove(string id)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        var window = _windows.FirstOrDefault(w => w.NoteId == id);
        if (note == null || window == null) return;
        if (_editingNote?.Id == id)
        {
            // No P1 target reaches this (Edit registers no :move); defensive clean-up mirrors
            // RemoveNote so a stray StartMove cannot strand the editing state.
            _editingNote = null;
            ReleaseKeyboard(handOver: false);
            _messenger.Send(new NoteEditingChangedMessage(false));
        }
        note.State = NoteState.Move;
        window.SetState(NoteState.Move);
        // Only the placement target is active while moving; done/keyboard and close/edit/move are
        // removed to prevent accidental activation, and all other notes are frozen.
        _dwellDetector.ClearTargets();
        _dwellDetector.AddTarget(new DwellTarget($"{id}:place", note.Position,
            DpiHelper.LogicalToPhysicalX(LayoutConstants.NotePlacementRadius), DwellTimeMs));
        _dwellDetector.ResetAllState();
        RaiseTargetsChanged();
    }

    public void FinishMove(string id, PhysicalPoint newPosition)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        var window = _windows.FirstOrDefault(w => w.NoteId == id);
        if (note == null || window == null) return;
        note.Position = newPosition;
        note.State = NoteState.Display;
        window.SetState(NoteState.Display);
        ReRegisterAllTargets();
        RaiseTargetsChanged();
    }

    /// <summary>
    /// Broadcasts <see cref="NoteTargetsChangedMessage"/> after every structural change to the
    /// detector's target set (create/remove/state flips) so the D17 inventory re-runs the preview
    /// panel's collision check (review F1). NOT called from the gaze-driven <c>:place</c> centre
    /// updates — see the message's doc for why that would be a per-sample walk.
    /// </summary>
    private void RaiseTargetsChanged() => _messenger.Send(new NoteTargetsChangedMessage());

    public void OnGazeMoved(PhysicalPoint gazePoint, long timestamp)
    {
        _lastGazePoint = gazePoint;

        // The keyboard gate (P1 D5, fixup m4): a note target is ineligible for any sample whose
        // gaze lies inside the keyboard's or its edge bar's hit area — dwell has no z-order, and
        // the Done button under the keyboard must never fire while the owner types. The probe
        // read costs real work (a list, an array, two PointToScreen calls), so it is skipped
        // entirely while the detector holds no targets to protect.
        bool gated = false;
        if (_dwellDetector.Targets.Count > 0 && _keyboardProbe is { } probe)
            gated = probe.Rects().Any(rect => rect.Contains(gazePoint));

        // The leave-before-arm latch (fixups B2/F1): advance the leave timers, then keep the
        // still-latched ones ineligible for this sample (the filter also clears whatever dwell
        // they accumulated while latched). A gated sample carries no leave information — the
        // keyboard's rect covers the spot, so it neither advances nor resets a timer (F4).
        if (_targetsAwaitingGazeLeave.Count > 0 && !gated)
            AdvanceLeaveTimers(gazePoint, timestamp);

        Func<DwellTarget, bool>? filter = null;
        if (_targetsAwaitingGazeLeave.Count > 0)
        {
            var disarmed = _targetsAwaitingGazeLeave;
            filter = t => !gated && !disarmed.ContainsKey(t.Id);
        }
        else if (gated)
        {
            filter = NotEligible;
        }
        _dwellDetector.Update(gazePoint, timestamp, filter);

        foreach (var note in _notes.Where(n => n.State == NoteState.Move))
        {
            _dwellDetector.UpdateTargetCenter($"{note.Id}:place", gazePoint);
            var window = _windows.FirstOrDefault(w => w.NoteId == note.Id);
            if (window != null)
            {
                window.Left = DpiHelper.PhysicalToLogicalX(gazePoint.X) - LayoutConstants.NoteWindowWidth / 2;
                window.Top = DpiHelper.PhysicalToLogicalY(gazePoint.Y) - LayoutConstants.NoteAnchorOffsetY;
            }
        }
    }

    private static bool NotEligible(DwellTarget _) => false;

    public void Receive(GazeCursorMovedMessage message)
    {
        // Held (blink) samples repeat the frozen last valid position: they must not advance a note's
        // close/edit/move dwell, and a note being placed simply stays put until the eyes reopen.
        if (message.IsInGap) return;

        // The probe's gate (build plan C15): while it is open every sample is dropped, so no note
        // target under the probe's opaque window accumulates dwell. On resume the detector is reset
        // — its clock is timestamp-based, so without the reset the first post-probe sample on the
        // same target it left would be credited time it did not spend there, and a note's close is
        // destructive and no-confirm (review 03 F5).
        if (_suspendPredicate?.Invoke() == true)
        {
            _suspendedByPredicate = true;
            return;
        }
        if (_suspendedByPredicate)
        {
            _suspendedByPredicate = false;
            _dwellDetector.ResetAllState();
        }

        OnGazeMoved(message.Position, message.TimestampMicroseconds);
    }

    /// <summary>The probe's suspend seam (build plan C15), configured once from EventCoordinator.
    /// The predicate is evaluated per delivered sample; nothing else changes while it holds.</summary>
    public void SetSuspendPredicate(Func<bool> predicate) =>
        _suspendPredicate = predicate ?? throw new ArgumentNullException(nameof(predicate));

    /// <summary>
    /// Test seam (review 03 F5): registers a Display note's dwell targets without constructing a
    /// <see cref="NoteWindow"/> — <see cref="CreateNoteAt"/> shows and focuses a real window, which
    /// W5(d) must not do. Returns the generated note id.
    /// </summary>
    internal string AddNoteTargetsForTest(PhysicalPoint position)
    {
        var data = new NoteData { Position = position, State = NoteState.Display };
        _notes.Add(data);
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
    internal string AddEditingNoteTargetsForTest(PhysicalPoint position)
    {
        ExitEditing(commit: true, handOver: true);
        var data = new NoteData { Position = position, State = NoteState.Edit, PreviousText = null, Draft = string.Empty };
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

    /// <summary>Test seam: the live window of a note, or null. The drawn-vs-registered ⌨ check
    /// (review 01 mutant A) needs the real window the manager drives on probe changes.</summary>
    internal NoteWindow? WindowForTest(string noteId) => Window(noteId);

    /// <summary>
    /// Clears and re-registers dwell targets for every note according to its current state.
    /// Called after a note is removed or returns to Display so surviving notes regain their
    /// controls, then resets accumulated dwell state to avoid a spurious immediate activation.
    /// </summary>
    private void ReRegisterAllTargets()
    {
        _dwellDetector.ClearTargets();
        if (_editingNote is { } editor)
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
    }

    private void RegisterTargetsForState(NoteData note)
    {
        switch (note.State)
        {
            case NoteState.Edit:
                RegisterEditTargets(note.Id);
                break;
            case NoteState.Move:
                _dwellDetector.AddTarget(new DwellTarget($"{note.Id}:place", note.Position,
                    DpiHelper.LogicalToPhysicalX(LayoutConstants.NotePlacementRadius), DwellTimeMs));
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
    /// The Edit state's targets (P1 D4/D7): ✓ Done in slot 0 always, ⌨ Show-keyboard in slot 1
    /// only while the keyboard is hidden, slot 2 empty (P2 puts Discard there). There is NO
    /// :close, :edit or :move while editing — Done is the only Edit exit in P1, and no other
    /// note's card targets are registered (the single-editor rule). A pure PER-NOTE registrar
    /// (fixup n3): it adds this note's targets and touches nothing else — a ClearTargets hidden
    /// in here would wipe other notes' targets depending on <see cref="ReRegisterAllTargets"/>'s
    /// loop order. Callers that need a full replacement use <see cref="RegisterSoleEditorTargets"/>.
    /// </summary>
    private void RegisterEditTargets(string noteId)
    {
        var note = _notes.FirstOrDefault(n => n.Id == noteId);
        if (note == null) return;

        // Window top-left in logical screen coords — must match CreateNoteAt positioning.
        double winLeftLogical = DpiHelper.PhysicalToLogicalX(note.Position.X) - LayoutConstants.NoteWindowWidth / 2;
        double winTopLogical = DpiHelper.PhysicalToLogicalY(note.Position.Y) - LayoutConstants.NoteAnchorOffsetY;

        double radiusPhysical = DpiHelper.LogicalToPhysicalX(LayoutConstants.NoteButtonRadius);

        PhysicalPoint ButtonCenter(int index)
        {
            var (lx, ly) = NoteLayout.ButtonCenter(index);
            return new(
                DpiHelper.LogicalToPhysicalX(winLeftLogical + lx),
                DpiHelper.LogicalToPhysicalY(winTopLogical + ly));
        }

        _dwellDetector.AddTarget(new DwellTarget($"{noteId}:done", ButtonCenter(0), radiusPhysical, DwellTimeMs));
        if (KeyboardHidden)
            _dwellDetector.AddTarget(new DwellTarget($"{noteId}:keyboard", ButtonCenter(1), radiusPhysical, DwellTimeMs));
    }

    /// <summary>The single editor's set is the ONLY live set (P1 D8), so full replacement:
    /// clear, then register. Used by every entry into Edit and by the probe's re-register.</summary>
    private void RegisterSoleEditorTargets(string noteId)
    {
        _dwellDetector.ClearTargets();
        RegisterEditTargets(noteId);
    }

    private void RegisterDwellTargets(string noteId)
    {
        var note = _notes.FirstOrDefault(n => n.Id == noteId);
        if (note == null) return;

        // Window top-left in logical screen coords — must match CreateNoteAt/OnGazeMoved positioning.
        double winLeftLogical = DpiHelper.PhysicalToLogicalX(note.Position.X) - LayoutConstants.NoteWindowWidth / 2;
        double winTopLogical = DpiHelper.PhysicalToLogicalY(note.Position.Y) - LayoutConstants.NoteAnchorOffsetY;

        double radiusPhysical = DpiHelper.LogicalToPhysicalX(LayoutConstants.NoteButtonRadius);

        // Button centers come from NoteLayout (window-relative logical coords), shared with
        // NoteWindow.DrawButtons so the dwell targets can't drift from the drawn buttons.
        PhysicalPoint ButtonCenter(int index)
        {
            var (lx, ly) = NoteLayout.ButtonCenter(index);
            return new(
                DpiHelper.LogicalToPhysicalX(winLeftLogical + lx),
                DpiHelper.LogicalToPhysicalY(winTopLogical + ly));
        }

        _dwellDetector.AddTarget(new DwellTarget($"{noteId}:close", ButtonCenter(0), radiusPhysical, DwellTimeMs));
        _dwellDetector.AddTarget(new DwellTarget($"{noteId}:edit", ButtonCenter(1), radiusPhysical, DwellTimeMs));
        _dwellDetector.AddTarget(new DwellTarget($"{noteId}:move", ButtonCenter(2), radiusPhysical, DwellTimeMs));
    }

    private NoteWindow? Window(string noteId) => _windows.FirstOrDefault(w => w.NoteId == noteId);

    // Post-effect publish (spec D8): the note action runs first — including the malformed-id
    // early return, whose no-op-cue trade-off is ACCEPTED (spec §Pause) — then the cue fires.
    // Hoisted to a named method (not a straight method-group to OnTargetActivated) so Dispose
    // can -= the same handler it +='d.
    private void OnDetectorTargetActivated(string targetId)
    {
        OnTargetActivated(targetId);
        _messenger.Send(new DwellActivationMessage());
    }

    private void OnTargetActivated(string targetId)
    {
        var parts = targetId.Split(':');
        if (parts.Length != 2) return;
        var (noteId, suffix) = (parts[0], parts[1]);

        switch (suffix)
        {
            case "close": RemoveNote(noteId); break;
            case "edit": StartEdit(noteId); break;
            case "move": StartMove(noteId); break;
            case "place": FinishMove(noteId, _lastGazePoint); break;
            case "done": FinishEdit(noteId, commit: true); break;
            case "keyboard": ShowKeyboardForEditing(noteId); break;
        }
    }

    private void OnTargetProgress(string targetId, double progress)
    {
        var parts = targetId.Split(':');
        if (parts.Length != 2) return;
        var (noteId, _) = (parts[0], parts[1]);
        var window = _windows.FirstOrDefault(w => w.NoteId == noteId);
        window?.UpdateDwellProgress(targetId, progress);
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
        _messenger.Unregister<GazeCursorMovedMessage>(this);

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
        _dwellDetector.ClearTargets();
    }
}
