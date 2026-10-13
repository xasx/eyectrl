namespace EyeCtrl.App.Services;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
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
    private const int MaxNotes = 8;
    private const int DwellTimeMs = 600;

    // The window that held the OS foreground before a note edit grabbed it. FocusEditor
    // activates the note window; without a restore, FinishEdit leaves the (now display-mode)
    // note holding focus — gaze-typed text would keep landing in the note instead of the app
    // the user was in. Restored via SetForegroundWindow: at FinishEdit our own process IS the
    // foreground process, so the foreground-lock restriction does not apply.
    private IntPtr _preEditForegroundWindow;

    public IReadOnlyList<NoteData> ActiveNotes => _notes.AsReadOnly();
    public bool IsAtCapacity => _windows.Count >= MaxNotes;

    /// <summary>
    /// Diagnostics/testing hook: true when every active note window is currently shown.
    /// Guards against regressions where a note is created but never displayed.
    /// </summary>
    public bool AllWindowsVisible => _windows.All(w => w.IsVisible);

    /// <summary>
    /// The live note dwell-target rects, physical px (the D17 inventory's sixth source, review F1):
    /// the surfaces this manager's own detector is actually feeding — a note's close/edit/move
    /// buttons in display state, its close button while editing, its <c>:place</c> target while
    /// moving. Reading the DETECTOR's snapshot (not re-deriving from note state) means the
    /// inventory can never disagree with what gaze is really evaluated against. Rect targets
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

    public void CreateNoteAt(PhysicalPoint point)
    {
        if (IsAtCapacity) return;

        var data = new NoteData { Position = point, State = NoteState.Edit };
        var window = new NoteWindow(data.Id);
        _notes.Add(data);
        _windows.Add(window);

        // Set the initial state before Show() so the first render builds the editor TextBox.
        window.SetState(NoteState.Edit);
        window.Left = DpiHelper.PhysicalToLogicalX(point.X) - LayoutConstants.NoteWindowWidth / 2;
        window.Top = DpiHelper.PhysicalToLogicalY(point.Y) - LayoutConstants.NoteAnchorOffsetY;

        // PreviewKeyDown (tunneling) catches Enter/Escape before the multiline TextBox consumes
        // them, so Enter reliably saves and Escape cancels regardless of TextBox key handling.
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                e.Handled = true;
                FinishEdit(data.Id, window.GetText());
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                e.Handled = true;
                FinishEdit(data.Id, data.Text); // cancel, restore original
            }
        };

        // Show() must run here, not inside the window's Loaded handler: Loaded only fires once the
        // window is shown, so a Show() registered inside Loaded would never execute.
        window.Show();
        window.SetText(data.Text);
        window.SetOverlayInteraction(clickThrough: false, focusable: true);
        window.FocusEditor();

        // While editing, only the close (discard) control is dwellable and other notes are frozen.
        // Saving is via the Enter key; discarding is via Escape or the close button.
        _dwellDetector.ClearTargets();
        RegisterDwellTargets(data.Id, registerCloseOnly: true);
        _dwellDetector.ResetAllState();
        RaiseTargetsChanged();
    }

    public void RemoveNote(string id)
    {
        var window = _windows.FirstOrDefault(w => w.NoteId == id);
        var note = _notes.FirstOrDefault(n => n.Id == id);
        if (window != null) { window.Close(); _windows.Remove(window); }
        if (note != null) _notes.Remove(note);
        ReRegisterAllTargets();
        RaiseTargetsChanged();
    }

    public void StartEdit(string id)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        var window = _windows.FirstOrDefault(w => w.NoteId == id);
        if (note == null || window == null) return;
        note.State = NoteState.Edit;
        window.SetState(NoteState.Edit);
        window.SetText(note.Text);
        _preEditForegroundWindow = GetForegroundWindow();
        window.FocusEditor();
        // Keep only this note's close target active; edit/move removed and other notes frozen.
        _dwellDetector.ClearTargets();
        RegisterDwellTargets(id, registerCloseOnly: true);
        _dwellDetector.ResetAllState();
        RaiseTargetsChanged();
    }

    public void FinishEdit(string id, string text)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        var window = _windows.FirstOrDefault(w => w.NoteId == id);
        if (note == null || window == null) return;
        note.Text = text;
        note.State = NoteState.Display;
        window.SetState(NoteState.Display);
        window.SetText(text);
        ReRegisterAllTargets();
        RaiseTargetsChanged();

        // Give the OS foreground back to whatever the user was in before the edit took it.
        if (_preEditForegroundWindow != IntPtr.Zero)
        {
            try { SetForegroundWindow(_preEditForegroundWindow); }
            catch { /* best-effort: a closed target window just leaves focus where it is */ }
            _preEditForegroundWindow = IntPtr.Zero;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public void StartMove(string id)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        var window = _windows.FirstOrDefault(w => w.NoteId == id);
        if (note == null || window == null) return;
        note.State = NoteState.Move;
        window.SetState(NoteState.Move);
        // Only the placement target is active while moving; close/edit/move are removed to
        // prevent accidental activation, and all other notes are frozen.
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
        _dwellDetector.Update(gazePoint, timestamp);

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

    public void Receive(GazeCursorMovedMessage message)
    {
        // Held (blink) samples repeat the frozen last valid position: they must not advance a note's
        // close/edit/move dwell, and a note being placed simply stays put until the eyes reopen.
        if (message.IsInGap) return;
        OnGazeMoved(message.Position, message.TimestampMicroseconds);
    }

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
                RegisterDwellTargets(note.Id, registerCloseOnly: true);
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

    private void RegisterDwellTargets(string noteId, bool registerCloseOnly = false)
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
        if (!registerCloseOnly)
        {
            _dwellDetector.AddTarget(new DwellTarget($"{noteId}:edit", ButtonCenter(1), radiusPhysical, DwellTimeMs));
            _dwellDetector.AddTarget(new DwellTarget($"{noteId}:move", ButtonCenter(2), radiusPhysical, DwellTimeMs));
        }
    }

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

        foreach (var window in _windows.ToList())
            window.Close();
        _windows.Clear();
        _notes.Clear();
        _dwellDetector.ClearTargets();
    }
}
