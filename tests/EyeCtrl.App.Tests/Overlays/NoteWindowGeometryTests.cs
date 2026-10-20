namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Tests.TestSupport;

using System;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Messages;
using Xunit;

[Collection("DpiHelper Tests (App)")]
public class NoteWindowGeometryTests
{
    private sealed class RecordOnlyUndoTimer : INoteUndoTimer
    {
        public bool IsRunning { get; private set; }
        public void Start(System.Action onTick) => IsRunning = true;
        public void Stop() => IsRunning = false;
        public void Dispose() => Stop();
    }
    // fixup-01 G3: the headless part-2 tests pinned placement through NoteTestBench surfaces;
    // this test pins the REAL window instead, at the user's 2.0 — because the renderer's
    // Left/Top/Width/Height are set from the placement in ApplyPlacementToWindow, and nothing
    // else would catch a regression there. It also checks the drawn button column against
    // NoteLayout.ButtonCenter for the current side, and re-checks both after a T6 re-place:
    // the only three places the user's eye can see when any of them drifts.

    [StaFact]
    public void TheRealWindow_BuildsThePlacement_AndTheButtonColumn_FollowsTheSide()
    {
        var m = new WeakReferenceMessenger();
        var area = new Rect(0, 0, 1920, 1200);
        using var manager = new NoteManager(m, hitTester: InactiveNoteHitTester.Instance, clockMs: () => 0,
            undoTimer: new RecordOnlyUndoTimer(), workAreaDip: () => area);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            manager.CreateNoteAt(new PhysicalPoint(1600, 1000));
            var id = manager.ActiveNotes[^1].Id;
            manager.Apply(new NoteAppend("hello"));
            manager.FinishEdit(id, commit: true);

            PinWindowAndButtons(manager, id);

            // T6: the work area shrinks (the message the geometry watcher broadcast) — the
            // far note flips Right → Left, its window and its drawn buttons must follow.
            area = new Rect(0, 0, 1000, 1200);
            m.Send(new DisplayGeometryChangedMessage(new DisplayGeometry(1000, 1200, 2, 2)));

            Check.WithCustomMessage("the re-placed far note must flip to the Left side")
                .That(manager.PlacementForTest(id)!.Side).IsEqualTo(NoteSide.Left);

            PinWindowAndButtons(manager, id);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    private static void PinWindowAndButtons(NoteManager manager, string id)
    {
        var placement = manager.PlacementForTest(id)!;
        var window = manager.WindowForTest(id)!;

        // The window reports the placement's rect: Width/Height/Left/Top within the half-DIP
        // rounding the 2× DPI transform applies (fixup-04: the tag's protrusion is a fractional
        // ink height, so the window's top strip is no longer a whole number of DIP).
        Check.WithCustomMessage("the window's width must come from the placement's window rect")
            .That(Math.Abs(window.Width - placement.Window.Width)).IsStrictlyLessThan(0.5);
        Check.WithCustomMessage("the window's height must come from the placement's window rect")
            .That(Math.Abs(window.Height - placement.Window.Height)).IsStrictlyLessThan(0.5);
        Check.WithCustomMessage("the window's Left must come from ApplyPlacementToWindow")
            .That(Math.Abs(window.Left - placement.Window.Left)).IsStrictlyLessThan(0.5);
        Check.WithCustomMessage("the window's Top must come from ApplyPlacementToWindow")
            .That(Math.Abs(window.Top - placement.Window.Top)).IsStrictlyLessThan(0.5);

        // The drawn button column (the display set's ✎ ↕ ✕ are slots 0-2, drawn once per side)
        // must sit on NoteLayout.ButtonCenter for the CURRENT side — per-slot, on the even
        // card-contrary column.
        var buttons = window.ButtonsForTest();
        Span<(string Suffix, int Index)> displaySet = [ ("edit", 0), ("move", 1), ("close", 3) ]; // P4 D3: close in the bottom row
        foreach (var (suffix, index) in displaySet)
        {
            Check.WithCustomMessage($"the {suffix} button must exist on the real canvas")
                .That(buttons.ContainsKey($"{id}:{suffix}")).IsTrue();
            var ellipse = buttons[$"{id}:{suffix}"];
            var (centerX, centerY) = NoteLayout.ButtonCenter(placement, index);
            Check.WithCustomMessage($"the {suffix} button must sit on the side's column x")
                .That(Canvas.GetLeft(ellipse)).IsCloseTo(centerX - ellipse.Width / 2, 1e-6);
            Check.WithCustomMessage($"the {suffix} button must sit on the column's y {index}")
                .That(Canvas.GetTop(ellipse)).IsCloseTo(centerY - ellipse.Height / 2, 1e-6);
        }
    }
}
