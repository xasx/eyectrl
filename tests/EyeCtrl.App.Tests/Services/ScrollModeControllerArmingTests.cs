namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

/// <summary>
/// The scroll-arming fix through the real <see cref="EyeCtrl.App.Services.ScrollModeController"/>:
/// G5 (the wheels still land on the anchor after an off-centre arming) and G7 (the diagnosis lines
/// carry <c>nearest=</c> and <c>offset=</c>).
/// <para>
/// EXCLUSIVE collection, like <see cref="ScrollModeControllerLoggingTests"/>: the G7 tests swap the
/// process-global <see cref="Log.Logger"/>, so they must run with no other controller alive, and each
/// selects its own line by content. The tests that write <see cref="DpiHelper"/> restore 1.0 in
/// <c>finally</c>; the collection is exclusive, so no reader sees the scale mid-test.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class ScrollModeControllerArmingTests
{
    // ScrollModeControllerThreadingTests.EnterScrollMode(im) anchors scroll mode at (400,400).
    private static readonly PhysicalPoint Anchor = new(400, 400);

    private static void Send(IMessenger messenger, PhysicalPoint at, long fromMs, long toMs)
    {
        for (long t = fromMs; t <= toMs; t += 30)
            messenger.Send(new GazeCursorMovedMessage(at, t * 1000));
    }

    [StaFact]
    public void G5_TheWheelsStillLandOnTheAnchor_AfterAnOffCentreArming()
    {
        // The re-centred rest point changes where the joystick's neutral is, NOT where the page is
        // scrolled: ScrollWorker.Start(anchor) keeps the anchor. Mutant M8: the worker's anchor
        // switched to the rest point → the wheels land at (300,450), red.
        var messenger = new WeakReferenceMessenger();
        var input = new TestFakes.FakeInputSimulator();
        var (controller, im, _, _) = ScrollModeControllerThreadingTests.NewScrollWorld(
            input, messenger: messenger);
        using (controller)
        {
            ScrollModeControllerThreadingTests.EnterScrollMode(im);
            var rest = new PhysicalPoint(Anchor.X - 100, Anchor.Y + 50);   // 112 px off: inside the 175-px circle

            Send(messenger, rest, 0, 150);                                 // the arming fixation, off the cross
            Send(messenger, Anchor, 180, 180);                             // gaze on the cross: now AWAY from the rest point

            var worker = controller.Worker;
            Check.That(worker).IsNotNull();
            for (int i = 0; i < 400 && input.BareScrollPoints.Count == 0; i++) worker!.Tick();

            Check.WithCustomMessage("the premise: the armed joystick produced wheels")
                .That(input.BareScrollPoints).Not.IsEmpty();
            Check.WithCustomMessage($"wheels landed at: {string.Join(" ", input.BareScrollPoints.Distinct())}")
                .That(input.BareScrollPoints.All(p => p == Anchor)).IsTrue();
        }
    }

    [StaFact]
    public void G7_ThePerSecondLine_CarriesNearest_WhileUnarmed()
    {
        // The closest any sample came to the anchor: 225 px — outside the 175-px circle, so it never
        // arms, and the line says how close the gaze got. Mutant M10: nearest never updated → -1.
        var sink = new CapturingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        try
        {
            var messenger = new WeakReferenceMessenger();
            var input = new TestFakes.FakeInputSimulator();
            var (controller, im, _, _) = ScrollModeControllerThreadingTests.NewScrollWorld(
                input, cursorReader: () => new PhysicalPoint(433, 566), messenger: messenger);
            using (controller)
            {
                ScrollModeControllerThreadingTests.EnterScrollMode(im);

                Send(messenger, new PhysicalPoint(Anchor.X + 300, Anchor.Y), 0, 90);
                Send(messenger, new PhysicalPoint(Anchor.X + 225, Anchor.Y), 120, 400);
                Send(messenger, new PhysicalPoint(Anchor.X + 260, Anchor.Y), 430, 500);

                Check.That(controller.Worker).IsNotNull();
                for (int i = 0; i < 63; i++) controller.Worker!.Tick();

                var line = sink.Messages.FirstOrDefault(m =>
                    m.Contains("Scroll: ", StringComparison.Ordinal)
                    && m.Contains("cursor=(433,566)", StringComparison.Ordinal));
                Check.WithCustomMessage($"saw: {string.Join(" | ", sink.Messages)}").That(line).IsNotNull();
                Check.WithCustomMessage(line!).That(line!.Contains("armed=False", StringComparison.Ordinal)).IsTrue();
                Check.WithCustomMessage(line).That(line.Contains(" nearest=225 ", StringComparison.Ordinal)).IsTrue();
            }
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    [StaFact]
    public void G7_TheArmedLine_CarriesTheOffsetAndNearest_AndTheEntryLineTheStartZone()
    {
        // The owner's tracker sat 150 px left, 60 px below the cross: the line must say so, with the
        // right sign (rest point - anchor), and the nearest approach (162 px). 175 DIP at scale 2.
        // Mutant M11: the offset sign flipped → offset=(150,-60); M10 → nearest=-1px.
        var sink = new CapturingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var messenger = new WeakReferenceMessenger();
            var input = new TestFakes.FakeInputSimulator();
            var (controller, im, _, _) = ScrollModeControllerThreadingTests.NewScrollWorld(
                input, cursorReader: () => new PhysicalPoint(777, 888), messenger: messenger);
            using (controller)
            {
                ScrollModeControllerThreadingTests.EnterScrollMode(im);

                Send(messenger, new PhysicalPoint(Anchor.X - 150, Anchor.Y + 60), 0, 150);

                var armed = sink.Messages.FirstOrDefault(m =>
                    m.Contains("Scroll armed after ", StringComparison.Ordinal)
                    && m.Contains("offset=(-150,60)px", StringComparison.Ordinal));
                Check.WithCustomMessage($"saw: {string.Join(" | ", sink.Messages)}").That(armed).IsNotNull();
                Check.WithCustomMessage(armed!).That(armed!.Contains(", nearest=162px)", StringComparison.Ordinal)).IsTrue();

                var entry = sink.Messages.FirstOrDefault(m =>
                    m.Contains("Scroll mode: ", StringComparison.Ordinal)
                    && m.Contains("cursor=(777,888)", StringComparison.Ordinal));
                Check.WithCustomMessage($"saw: {string.Join(" | ", sink.Messages)}").That(entry).IsNotNull();
                Check.WithCustomMessage(entry!).That(entry!.Contains("startZone=350px", StringComparison.Ordinal)).IsTrue();
            }
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
            Log.Logger = previous;
        }
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<string> Messages { get; } = new();
        public void Emit(LogEvent logEvent) => Messages.Add(logEvent.RenderMessage());
    }
}
