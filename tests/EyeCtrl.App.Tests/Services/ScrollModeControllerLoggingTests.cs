namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

/// <summary>
/// G8 (scroll start fix, F-S5): the INFO lines that make the next session diagnosable — the entry
/// (anchor, thresholds, cursor, backdrop), the per-second wheel/cursor/armed summary, and the exit
/// (duration, wheels, armed). The armed line itself is pinned in
/// <see cref="EyeCtrl.ViewModels.Tests.ScrollOverlayViewModelTests"/> where it is emitted.
/// <para>
/// EXCLUSIVE collection (fixup 01, F1): this class swaps the process-global <see cref="Log.Logger"/>,
/// and parallel classes drive <see cref="ScrollModeController"/> (their entry lines went into
/// whichever logger was global — 4 of 6 runs red on unmutated code). The
/// <c>WpfApplication</c> collection is <c>DisableParallelization = true</c> (xUnit v3 runs such a
/// collection exclusively), and every selection below additionally names THIS test's own line.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class ScrollModeControllerLoggingTests
{
    [StaFact]
    public void TheEntryAndExitLines_CarryTheirFields()
    {
        var sink = new CapturingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        try
        {
            var input = new TestFakes.FakeInputSimulator();
            var backdrop = new TestFakes.FakeGlassBackdropOverlay();
            // The backdrop was up when the entry began (a menu action from the magnifier): the
            // entry's CompleteHideNow must complete it, and the line must say False.
            backdrop.ShowBackdrop(new BackdropSettings { Type = BackdropType.Glass, Enabled = true }, new PhysicalPoint(1, 1));
            var (controller, im, _, _) = ScrollModeControllerThreadingTests.NewScrollWorld(
                input,
                cursorReader: () => input.InjectionState is null
                    ? new PhysicalPoint(432, 567)   // BEFORE the park: the external writer's cursor
                    : new PhysicalPoint(999, 999),  // after MoveCursor: must NOT be what the line reports
                glassBackdrop: backdrop);
            input.InjectionStateProbe = () => "parked";
            using (controller)
            {
                ScrollModeControllerThreadingTests.EnterScrollMode(im);

                // Select THIS test's own line: the anchor AND its unique fake cursor. Without the
                // collection, another test's entry line could match "Scroll mode: " first (F1).
                var entry = sink.Messages.FirstOrDefault(m =>
                    m.Contains("Scroll mode: ", StringComparison.Ordinal)
                    && m.Contains("anchor=(400,400)", StringComparison.Ordinal)
                    && m.Contains("cursor=(432,567)", StringComparison.Ordinal));
                Check.WithCustomMessage($"saw: {string.Join(" | ", sink.Messages)}").That(entry).IsNotNull();
                Check.WithCustomMessage(entry!)
                    .That(entry!.Contains("deadZone=75px", StringComparison.Ordinal)).IsTrue();
                Check.WithCustomMessage(entry)
                    .That(entry.Contains("stopZone=50px", StringComparison.Ordinal)).IsTrue();
                Check.WithCustomMessage(entry)
                    .That(entry.Contains("[BackdropVisible=False]", StringComparison.Ordinal)).IsTrue();

                im.ExitScrollMode();

                // The exclusive collection leaves only this test's exit line; LastOrDefault keeps
                // it ours even if a worker line arrived first.
                var exit = sink.Messages.LastOrDefault(m =>
                    m.Contains("Scroll mode exited after ", StringComparison.Ordinal));
                Check.WithCustomMessage($"saw: {string.Join(" | ", sink.Messages)}").That(exit).IsNotNull();
                Check.WithCustomMessage(exit!)
                    .That(exit!.Contains("(wheels 0, armed False)", StringComparison.Ordinal)).IsTrue();
            }
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    [StaFact]
    public void TheEntryLine_ReportsAVisibleBackdrop_WhenItsHideDoesNotComplete()
    {
        // S14 (fixup 01): the entry line's [BackdropVisible=…] is the injection-instant reading. A
        // backdrop whose deferred hide does NOT complete (the stub below) stays up — the line must
        // say True, or a session where the wheel could land on the backdrop is undiagnosable.
        // Mutant: `?? false` / the constant False → the line reads False while the stub is up.
        var sink = new CapturingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        try
        {
            var input = new TestFakes.FakeInputSimulator();
            var backdrop = new StuckBackdrop();
            var (controller, im, _, _) = ScrollModeControllerThreadingTests.NewScrollWorld(
                input,
                cursorReader: () => new PhysicalPoint(432, 567),
                glassBackdrop: backdrop);
            using (controller)
            {
                ScrollModeControllerThreadingTests.EnterScrollMode(im);

                var entry = sink.Messages.FirstOrDefault(m =>
                    m.Contains("Scroll mode: ", StringComparison.Ordinal)
                    && m.Contains("cursor=(432,567)", StringComparison.Ordinal));
                Check.WithCustomMessage($"saw: {string.Join(" | ", sink.Messages)}").That(entry).IsNotNull();
                Check.WithCustomMessage(entry!)
                    .That(entry!.Contains("[BackdropVisible=True]", StringComparison.Ordinal)).IsTrue();
            }
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    [StaFact]
    public void ThePerSecondLine_CountsTheSameWheelsTheSimulatorReceived()
    {
        // S19 (fixup 02): the down-counter had no assertion, so removing
        // `_wheelsDown += -intDeltaY;` survived. N ticks with a downward target: the line's
        // wheelsDown must equal the negative deltas the fake simulator actually received, and
        // wheelsUp must stay 0.
        var sink = new CapturingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        try
        {
            var input = new TestFakes.FakeInputSimulator();
            using var worker = new ScrollWorker(input);
            worker.Start(
                new PhysicalPoint(1000, 1000),
                isArmed: () => true,
                cursorReader: () => new PhysicalPoint(1100, 1000));
            worker.UpdateTargetVelocity(0, -600);   // downward

            for (int i = 0; i < 63; i++) worker.Tick();

            int wheelsDown = input.BareScrolls.Sum(s => s.Dy < 0 ? -s.Dy : 0);
            int wheelsUp = input.BareScrolls.Sum(s => s.Dy > 0 ? s.Dy : 0);
            Check.WithCustomMessage("the premise: the fake received downward wheels")
                .That(wheelsDown).IsStrictlyGreaterThan(0);

            var line = sink.Messages.FirstOrDefault(m =>
                m.Contains("Scroll: ", StringComparison.Ordinal)
                && m.Contains("cursor=(1100,1000)", StringComparison.Ordinal));
            Check.WithCustomMessage($"saw: {string.Join(" | ", sink.Messages)}").That(line).IsNotNull();
            Check.WithCustomMessage(line!)
                .That(line!.Contains($"wheelsDown={wheelsDown}", StringComparison.Ordinal)).IsTrue();
            Check.WithCustomMessage(line)
                .That(line.Contains($"wheelsUp={wheelsUp}", StringComparison.Ordinal)).IsTrue();
            Check.That(wheelsUp).IsEqualTo(0);
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    [StaFact]
    public void TheWorker_LogsThePerSecondSummary_WithItsFields()
    {
        var sink = new CapturingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        try
        {
            var input = new TestFakes.FakeInputSimulator();
            using var worker = new ScrollWorker(input);
            worker.Start(
                new PhysicalPoint(1000, 1000),
                isArmed: () => true,
                cursorReader: () => new PhysicalPoint(1100, 1000));
            worker.UpdateTargetVelocity(0, -600);   // a strong downward target: notches every tick

            for (int i = 0; i < 63; i++) worker.Tick();

            // This test's own line: its unique fake cursor (F1's selection rule).
            var line = sink.Messages
                .FirstOrDefault(m => m.Contains("Scroll: ", StringComparison.Ordinal)
                    && m.Contains("cursor=(1100,1000)", StringComparison.Ordinal));
            Check.WithCustomMessage($"saw: {string.Join(" | ", sink.Messages)}").That(line).IsNotNull();
            Check.WithCustomMessage(line!)
                .That(line!.Contains("wheelsUp=0", StringComparison.Ordinal)).IsTrue();
            Check.WithCustomMessage(line)
                .That(line.Contains("cursorToAnchor=100px", StringComparison.Ordinal)).IsTrue();
            Check.WithCustomMessage(line)
                .That(line.Contains("armed=True", StringComparison.Ordinal)).IsTrue();
            Check.WithCustomMessage(line)
                .That(line.Contains(" elapsedMs=", StringComparison.Ordinal)).IsTrue();

            // One line per second of ticks: the next 60 ticks log nothing (the 62nd would).
            for (int i = 0; i < 60; i++) worker.Tick();
            Check.That(sink.Messages.Count(m => m.Contains("Scroll: ", StringComparison.Ordinal)
                && m.Contains("cursor=(1100,1000)", StringComparison.Ordinal))).IsEqualTo(1);
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    /// <summary>A backdrop whose deferred hide never completes (S14): it stays visible through the
    /// scroll entry, so the entry line must report it.</summary>
    private sealed class StuckBackdrop : IGlassBackdropOverlay
    {
        public bool IsVisible => true;
        public void ShowBackdrop(BackdropSettings settings, PhysicalPoint hideButtonCenter) { }
        public void Show() { }
        public void Hide() { }
        public void CompleteHideNow() { }
        public void ApplyBackdrop(BackdropSettings settings) { }
        public void DrawHideButton(PhysicalPoint buttonCenter, double progress = 0) { }
        public void UpdateHideButtonProgress(double progress) { }
        public void ShowEscapeHint() { }
        public void HideEscapeHint() { }
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<string> Messages { get; } = new();
        public void Emit(LogEvent logEvent) => Messages.Add(logEvent.RenderMessage());
    }
}
