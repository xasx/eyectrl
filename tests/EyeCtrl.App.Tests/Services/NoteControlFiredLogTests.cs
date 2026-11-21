namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.Logging;
using NFluent;
using Xunit;

/// <summary>
/// A4 (sitting 10): one Information line per FIRED note control — the control's closed id, the
/// note's number, the gaze's distance from the control's centre in WHOLE DIP and the dwell the
/// target required. The line is content-free: no note text, no word, no position. A caretaker
/// touch/click has no dwell and no gaze distance and is not a misfire signal, so it does not log.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class NoteControlFiredLogTests
{
    private const int TextSize = 28;

    private static (NoteManager Manager, Capture Logger) MakeManager()
    {
        var logger = new Capture();
        var messenger = new WeakReferenceMessenger();
        var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider,
            noteSettings: new NoteSettings { TextSizeDip = TextSize },
            logger: logger);
        manager.SetKeyboardProbe(new NoteManagerTests.FakeKeyboardProbe { IsVisible = false });
        return (manager, logger);
    }

    [StaFact]
    public void DoneFired_LogsOneInformationLine_WithTheControlNumberDistanceAndDwell()
    {
        var (manager, logger) = MakeManager();
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            manager.CreateNoteAt(new PhysicalPoint(1600, 1000));
            var id = manager.ActiveNotes[0].Id;
            manager.Apply(new NoteAppend("x"));
            var done = NoteTestBench.CellCentre(manager, id, 0); // the Edit row's ✓
            ArmThenHold(manager, done, 0);

            Check.That(manager.IsEditing).IsFalse();
            Check.That(logger.Lines).HasSize(1);
            var (level, line) = logger.Lines[0];
            Check.That(level).IsEqualTo(LogLevel.Information);
            Check.WithCustomMessage($"the line must name the control, the number, the distance and the dwell: {line}")
                .That(line).Contains("control=done");
            Check.That(line).Contains("note=1");
            Check.That(line).Contains("distDip=0");
            Check.That(line).Contains("dwellMs=600");
        }
        finally { DpiHelper.Initialize(1.0, 1.0); manager.Dispose(); }
    }

    [StaFact]
    public void TheLine_NeverCarriesTheNotesText()
    {
        var (manager, logger) = MakeManager();
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            manager.CreateNoteAt(new PhysicalPoint(1600, 1000));
            var id = manager.ActiveNotes[0].Id;
            manager.Apply(new NoteAppend("SECRET-WORD"));
            ArmThenHold(manager, NoteTestBench.CellCentre(manager, id, 0), 0);

            var line = logger.Lines.Single().Message;
            Check.WithCustomMessage($"the A4 line must never carry note text: {line}")
                .That(line.Contains("SECRET", StringComparison.Ordinal)).IsFalse();
            Check.That(line.Contains("SECRET-WORD", StringComparison.Ordinal)).IsFalse();
            Check.That(line).Contains("control=done");
        }
        finally { DpiHelper.Initialize(1.0, 1.0); manager.Dispose(); }
    }

    [StaFact]
    public void AnOffCentreDwell_LogsTheDistanceInWholeDip()
    {
        var (manager, logger) = MakeManager();
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            manager.CreateNoteAt(new PhysicalPoint(1600, 1000));
            var id = manager.ActiveNotes[0].Id;
            manager.Apply(new NoteAppend("x"));
            var done = NoteTestBench.CellCentre(manager, id, 0);
            // 30 DIP right of the cell centre: still inside the 56 DIP half-width.
            ArmThenHold(manager, new PhysicalPoint(done.X + (30 * 2), done.Y), 0);

            Check.That(manager.IsEditing).IsFalse();
            var line = logger.Lines.Single().Message;
            Check.WithCustomMessage($"a 30 DIP off-centre dwell must log 30: {line}")
                .That(line).Contains("distDip=30");
        }
        finally { DpiHelper.Initialize(1.0, 1.0); manager.Dispose(); }
    }

    [StaFact]
    public void CloseAndUndo_UseTheirClosedControlTokens()
    {
        var (manager, logger) = MakeManager();
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            manager.CreateNoteAt(new PhysicalPoint(1600, 1000));
            var id = manager.ActiveNotes[0].Id;
            manager.Apply(new NoteAppend("saved"));
            manager.FinishEdit(id, commit: true); // Display: ✎ ↕ ✕

            ArmThenHold(manager, NoteTestBench.CellCentre(manager, id, 3), 0); // ✕ → chip
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            Check.WithCustomMessage("the ✕ dwell must log control=close")
                .That(logger.Lines.Single().Message).Contains("control=close");

            logger.Lines.Clear();
            ArmThenHold(manager, NoteTestBench.CellCentre(manager, id, 0), 2_000_000); // chip ↺
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.WithCustomMessage("the chip's ↺ dwell must log control=undo")
                .That(logger.Lines.Single().Message).Contains("control=undo");
        }
        finally { DpiHelper.Initialize(1.0, 1.0); manager.Dispose(); }
    }

    [StaFact]
    public void ACaretakerTouch_DoesNotLog_ItHasNoDwellOrGazeDistance()
    {
        var (manager, logger) = MakeManager();
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            manager.CreateNoteAt(new PhysicalPoint(1600, 1000));
            var id = manager.ActiveNotes[0].Id;
            manager.Apply(new NoteAppend("saved"));
            manager.FinishEdit(id, commit: true);

            for (long t = 0; t <= 200_000; t += 100_000)
                manager.OnGazeMoved(new PhysicalPoint(4000, 5000), t);
            manager.RunControlInput(id, "close", NoteInputKind.Touch, fromStylus: false);

            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            Check.WithCustomMessage("a caretaker touch is not a fired DWELL: no A4 line")
                .That(logger.Lines).IsEmpty();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); manager.Dispose(); }
    }

    /// <summary>Looks away past every control for 200 ms, then holds past the 600 ms dwell.</summary>
    private static void ArmThenHold(NoteManager manager, PhysicalPoint point, long startUs)
    {
        var away = new PhysicalPoint(point.X + 10_000, point.Y);
        for (long t = startUs; t <= startUs + 200_000; t += 100_000)
            manager.OnGazeMoved(away, t);
        for (long t = startUs + 300_000; t <= startUs + 900_000; t += 100_000)
            manager.OnGazeMoved(point, t);
    }

    /// <summary>A logger that keeps every rendered line, so the test reads what Serilog would
    /// write — the message template's formatted state, not a hand-built string.</summary>
    private sealed class Capture : ILogger<NoteManager>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, state?.ToString() ?? string.Empty));
    }
}
