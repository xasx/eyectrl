namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
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
            var (controller, im, _, _) = ScrollModeControllerThreadingTests.NewScrollWorld(
                input,
                cursorReader: () => new PhysicalPoint(432, 567),
                glassBackdrop: backdrop);
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

    private sealed class CapturingSink : ILogEventSink
    {
        public List<string> Messages { get; } = new();
        public void Emit(LogEvent logEvent) => Messages.Add(logEvent.RenderMessage());
    }
}
