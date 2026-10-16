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
    // Whether the EDITING note owns the eye keyboard (P1 D5): set on every entry into Edit —
    // a visible keyboard hands its ownership over (no hide, no show), a hidden one is summoned.
    private bool _editingOwnsKeyboard;
    // Set when THIS manager asked the keyboard to hide (P1 D5): Done on an owning editor sends
    // KeyboardHideRequestedMessage. A hide that arrives without the flag was foreign (the edge
    // bar's Close, the toolbar toggle) and releases the ownership claim instead.
    private bool _hideRequestedByNote;
    // The probe's IsVisible at the last Changed raise, so the foreign-hide rule reads a transition.
    private bool _keyboardWasVisible;
    private IKeyboardProbe? _keyboardProbe;

    public IReadOnlyList<NoteData> ActiveNotes => _notes.AsReadOnly();
    public bool IsAtCapacity => _windows.Count >= MaxNotes;

    /// <summary>True while some note is in Edit: the keyboard dispatchers route every output
    /// through <see cref="Apply"/> and none of it may become OS input (P1 D1).</summary>
    public bool IsEditing => _editingNote is not null;

    /// <summary>
    /// Diagnostics/testing hook: true when every active note window is currently shown.
    /// Guards against regressions where a note is created but never displayed.
    /// </summary>
    public bool AllWindowsVisible => _windows.All(w => w.IsVisible);

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
        window.Show();
        window.SetText(data.Draft);

        RegisterEditTargets(data.Id);
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

        RegisterEditTargets(id);
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
        _hideRequestedByNote = false;
        bool visible = _keyboardProbe?.IsVisible ?? false;
        _keyboardWasVisible = visible;
        if (!visible)
            _messenger.Send(new KeyboardShowRequestedMessage());
        _editingOwnsKeyboard = true;
        _messenger.Send(new NoteEditingChangedMessage(true));
    }

    /// <summary>
    /// The shared exit from Edit (P1 D4/D8). <paramref name="commit"/> runs the P1 Done table:
    /// a draft with text saves; an emptied EXISTING note restores <see cref="NoteData.PreviousText"/>
    /// (interim — P2 turns this into PendingUndo); a brand-new note whose draft is empty was never
    /// saved and disappears. <paramref name="handOver"/> is the commit-first case: the exit sends
    /// NO NoteEditingChangedMessage and does NOT release the keyboard — ownership moves to the
    /// next editor inside its BeginEditing, so the switch produces exactly one (true) and no
    /// hide/show bounce (P1 D6/D8).
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
                ReleaseKeyboard(handOver);
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
        RaiseTargetsChanged();
        if (!handOver)
            _messenger.Send(new NoteEditingChangedMessage(false));
    }

    /// <summary>
    /// The keyboard release half of an exit (P1 D5): only a keyboard the EDITING note summoned is
    /// hidden, and only when the keyboard is still up. On a hand-over the release never runs —
    /// the ownership moves to the next editor and no hide or show is sent (P1 D8), so fixation
    /// suppression is never released in between.
    /// </summary>
    private void ReleaseKeyboard(bool handOver)
    {
        if (handOver)
            return;
        if (_editingOwnsKeyboard && (_keyboardProbe?.IsVisible ?? false))
        {
            _hideRequestedByNote = true;
            _messenger.Send(new KeyboardHideRequestedMessage());
        }
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
    /// The probe's change handler. A visible→hidden transition is a HIDE: if this manager did not
    /// request it (edge-bar Close, toolbar toggle), it was foreign and the editing note loses its
    /// ownership claim. In both cases the flag resets after the first hidden raise (P1 D5). The
    /// ⌨ Show-keyboard control registers in slot 1 exactly while the keyboard is hidden, so the
    /// raise re-registers the editing targets too.
    /// </summary>
    private void OnKeyboardProbeChanged()
    {
        if (_disposed) return; // the probe may raise once more than the manager's lifetime (P1 Risks)
        bool visible = _keyboardProbe?.IsVisible ?? false;
        bool wasVisible = _keyboardWasVisible;
        _keyboardWasVisible = visible;

        if (!visible && wasVisible)
        {
            bool hideWasOurs = _hideRequestedByNote;
            _hideRequestedByNote = false;
            if (!hideWasOurs)
                _editingOwnsKeyboard = false;
        }

        if (_editingNote is { } note)
        {
            Window(note.Id)?.SetKeyboardControlVisible(!visible);
            RegisterEditTargets(note.Id);
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

        // The keyboard gate (P1 D5): a note target is ineligible for any sample whose gaze lies
        // inside the keyboard's or its edge bar's hit area — dwell has no z-order, and the Done
        // button under the keyboard must never fire while the owner types. The filter drops every
        // target for the gated sample, which also resets its accumulated dwell.
        bool gated = _keyboardProbe is { } probe && probe.Rects().Any(rect => rect.Contains(gazePoint));
        _dwellDetector.Update(gazePoint, timestamp, gated ? NotEligible : null);

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
        RegisterEditTargets(data.Id);
        _dwellDetector.ResetAllState();
        BeginEditing(data);
        return data.Id;
    }

    /// <summary>Test seam: whether the editing note currently owns the keyboard claim (P1 D5's
    /// OwnsKeyboard). The claim has no user-visible P1 difference outside a hand-over, so state
    /// is exposed for the foreign-hide tests instead of inferred.</summary>
    internal bool EditingOwnsKeyboardForTest => _editingOwnsKeyboard;

    /// <summary>Test seam: whether the note's own hide request is still pending (P1 D5's
    /// _hideRequestedByNote, reset by the first hidden probe raise).</summary>
    internal bool HideRequestedByNoteForTest => _hideRequestedByNote;

    /// <summary>
    /// Clears and re-registers dwell targets for every note according to its current state.
    /// Called after a note is removed or returns to Display so surviving notes regain their
    /// controls, then resets accumulated dwell state to avoid a spurious immediate activation.
    /// </summary>
    private void ReRegisterAllTargets()
    {
        _dwellDetector.ClearTargets();
        foreach (var n in _notes)
            RegisterTargetsForState(n);
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
    /// note's card targets are registered (the single-editor rule).
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

        _dwellDetector.ClearTargets();
        _dwellDetector.AddTarget(new DwellTarget($"{noteId}:done", ButtonCenter(0), radiusPhysical, DwellTimeMs));
        if (KeyboardHidden)
            _dwellDetector.AddTarget(new DwellTarget($"{noteId}:keyboard", ButtonCenter(1), radiusPhysical, DwellTimeMs));
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
