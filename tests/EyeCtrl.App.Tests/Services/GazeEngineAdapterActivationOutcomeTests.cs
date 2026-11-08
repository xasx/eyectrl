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
/// R3: the adapter-side wiring of the activation-outcome tracker. The full window-free chain under
/// the DEFAULT settings (RestFill, None mode): rest → fill → fire → the VM's completion → the
/// adapter's accept notes the tracker (id, style, mode) → the session's end resolves exactly one
/// <c>Activation outcome</c> line. An offer torn down without an accept (the expiry/cancel shape)
/// writes none.
/// <para>
/// Fixups: holistic F2's race (a run-end FreeLook posted between the fire and the accept must not
/// close the fresh session — the reviewer's real-dispatcher probe, as the regression test);
/// R3 review F3 (the accept's style and mode arguments, under OfferButton + ManualSelection);
/// R3 review F4/O9 (an AutoSelection decline writes no outcome line).
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

    /// <summary>Captures the adapter's Information lines, like the rest-offer tests' recorder.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }

    [StaFact]
    public void ARestFire_AcceptedThroughTheAdapter_WritesOneOutcomeLineAtTheActionsEnd()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        settings.Dwell.FixationRadiusPixels = 90;
        var provider = new TestFakes.FakeGazeProvider();
        using var engine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
        engine.ConfigureActivationOffers(Screen, scale: 2.0);

        var messenger = new WeakReferenceMessenger();
        var outcomeLines = new List<string>();
        long clock = 5_000;
        using var tracker = new ActivationOutcomeLog(
            messenger, outcomeLines.Add, () => clock);
        using var adapter = new GazeEngineAdapter(
            engine, settings, messenger, Dispatcher.CurrentDispatcher,
            NullLogger<GazeEngineAdapter>.Instance, new StubControlFinder(),
            noteOutcomeAccepted: tracker.NoteAccepted);
        _ = new ActivationButtonViewModel(messenger);   // in the loop, as production has it

        // Rest → the offer shows; keep resting → it fires (the fill is F = 700 ms at ~30 ms samples).
        long us = 33_000;
        provider.Raise(new GazePoint(P.X, P.Y, us, true));
        var im = engine.InteractionManager;
        im.OnFixationStarted(P);
        im.OnFixationSettled(P);
        for (int i = 0; i < 26; i++)
        {
            us += 30_000;
            provider.Raise(new GazePoint(P.X, P.Y, us, true));
        }
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        // The fire went through the VM's completion and the adapter ACCEPTED it: the session is
        // open, but no outcome line exists yet — the session has not ended.
        Check.WithCustomMessage("the state must be ActionMenuVisible (None mode) after the accept")
            .That(im.CurrentState).IsEqualTo(InteractionState.ActionMenuVisible);
        Check.WithCustomMessage("no outcome before the session ends; lines: "
                + string.Join(" | ", outcomeLines))
            .That(outcomeLines).IsEmpty();

        // The menu action ends the session as `action` (the ActionRequested traffic the adapter
        // marshals is the same that writes today's Action: lines).
        clock += 1_700;
        im.OnActionSelected(InteractionAction.LeftClick);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        Check.That(outcomeLines).HasSize(1);
        Check.That(outcomeLines[0]).IsEqualTo(
            "Activation outcome #1 style=rest mode=None result=action heldMs=1700");
    }

    [StaFact]
    public void ARestFire_AcceptedThenDismissed_WritesOneClosedOutcomeLine()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        settings.Dwell.FixationRadiusPixels = 90;
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
        for (int i = 0; i < 26; i++)
        {
            us += 30_000;
            provider.Raise(new GazePoint(P.X, P.Y, us, true));
        }
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        Check.That(im.CurrentState).IsEqualTo(InteractionState.ActionMenuVisible);

        // Closed with no action (Back / ✕ / timeout all land here): the unintended-open count.
        im.OnActionMenuDismissed();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        Check.That(outcomeLines).HasSize(1);
        Check.That(outcomeLines[0]).Contains("style=rest mode=None result=closed");
    }

    [StaFact]
    public void AnOfferTornDownWithoutAnAccept_WritesNoOutcomeLine()
    {
        // The expiry/cancel shape: the offer dies (a clear here stands in for the expiry) before
        // any completion, so no accept is noted and no outcome line may appear.
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        settings.Dwell.FixationRadiusPixels = 90;
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
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        im.RequestActivationOfferClear();             // the offer dies un-fired
        us += 30_000;
        provider.Raise(new GazePoint(P.X, P.Y, us, true));
        im.OnFixationEnded();                          // back to FreeLook
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        Check.WithCustomMessage("lines: " + string.Join(" | ", outcomeLines))
            .That(outcomeLines).IsEmpty();
    }

    [StaFact]
    public void ARunEndQueuedBetweenTheFireAndTheAccept_DoesNotCloseTheSession_TheActionWins()
    {
        // Holistic fixup-01 F2 / R3 review F1, the reviewer's probe as the regression test. The
        // gaze thread can end the run on the fire sample (H = 1.5 R credits a sample beyond R that
        // ends the detector's run), posting StateChanged(FreeLook) AFTER the fired message but
        // BEFORE the UI thread runs the completion. The stale FreeLook is delivered after
        // NoteAccepted opened the session; without the arming gate it resolves the fresh session
        // as `result=closed heldMs=0` and the real click finds no session left.
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        settings.Dwell.FixationRadiusPixels = 90;
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

        // Rest → offer → fill → fire; then the run ends BEFORE the dispatcher runs the fire.
        long us = 33_000;
        provider.Raise(new GazePoint(P.X, P.Y, us, true));
        var im = engine.InteractionManager;
        im.OnFixationStarted(P);
        im.OnFixationSettled(P);
        for (int i = 0; i < 26; i++)
        {
            us += 30_000;
            provider.Raise(new GazePoint(P.X, P.Y, us, true));
        }
        im.OnFixationEnded();                          // the race: posted, not yet delivered
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        Check.WithCustomMessage("the accept must still have run")
            .That(im.CurrentState).IsEqualTo(InteractionState.ActionMenuVisible);
        Check.WithCustomMessage(
                "the stale FreeLook must not resolve the fresh session; lines: "
                + string.Join(" | ", outcomeLines))
            .That(outcomeLines).IsEmpty();

        im.OnActionSelected(InteractionAction.LeftClick);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        Check.That(outcomeLines).HasSize(1);
        Check.WithCustomMessage("the run's real end must win over the queued FreeLook; lines: "
                + string.Join(" | ", outcomeLines))
            .That(outcomeLines[0]).Contains("style=rest mode=None result=action");
    }

    [StaFact]
    public void AButtonStyleFire_UnderManualSelection_WritesStyleButtonModeManualSelection()
    {
        // R3 review F3: the accept's style and mode arguments reach the tracker — OfferButton and
        // ManualSelection, not the default RestFill + None every other test runs under (mutants
        // O6/O7 hard-wire `rest`/`None` and go red only here).
        var settings = new EyeCtrlSettings();
        settings.Dwell.ActivationStyle = ActivationStyle.OfferButton;
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        settings.Dwell.FixationRadiusPixels = 90;
        settings.SelectionMode = SelectionMode.ManualSelection;
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

        // Settle at P, then dwell on the offered button B until it fires.
        long us = 0;
        for (int i = 0; i < 20; i++)
        {
            us += 33_000;
            provider.Raise(new GazePoint(P.X, P.Y, us, true));
        }
        var offer = engine.InteractionManager.ActivationOffers!.CurrentOffer!;
        for (int i = 0; i < 25; i++)
        {
            us += 33_000;
            provider.Raise(new GazePoint(offer.Button.X, offer.Button.Y, us, true));
        }
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        var im = engine.InteractionManager;
        Check.WithCustomMessage("the button fire must have been accepted into ManualSelection")
            .That(im.CurrentState).IsEqualTo(InteractionState.ManualSelectionActive);

        // Back out of the magnifier with no action: the unintended-open shape.
        im.OnManualSelectionBack();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        Check.That(outcomeLines).HasSize(1);
        Check.That(outcomeLines[0]).Contains(
            "style=button mode=ManualSelection result=closed");
    }

    [StaFact]
    public void AnAutoSelectionDecline_WritesNoOutcomeLine()
    {
        // R3 review F4/O9: a fire that is then DECLINED writes `Offer fired` and
        // `Offer declined #N (no control found)` but no outcome line — only an accept notes a
        // session. AutoSelection with no control found and no pixel fallback is the decline path.
        var settings = new EyeCtrlSettings();
        settings.Dwell.ActivationStyle = ActivationStyle.OfferButton;
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        settings.Dwell.FixationRadiusPixels = 90;
        settings.SelectionMode = SelectionMode.AutoSelection;
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
        // come back to this dispatcher, which the loop below pumps.
        var previousContext = System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            long us = 0;
            for (int i = 0; i < 20; i++)
            {
                us += 33_000;
                provider.Raise(new GazePoint(P.X, P.Y, us, true));
            }
            var offer = engine.InteractionManager.ActivationOffers!.CurrentOffer!;
            for (int i = 0; i < 25; i++)
            {
                us += 33_000;
                provider.Raise(new GazePoint(offer.Button.X, offer.Button.Y, us, true));
            }

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!logger.Lines.Any(l => l.Contains("Offer declined", StringComparison.Ordinal))
                   && DateTime.UtcNow < deadline)
            {
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                System.Threading.Thread.Sleep(5);
            }
        }
        finally
        {
            System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        Check.WithCustomMessage("the decline line must have been written: "
                + string.Join(" | ", logger.Lines))
            .That(logger.Lines.Any(l => l.Contains("Offer declined #1 (no control found)",
                StringComparison.Ordinal))).IsTrue();
        // A decline that wrongly opened a session would sit UNARMED (no entry state follows a
        // decline) and write nothing by itself, so the bare "no line" above cannot tell. Deliver
        // an entry state and its end, as the next real accept's session would see them: with no
        // session open they resolve nothing, while a session the decline opened would arm on the
        // first and close on the second.
        messenger.Send(new StateChangedMessage(InteractionState.ActionMenuVisible));
        messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Check.WithCustomMessage("a declined fire must open no session; lines: "
                + string.Join(" | ", outcomeLines))
            .That(outcomeLines).IsEmpty();
    }
}
