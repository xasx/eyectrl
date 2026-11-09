namespace EyeCtrl.App.Tests.Services;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NFluent;
using Xunit;

/// <summary>
/// The adapter-side wiring of the activation-outcome tracker (salvaged from #322's R3, round-button
/// B1, rebased on the offer button — the one activation style). The full window-free chain under the
/// default settings (None mode): settle at P → dwell on the offered button B → fire → the VM's
/// completion → the adapter's accept notes the tracker (id, mode) → the session's end resolves
/// exactly one <c>Activation outcome</c> line. An offer torn down without an accept writes none.
/// <para>
/// Fixups carried over: #322 holistic F2's race (a run-end FreeLook posted between the fire and the
/// accept must not close the fresh session — the reviewer's real-dispatcher probe, as the regression
/// test); R3 review F3 (the accept's mode argument, under ManualSelection); R3 review F4/O9 (an
/// AutoSelection decline writes no outcome line).
/// </para>
/// </summary>
public class GazeEngineAdapterActivationOutcomeTests
{
    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);
    private static readonly PhysicalPoint P = new(2000, 1200);

    private sealed class StubControlFinder : IControlFinder
    {
        public ControlInfo? FindTarget(PhysicalPoint point) => null;
    }

    /// <summary>Captures the adapter's Information lines.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }

    private static EyeCtrlSettings DefaultSettings(SelectionMode mode = SelectionMode.None)
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        settings.Dwell.FixationRadiusPixels = 90;
        settings.SelectionMode = mode;
        return settings;
    }

    /// <summary>Settle at P (the real detector settles on these samples, and the offer appears),
    /// then dwell on the offered button B well past the 500 ms default dwell. Returns the last
    /// sample's timestamp.</summary>
    private static long SettleThenDwellOnTheButton(TestFakes.FakeGazeProvider provider, GazeEngine engine)
    {
        long us = 0;
        for (int i = 0; i < 20; i++)
        {
            us += 33_000;
            provider.Raise(new GazePoint(P.X, P.Y, us, true));
        }
        var offer = engine.InteractionManager.ActivationOffers!.CurrentOffer;
        Check.WithCustomMessage("the settle at P must have made an offer").That(offer).IsNotNull();
        for (int i = 0; i < 25; i++)
        {
            us += 33_000;
            provider.Raise(new GazePoint(offer!.Button.X, offer.Button.Y, us, true));
        }
        return us;
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    [StaFact]
    public void AButtonFire_AcceptedThroughTheAdapter_WritesOneOutcomeLineAtTheActionsEnd()
    {
        var settings = DefaultSettings();
        var provider = new TestFakes.FakeGazeProvider();
        using var engine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
        engine.ConfigureActivationOffers(Screen, scale: 2.0);

        var messenger = new WeakReferenceMessenger();
        var outcomeLines = new List<string>();
        long clock = 5_000;
        using var tracker = new ActivationOutcomeLog(messenger, outcomeLines.Add, () => clock);
        using var adapter = new GazeEngineAdapter(
            engine, settings, messenger, Dispatcher.CurrentDispatcher,
            NullLogger<GazeEngineAdapter>.Instance, new StubControlFinder(),
            noteOutcomeAccepted: tracker.NoteAccepted);
        _ = new ActivationButtonViewModel(messenger);   // in the loop, as production has it

        SettleThenDwellOnTheButton(provider, engine);
        Pump();

        // The fire went through the VM's completion and the adapter ACCEPTED it: the session is
        // open, but no outcome line exists yet — the session has not ended.
        var im = engine.InteractionManager;
        Check.WithCustomMessage("the state must be ActionMenuVisible (None mode) after the accept")
            .That(im.CurrentState).IsEqualTo(InteractionState.ActionMenuVisible);
        Check.WithCustomMessage("no outcome before the session ends; lines: " + string.Join(" | ", outcomeLines))
            .That(outcomeLines).IsEmpty();

        // The menu action ends the session as `action` (the ActionRequested traffic the adapter
        // marshals is the same that writes today's Action: lines).
        clock += 1_700;
        im.OnActionSelected(InteractionAction.LeftClick);
        Pump();

        Check.That(outcomeLines).HasSize(1);
        Check.That(outcomeLines[0]).IsEqualTo("Activation outcome #1 mode=None result=action heldMs=1700");
    }

    [StaFact]
    public void AButtonFire_AcceptedThenDismissed_WritesOneClosedOutcomeLine()
    {
        var settings = DefaultSettings();
        var provider = new TestFakes.FakeGazeProvider();
        using var engine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
        engine.ConfigureActivationOffers(Screen, scale: 2.0);

        var messenger = new WeakReferenceMessenger();
        var outcomeLines = new List<string>();
        using var tracker = new ActivationOutcomeLog(messenger, outcomeLines.Add);
        using var adapter = new GazeEngineAdapter(
            engine, settings, messenger, Dispatcher.CurrentDispatcher,
            NullLogger<GazeEngineAdapter>.Instance, new StubControlFinder(),
            noteOutcomeAccepted: tracker.NoteAccepted);
        _ = new ActivationButtonViewModel(messenger);

        SettleThenDwellOnTheButton(provider, engine);
        Pump();
        var im = engine.InteractionManager;
        Check.That(im.CurrentState).IsEqualTo(InteractionState.ActionMenuVisible);

        // Closed with no action (Back / ✕ / timeout all land here): the unintended-open count.
        im.OnActionMenuDismissed();
        Pump();

        Check.That(outcomeLines).HasSize(1);
        Check.That(outcomeLines[0]).StartsWith("Activation outcome #1 mode=None result=closed heldMs=");
    }

    [StaFact]
    public void AnOfferTornDownWithoutAnAccept_WritesNoOutcomeLine()
    {
        // The expiry/cancel shape: the offer dies (a clear here stands in for the expiry) before
        // any completion, so no accept is noted and no outcome line may appear.
        var settings = DefaultSettings();
        var provider = new TestFakes.FakeGazeProvider();
        using var engine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
        engine.ConfigureActivationOffers(Screen, scale: 2.0);

        var messenger = new WeakReferenceMessenger();
        var outcomeLines = new List<string>();
        using var tracker = new ActivationOutcomeLog(messenger, outcomeLines.Add);
        using var adapter = new GazeEngineAdapter(
            engine, settings, messenger, Dispatcher.CurrentDispatcher,
            NullLogger<GazeEngineAdapter>.Instance, new StubControlFinder(),
            noteOutcomeAccepted: tracker.NoteAccepted);
        _ = new ActivationButtonViewModel(messenger);

        long us = 33_000;
        provider.Raise(new GazePoint(P.X, P.Y, us, true));
        var im = engine.InteractionManager;
        im.OnFixationStarted(P);
        im.OnFixationSettled(P);
        Pump();
        Check.WithCustomMessage("the settle must have made an offer")
            .That(im.ActivationOffers!.CurrentOffer).IsNotNull();

        im.RequestActivationOfferClear();             // the offer dies un-fired
        us += 30_000;
        provider.Raise(new GazePoint(P.X, P.Y, us, true));
        im.OnFixationEnded();                          // back to FreeLook
        Pump();

        Check.WithCustomMessage("lines: " + string.Join(" | ", outcomeLines)).That(outcomeLines).IsEmpty();
    }

    [StaFact]
    public void ARunEndQueuedBetweenTheFireAndTheAccept_DoesNotCloseTheSession_TheActionWins()
    {
        // #322 holistic fixup-01 F2 / R3 review F1, the reviewer's probe as the regression test. The
        // gaze thread can end the run on (or right after) the fire sample, posting
        // StateChanged(FreeLook) AFTER the fired message but BEFORE the UI thread runs the
        // completion. The stale FreeLook is delivered after NoteAccepted opened the session; without
        // the arming gate it resolves the fresh session as `result=closed heldMs=0` and the real
        // click finds no session left.
        var settings = DefaultSettings();
        var provider = new TestFakes.FakeGazeProvider();
        using var engine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
        engine.ConfigureActivationOffers(Screen, scale: 2.0);

        var messenger = new WeakReferenceMessenger();
        var outcomeLines = new List<string>();
        using var tracker = new ActivationOutcomeLog(messenger, outcomeLines.Add);
        using var adapter = new GazeEngineAdapter(
            engine, settings, messenger, Dispatcher.CurrentDispatcher,
            NullLogger<GazeEngineAdapter>.Instance, new StubControlFinder(),
            noteOutcomeAccepted: tracker.NoteAccepted);
        _ = new ActivationButtonViewModel(messenger);

        SettleThenDwellOnTheButton(provider, engine);
        var im = engine.InteractionManager;
        Check.WithCustomMessage("the button run must still be in Fixation, so its end posts a FreeLook")
            .That(im.CurrentState).IsEqualTo(InteractionState.Fixation);
        im.OnFixationEnded();                          // the race: posted, not yet delivered
        Pump();

        Check.WithCustomMessage("the accept must still have run")
            .That(im.CurrentState).IsEqualTo(InteractionState.ActionMenuVisible);
        Check.WithCustomMessage(
                "the stale FreeLook must not resolve the fresh session; lines: " + string.Join(" | ", outcomeLines))
            .That(outcomeLines).IsEmpty();

        im.OnActionSelected(InteractionAction.LeftClick);
        Pump();

        Check.That(outcomeLines).HasSize(1);
        Check.WithCustomMessage("the run's real end must win over the queued FreeLook; lines: "
                + string.Join(" | ", outcomeLines))
            .That(outcomeLines[0]).StartsWith("Activation outcome #1 mode=None result=action heldMs=");
    }

    [StaFact]
    public void AButtonFire_UnderManualSelection_WritesModeManualSelection()
    {
        // R3 review F3: the accept's mode argument reaches the tracker — ManualSelection (the
        // owner's mode), not the default None every other test runs under (a mutant hard-wiring
        // `None` goes red only here).
        var settings = DefaultSettings(SelectionMode.ManualSelection);
        var provider = new TestFakes.FakeGazeProvider();
        using var engine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
        engine.ConfigureActivationOffers(Screen, scale: 2.0);

        var messenger = new WeakReferenceMessenger();
        var outcomeLines = new List<string>();
        using var tracker = new ActivationOutcomeLog(messenger, outcomeLines.Add);
        using var adapter = new GazeEngineAdapter(
            engine, settings, messenger, Dispatcher.CurrentDispatcher,
            NullLogger<GazeEngineAdapter>.Instance, new StubControlFinder(),
            noteOutcomeAccepted: tracker.NoteAccepted);
        _ = new ActivationButtonViewModel(messenger);

        SettleThenDwellOnTheButton(provider, engine);
        Pump();
        var im = engine.InteractionManager;
        Check.WithCustomMessage("the button fire must have been accepted into ManualSelection")
            .That(im.CurrentState).IsEqualTo(InteractionState.ManualSelectionActive);

        // Back out of the magnifier with no action: the unintended-open shape.
        im.OnManualSelectionBack();
        Pump();

        Check.That(outcomeLines).HasSize(1);
        Check.That(outcomeLines[0]).StartsWith("Activation outcome #1 mode=ManualSelection result=closed heldMs=");
    }

    [StaFact]
    public void TheOfferFiredLine_CarriesTheShowToFirstLookTime()
    {
        // Round-button B1: `Offer fired #N firstLookMs=…`, the time from the offer's show to the
        // first look into the button's area, which the metrics tool takes the median of. The first
        // sample on B is the first look (Direct filter: the engine sees the raw point).
        var settings = DefaultSettings();
        var provider = new TestFakes.FakeGazeProvider();
        using var engine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
        engine.ConfigureActivationOffers(Screen, scale: 2.0);

        var messenger = new WeakReferenceMessenger();
        var logger = new RecordingLogger<GazeEngineAdapter>();
        using var adapter = new GazeEngineAdapter(
            engine, settings, messenger, Dispatcher.CurrentDispatcher, logger, new StubControlFinder());

        long us = 0;
        for (int i = 0; i < 20; i++)
        {
            us += 33_000;
            provider.Raise(new GazePoint(P.X, P.Y, us, true));
        }
        var offer = engine.InteractionManager.ActivationOffers!.CurrentOffer!;
        long firstLookUs = us + 33_000;
        for (int i = 0; i < 25; i++)
        {
            us += 33_000;
            provider.Raise(new GazePoint(offer.Button.X, offer.Button.Y, us, true));
        }

        var fired = logger.Lines.Where(l => l.StartsWith("Offer fired #", StringComparison.Ordinal)).ToList();
        Check.WithCustomMessage("lines: " + string.Join(" | ", logger.Lines)).That(fired).HasSize(1);
        long expectedMs = (firstLookUs - offer.ShownAtUs) / 1000;
        Check.That(expectedMs).IsStrictlyGreaterThan(0L);
        Check.That(fired[0].Equals($"Offer fired #{offer.Id} firstLookMs={expectedMs}", StringComparison.Ordinal))
            .IsTrue();
    }

    [StaFact]
    public void AnAutoSelectionDecline_WritesNoOutcomeLine()
    {
        // R3 review F4/O9: a fire that is then DECLINED writes `Offer fired` and
        // `Offer declined #N (no control found)` but no outcome line — only an accept notes a
        // session. AutoSelection with no control found and no pixel fallback is the decline path.
        var settings = DefaultSettings(SelectionMode.AutoSelection);
        settings.AutoSelection.FallbackToPixelOnNoControl = false;
        var provider = new TestFakes.FakeGazeProvider();
        using var engine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
        engine.ConfigureActivationOffers(Screen, scale: 2.0);

        var messenger = new WeakReferenceMessenger();
        var outcomeLines = new List<string>();
        var logger = new RecordingLogger<GazeEngineAdapter>();
        using var tracker = new ActivationOutcomeLog(messenger, outcomeLines.Add);
        using var adapter = new GazeEngineAdapter(
            engine, settings, messenger, Dispatcher.CurrentDispatcher,
            logger, new StubControlFinder(),
            noteOutcomeAccepted: tracker.NoteAccepted);
        _ = new ActivationButtonViewModel(messenger);
        // The completion handler awaits the control lookup; its continuation (the decline) must
        // come back to this dispatcher, which the bounded loop below pumps.
        var previousContext = System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            SettleThenDwellOnTheButton(provider, engine);

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!logger.Lines.Any(l => l.Contains("Offer declined", StringComparison.Ordinal))
                   && DateTime.UtcNow < deadline)
            {
                Pump();
                System.Threading.Thread.Sleep(5);
            }
        }
        finally
        {
            System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        Check.WithCustomMessage("the decline line must have been written: " + string.Join(" | ", logger.Lines))
            .That(logger.Lines.Any(l => l.Contains("Offer declined #1 (no control found)",
                StringComparison.Ordinal))).IsTrue();
        // A decline that wrongly opened a session would sit UNARMED (no entry state follows a
        // decline) and write nothing by itself, so the bare "no line" above cannot tell. Deliver
        // an entry state and its end, as the next real accept's session would see them: with no
        // session open they resolve nothing, while a session the decline opened would arm on the
        // first and close on the second.
        messenger.Send(new StateChangedMessage(InteractionState.ActionMenuVisible));
        messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Check.WithCustomMessage("a declined fire must open no session; lines: " + string.Join(" | ", outcomeLines))
            .That(outcomeLines).IsEmpty();
    }
}
