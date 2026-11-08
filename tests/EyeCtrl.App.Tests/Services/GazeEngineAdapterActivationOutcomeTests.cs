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
/// </summary>
public class GazeEngineAdapterActivationOutcomeTests
{
    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);
    private static readonly PhysicalPoint P = new(2000, 1200);

    private sealed class StubControlFinder : IControlFinder
    {
        public ControlInfo? FindTarget(PhysicalPoint point) => null;
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
}
