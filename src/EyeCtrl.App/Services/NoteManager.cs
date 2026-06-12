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

        window.SetState(NoteState.Edit);
        window.Loaded += (_, _) =>
        {
            window.Left = DpiHelper.PhysicalToLogicalX(point.X) - LayoutConstants.NoteWindowWidth / 2;
            window.Top = DpiHelper.PhysicalToLogicalY(point.Y) - 70;
            window.Show();
            window.SetText(data.Text);
            window.SetOverlayInteraction(clickThrough: false, focusable: true);
            window.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    FinishEdit(data.Id, window.GetText());
                }
                else if (e.Key == System.Windows.Input.Key.Escape)
                {
                    FinishEdit(data.Id, data.Text);
                }
            };
        };
        RegisterDwellTargets(data.Id);
    }

    public void RemoveNote(string id)
    {
        _dwellDetector.ClearTargets();
        var window = _windows.FirstOrDefault(w => w.NoteId == id);
        var note = _notes.FirstOrDefault(n => n.Id == id);
        if (window != null) { window.Close(); _windows.Remove(window); }
        if (note != null) _notes.Remove(note);
        foreach (var n in _notes)
            RegisterDwellTargets(n.Id);
    }

    public void StartEdit(string id)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        var window = _windows.FirstOrDefault(w => w.NoteId == id);
        if (note == null || window == null) return;
        note.State = NoteState.Edit;
        window.SetState(NoteState.Edit);
        window.SetText(note.Text);
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
        _dwellDetector.ResetAllState();
    }

    public void StartMove(string id)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        var window = _windows.FirstOrDefault(w => w.NoteId == id);
        if (note == null || window == null) return;
        note.State = NoteState.Move;
        window.SetState(NoteState.Move);
        _dwellDetector.ClearTargets();
        double physicalPlacementRadius = DpiHelper.LogicalToPhysicalX(100);
        _dwellDetector.AddTarget(new DwellTarget($"{id}:place", note.Position, physicalPlacementRadius, DwellTimeMs));
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
        _dwellDetector.ClearTargets();
        RegisterDwellTargets(id);
        _dwellDetector.ResetAllState();
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
                window.Top = DpiHelper.PhysicalToLogicalY(gazePoint.Y) - 70;
            }
        }
    }

    public void Receive(GazeCursorMovedMessage message)
    {
        OnGazeMoved(message.Position, message.TimestampMicroseconds);
    }

    private void RegisterDwellTargets(string noteId)
    {
        var note = _notes.FirstOrDefault(n => n.Id == noteId);
        if (note == null) return;

        double physicalButtonRadius = DpiHelper.LogicalToPhysicalX(LayoutConstants.NoteButtonRadius);
        double physicalWindowWidth = DpiHelper.LogicalToPhysicalX(LayoutConstants.NoteWindowWidth);
        double physicalGap = DpiHelper.LogicalToPhysicalX(10);
        double physicalTitleHeight = DpiHelper.LogicalToPhysicalY(24);

        double bx = note.Position.X + physicalWindowWidth - physicalButtonRadius;
        double byBase = note.Position.Y + physicalTitleHeight;
        double step = physicalButtonRadius * 2 + physicalGap;

        _dwellDetector.AddTarget(new DwellTarget($"{noteId}:close", new PhysicalPoint(bx, byBase), physicalButtonRadius, DwellTimeMs));
        _dwellDetector.AddTarget(new DwellTarget($"{noteId}:edit", new PhysicalPoint(bx, byBase + step), physicalButtonRadius, DwellTimeMs));
        _dwellDetector.AddTarget(new DwellTarget($"{noteId}:move", new PhysicalPoint(bx, byBase + step * 2), physicalButtonRadius, DwellTimeMs));
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
