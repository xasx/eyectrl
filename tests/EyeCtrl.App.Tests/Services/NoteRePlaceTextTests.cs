namespace EyeCtrl.App.Tests.Services;

using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Messages;

[Collection("DpiHelper Tests (App)")]
public class NoteRePlaceTextTests
{
    public NoteRePlaceTextTests() => DpiHelper.Initialize(1.0, 1.0);

    private sealed class Timer : INoteUndoTimer
    {
        public bool IsRunning { get; private set; }
        public void Start(System.Action onTick) => IsRunning = true;
        public void Stop() => IsRunning = false;
        public void Dispose() => Stop();
    }

    private static NoteManager Make(WeakReferenceMessenger m, System.Func<System.Windows.Rect>? area = null) =>
        new(m, hitTester: InactiveNoteHitTester.Instance, clockMs: () => 0, undoTimer: new Timer(),
            workAreaDip: area ?? NoteTestBench.WorkAreaProvider);

    private static string Saved(NoteManager manager, string text, PhysicalPoint anchor)
    {
        manager.CreateNoteAt(anchor);
        var id = manager.ActiveNotes[^1].Id;
        manager.Apply(new NoteAppend(text));
        manager.FinishEdit(id, commit: true);
        return id;
    }

    /// <summary>Reviewer probe R9a (fixup-01 B1): a T6 re-place goes through ApplyPlacement →
    /// Draw → UpdateContent, which used to rebuild the body's text block empty — nothing
    /// re-pushed SetText on the re-place path. The stored body text must survive the redraw.
    /// Proof mutant: drop the `Text = _text` restore in UpdateContent → red here and in
    /// AfterAMove_TheNoteStillShowsItsText.</summary>
    [StaFact]
    public void RePlace_KeepsTheNoteTextVisible()
    {
        var m = new WeakReferenceMessenger();
        var area = new System.Windows.Rect(0, 0, 1920, 1200);
        using var manager = Make(m, () => area);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = Saved(manager, "hello", new PhysicalPoint(1600, 1000));
            Check.That(manager.WindowForTest(id)!.GetText()).IsEqualTo("hello");
            area = new System.Windows.Rect(0, 0, 1000, 1200); // forces a new side
            m.Send(new DisplayGeometryChangedMessage(new DisplayGeometry(1000, 1200, 2, 2)));
            Check.WithCustomMessage("after a T6 re-place the card shows: '" + manager.WindowForTest(id)!.GetText() + "'")
                .That(manager.WindowForTest(id)!.GetText()).IsEqualTo("hello");
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    /// <summary>Reviewer probe R9b (fixup-01 B1): StartMove → FinishMove re-applies the
    /// placement and flips state without re-pushing SetText — the text must show during the
    /// Move and after Place here.</summary>
    [StaFact]
    public void AfterAMove_TheNoteStillShowsItsText()
    {
        var m = new WeakReferenceMessenger();
        using var manager = Make(m);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = Saved(manager, "hello", new PhysicalPoint(1600, 1000));
            manager.StartMove(id);
            var duringMove = manager.WindowForTest(id)!.GetText();
            manager.FinishMove(id, new PhysicalPoint(1000, 800));
            Check.WithCustomMessage($"during Move: '{duringMove}', after Place here: '{manager.WindowForTest(id)!.GetText()}'")
                .That(manager.WindowForTest(id)!.GetText()).IsEqualTo("hello");
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }
}
