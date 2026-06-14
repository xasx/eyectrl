namespace EyeCtrl.App.Services;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Messages;

public class NoteManager : INoteManager, IRecipient<GazeCursorMovedMessage>, IDisposable
{
    private readonly List<NoteWindow> _windows = new();
    private readonly List<NoteData> _notes = new();
    private readonly DwellDetector _dwellDetector = new(cooldownMs: 300);
    private readonly IMessenger _messenger;
    private PhysicalPoint _lastGazePoint;
    private bool _disposed;
    private const int MaxNotes = 8;
    private const int DwellTimeMs = 600;

    public IReadOnlyList<NoteData> ActiveNotes => _notes.AsReadOnly();
    public bool IsAtCapacity => _windows.Count >= MaxNotes;

    /// <summary>
    /// Diagnostics/testing hook: true when every active note window is currently shown.
    /// Guards against regressions where a note is created but never displayed.
    /// </summary>
    public bool AllWindowsVisible => _windows.All(w => w.IsVisible);

    public NoteManager(IMessenger messenger)
    {
        _messenger = messenger;
        _dwellDetector.TargetActivated += OnTargetActivated;
        _dwellDetector.TargetProgress += OnTargetProgress;
        _messenger.Register<GazeCursorMovedMessage>(this);
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
    }

    public void RemoveNote(string id)
    {
        var window = _windows.FirstOrDefault(w => w.NoteId == id);
        var note = _notes.FirstOrDefault(n => n.Id == id);
        if (window != null) { window.Close(); _windows.Remove(window); }
        if (note != null) _notes.Remove(note);
        ReRegisterAllTargets();
    }

    public void StartEdit(string id)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        var window = _windows.FirstOrDefault(w => w.NoteId == id);
        if (note == null || window == null) return;
        note.State = NoteState.Edit;
        window.SetState(NoteState.Edit);
        window.SetText(note.Text);
        window.FocusEditor();
        // Keep only this note's close target active; edit/move removed and other notes frozen.
        _dwellDetector.ClearTargets();
        RegisterDwellTargets(id, registerCloseOnly: true);
        _dwellDetector.ResetAllState();
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
    }

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
    }

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

        // Button centers in window-relative logical coords — must match NoteWindow.DrawButtons.
        double cx = LayoutConstants.NoteWindowWidth - LayoutConstants.NoteButtonPadding - LayoutConstants.NoteButtonRadius;
        double cy0 = LayoutConstants.NoteTitleBarHeight + LayoutConstants.NoteButtonPadding + LayoutConstants.NoteButtonRadius;
        double step = 2 * LayoutConstants.NoteButtonRadius + LayoutConstants.NoteButtonGap;
        double radiusPhysical = DpiHelper.LogicalToPhysicalX(LayoutConstants.NoteButtonRadius);

        PhysicalPoint ButtonCenter(int index) => new(
            DpiHelper.LogicalToPhysicalX(winLeftLogical + cx),
            DpiHelper.LogicalToPhysicalY(winTopLogical + cy0 + index * step));

        _dwellDetector.AddTarget(new DwellTarget($"{noteId}:close", ButtonCenter(0), radiusPhysical, DwellTimeMs));
        if (!registerCloseOnly)
        {
            _dwellDetector.AddTarget(new DwellTarget($"{noteId}:edit", ButtonCenter(1), radiusPhysical, DwellTimeMs));
            _dwellDetector.AddTarget(new DwellTarget($"{noteId}:move", ButtonCenter(2), radiusPhysical, DwellTimeMs));
        }
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

        _dwellDetector.TargetActivated -= OnTargetActivated;
        _dwellDetector.TargetProgress -= OnTargetProgress;
        _messenger.Unregister<GazeCursorMovedMessage>(this);

        foreach (var window in _windows.ToList())
            window.Close();
        _windows.Clear();
        _notes.Clear();
        _dwellDetector.ClearTargets();
    }
}
