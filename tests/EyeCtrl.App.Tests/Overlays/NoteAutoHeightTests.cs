namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Tests.TestSupport;

using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// P4 D2 acceptance: the card grows with its text (auto-height), the cap clips it, and every
/// real height change fires T7 exactly once. The window is the measurement instrument (its
/// MeasureTextHeightDip is the same measure the manager derives the placement from), and the
/// targets-changed raise is T7's observable.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class NoteAutoHeightTests
{
    private sealed class CountingProbe
    {
        public int TargetsChanged;
        public CountingProbe(WeakReferenceMessenger messenger)
        {
            messenger.Register<CountingProbe, NoteTargetsChangedMessage>(this, (_, _) => TargetsChanged++);
        }
    }

    private static (NoteManager Manager, string Id, NoteWindow Window, WeakReferenceMessenger Messenger) SavedNoteWith(
        string text, PhysicalPoint? anchor = null, NoteSettings? settings = null)
    {
        var messenger = new WeakReferenceMessenger();
        var area = NoteTestBench.WorkArea;
        var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider, noteSettings: settings);
        DpiHelper.Initialize(2.0, 2.0);
        manager.CreateNoteAt(anchor ?? new PhysicalPoint(1600, 1000));
        var id = manager.ActiveNotes[^1].Id;
        if (text.Length > 0)
        {
            manager.Apply(new NoteAppend(text));
            manager.FinishEdit(id, commit: true);
        }
        return (manager, id, manager.WindowForTest(id)!, messenger);
    }

    [StaFact]
    public void CardHeights_TrackTheText_WithTheColumnFloorAndTheCap()
    {
        var (manager, id, window, _) = SavedNoteWith("one line");
        try
        {
            double size = NoteSettings.DefaultTextSizeDip;
            double workHeight = NoteTestBench.WorkArea.Height;

            double oneLine = window.MeasureTextHeightDip("one line");
            double expectedOne = NoteLayout.CardHeightDip(size, oneLine, workHeight);
            Check.WithCustomMessage($"one-line card height (floor {NoteLayout.ControlsColumnHeightDip})")
                .That(manager.PlacementForTest(id)!.Card.Height).IsEqualTo(expectedOne);
            Check.That(expectedOne).IsEqualTo(NoteLayout.ControlsColumnHeightDip); // the column is the floor

            // Ten lines clear the control-column floor (about nine lines) but stay under the cap.
            var tenLines = string.Join("\n", Enumerable.Repeat("line", 10));
            manager.StartEdit(id);
            manager.Apply(new NoteAppend(tenLines));
            manager.FinishEdit(id, commit: true);
            double ten = window.MeasureTextHeightDip(tenLines);
            Check.WithCustomMessage("ten-line card height")
                .That(manager.PlacementForTest(id)!.Card.Height).IsEqualTo(NoteLayout.CardHeightDip(size, ten, workHeight));
            Check.That(manager.PlacementForTest(id)!.Card.Height).IsStrictlyGreaterThan(expectedOne);

            // Over the cap: 40 lines far exceed min(55% of 1200, 12 lines at 28) = 420 DIP.
            var many = string.Join("\n", Enumerable.Repeat("line", 40));
            manager.StartEdit(id);
            manager.Apply(new NoteAppend(many));
            manager.FinishEdit(id, commit: true);
            double cap = NoteLayout.TextCapHeightDip(workHeight, size);
            Check.WithCustomMessage("the over-cap card stops at the cap plus padding")
                .That(manager.PlacementForTest(id)!.Card.Height).IsEqualTo(cap + 2 * LayoutConstants.NoteButtonPadding);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); manager.Dispose(); }
    }

    [StaFact]
    public void FadeAndEllipsis_AppearOnlyOverTheCap_InDisplay()
    {
        var (manager, id, window, _) = SavedNoteWith(string.Join("\n", Enumerable.Repeat("line", 5)));
        try
        {
            Check.WithCustomMessage("under the cap there is no clipping")
                .That(window.OverCapForTest).IsFalse();
            Check.That(window.OverCapFadeForTest).IsFalse();
            Check.That(window.GetText()).DoesNotContain("…");

            manager.StartEdit(id);
            manager.Apply(new NoteAppend(string.Join("\n", Enumerable.Repeat("line", 40))));
            manager.FinishEdit(id, commit: true);

            Check.WithCustomMessage("over the cap Display clips")
                .That(window.OverCapForTest).IsTrue();
            Check.WithCustomMessage("over the cap Display fades")
                .That(window.OverCapFadeForTest).IsTrue();
            Check.WithCustomMessage("the ellipsis ends the shown text")
                .That(window.GetText().EndsWith("…", StringComparison.Ordinal)).IsTrue();
            // The full text stays in the note data.
            Check.That(manager.ActiveNotes[0].Text).Contains("line");
        }
        finally { DpiHelper.Initialize(1.0, 1.0); manager.Dispose(); }
    }

    [StaFact]
    public void Edit_OverTheCap_ShowsTheEndOfTheDraft_NoFadeNoEllipsis()
    {
        var tail = string.Join("\n", Enumerable.Repeat("tail", 6));
        var (manager, id, window, _) = SavedNoteWith("head " + tail);
        try
        {
            manager.StartEdit(id);
            manager.Apply(new NoteAppend(string.Join("\n", Enumerable.Repeat("more", 40))));

            Check.WithCustomMessage("over the cap Edit clips too")
                .That(window.OverCapForTest).IsTrue();
            Check.WithCustomMessage("Edit never fades")
                .That(window.OverCapFadeForTest).IsFalse();
            var shown = window.GetText();
            Check.WithCustomMessage($"Edit shows the tail of the draft (shown ends: ...{shown[^40..]})")
                .That(shown.EndsWith("more", StringComparison.Ordinal)).IsTrue();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); manager.Dispose(); }
    }

    [StaFact]
    public void AHeightChange_FiresT7_ExactlyOnce_PerActualChange()
    {
        var messenger = new WeakReferenceMessenger();
        var area = NoteTestBench.WorkArea;
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            manager.CreateNoteAt(new PhysicalPoint(1600, 1000));
            var id = manager.ActiveNotes[0].Id;
            var window = manager.WindowForTest(id)!;

            var probe = new CountingProbe(messenger);

            // Type 'M's one keystroke at a time; every placement-height change must raise the
            // targets-changed message exactly once (and nothing else may).
            int heightChanges = 0;
            double last = manager.PlacementForTest(id)!.Card.Height;
            for (int i = 0; i < 400; i++)
            {
                manager.Apply(new NoteAppend("M"));
                var now = manager.PlacementForTest(id)!.Card.Height;
                if (now != last)
                {
                    heightChanges++;
                    last = now;
                }
            }

            Check.WithCustomMessage($"T7 fired {probe.TargetsChanged} for {heightChanges} real height changes")
                .That(probe.TargetsChanged).IsEqualTo(heightChanges);
            Check.WithCustomMessage("400 Ms at the default size must cross the control-column floor")
                .That(heightChanges).IsStrictlyGreaterThan(0);
            // Over the cap the SHOWN text clips; the draft keeps every keystroke.
            Check.That(manager.ActiveNotes[0].Draft).IsEqualTo(new string('M', 400));
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }
}
