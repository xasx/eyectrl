namespace EyeCtrl.App.Tests.Services;

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// Review 01's regression probes (fixup 01), adopted verbatim in intent: R1 pins B1 (the
/// Edit → Display transition must leave the Display controls drawn), R2 pins B2 (a gaze
/// lingering on Done must not delete the saved note through the ✕ in the same slot), R3 pins
/// M2 (Done on an empty new note must still tell the keyboard editing ended), R4 pins M1
/// (Done must not hide a keyboard this entry never summoned).
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class Review01ProbeTests
{
    public Review01ProbeTests() => DpiHelper.Initialize(1.0, 1.0);

    [StaFact]
    public void R1_EditThenDisplay_DrawsTheDisplayGlyphs()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new NoteWindow("rv-note");
        window.SetState(NoteState.Edit);
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        window.SetState(NoteState.Display);

        var visible = Blocks((Canvas)window.Content)
            .Where(t => t.Visibility == Visibility.Visible)
            .Select(t => t.Text).ToList();
        Check.WithCustomMessage("visible texts after Edit->Display: " + string.Join("|", visible))
            .That(visible).Contains("✕", "✎", "↕");
    }

    [StaFact]
    public void R2_GazeLingeringOnDone_ThenRemovesTheSavedNote()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);
        var probe = new NoteManagerTests.FakeKeyboardProbe { IsVisible = false };
        manager.SetKeyboardProbe(probe);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            manager.AddEditingNoteTargetsForTest(anchor);
            manager.Apply(new NoteAppend("hello"));
            var slot0 = Centre(anchor, 0);
            long t = 0;
            for (; t <= 700_000; t += 33_000) manager.OnGazeMoved(slot0, t);
            Check.That(manager.IsEditing).IsFalse();
            Check.That(manager.ActiveNotes).HasSize(1);
            // The owner keeps looking at the same spot for one more second.
            for (; t <= 1_800_000; t += 33_000) manager.OnGazeMoved(slot0, t);
            Check.WithCustomMessage("the saved note was removed by the lingering gaze")
                .That(manager.ActiveNotes).HasSize(1);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void R3_DoneOnEmptyNewNote_SendsEditingFalse()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);
        int editingFalse = 0;
        messenger.Register<object, NoteEditingChangedMessage>(this, (_, m) => { if (!m.Editing) editingFalse++; });
        var id = manager.AddEditingNoteTargetsForTest(new PhysicalPoint(0, 0));
        manager.FinishEdit(id, commit: true);
        Check.That(manager.ActiveNotes).IsEmpty();
        Check.WithCustomMessage("empty-new Done never told the keyboard editing ended")
            .That(editingFalse).IsEqualTo(1);
    }

    [StaFact]
    public void R4_EditEnteredWithUsersKeyboardUp_DoneDoesNotHideIt()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);
        var probe = new NoteManagerTests.FakeKeyboardProbe { IsVisible = true }; // the owner opened it
        manager.SetKeyboardProbe(probe);
        int hides = 0;
        messenger.Register<object, KeyboardHideRequestedMessage>(this, (_, _) => hides++);
        var id = manager.AddEditingNoteTargetsForTest(new PhysicalPoint(0, 0));
        manager.Apply(new NoteAppend("x"));
        manager.FinishEdit(id, commit: true);
        Check.WithCustomMessage("Done hid a keyboard the note did not open")
            .That(hides).IsEqualTo(0);
    }

    private static PhysicalPoint Centre(PhysicalPoint anchor, int slot)
    {
        double l = DpiHelper.PhysicalToLogicalX(anchor.X) - EyeCtrl.Core.LayoutConstants.NoteWindowWidth / 2;
        double tp = DpiHelper.PhysicalToLogicalY(anchor.Y) - EyeCtrl.Core.LayoutConstants.NoteAnchorOffsetY;
        var (lx, ly) = NoteLayout.ButtonCenter(slot);
        return new(DpiHelper.LogicalToPhysicalX(l + lx), DpiHelper.LogicalToPhysicalY(tp + ly));
    }

    private static IEnumerable<TextBlock> Blocks(DependencyObject root)
    {
        if (root is TextBlock tb) { yield return tb; yield break; }
        if (root is Panel p)
            foreach (UIElement c in p.Children) foreach (var d in Blocks(c)) yield return d;
        else if (root is Border { Child: { } bc })
            foreach (var d in Blocks(bc)) yield return d;
    }
}
