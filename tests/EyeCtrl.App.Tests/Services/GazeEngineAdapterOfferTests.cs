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
/// Ticket 8 at the adapter seam: the button's show follows the offer (B and the hit radius, not the
/// fixation pin and not the raw fixation-radius setting), and a plain rest — however long — never
/// completes through the adapter and the VM. Mutation for the show test: read the fixation point and
/// the setting instead of the offer → both assertions red. Mutation for the rest test: restore the
/// VM's <see cref="FixationActivatedMessage"/> handler → the completion/cue counts go non-zero.
/// </summary>
public class GazeEngineAdapterOfferTests
{
    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);
    private static readonly PhysicalPoint P = new(2000, 1200);

    [StaFact]
    public void ShowMessage_CarriesTheOfferButtonCenterAndHitRadius()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        settings.Dwell.FixationRadiusPixels = 90;
        var provider = new TestFakes.FakeGazeProvider();
        using var engine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
        engine.ConfigureActivationOffers(Screen, scale: 2.0);

        var messenger = new WeakReferenceMessenger();
        ShowActivationButtonMessage? captured = null;
        messenger.Register<ShowActivationButtonMessage>(this, (_, message) => captured = message);

        using var adapter = new GazeEngineAdapter(
            engine, settings, messenger, Dispatcher.CurrentDispatcher,
            NullLogger<GazeEngineAdapter>.Instance, new StubControlFinder());

        // The first sample applies the geometry rebuild and attaches the engine.
        provider.Raise(new GazePoint(P.X, P.Y, 33_000, true));
        engine.InteractionManager.OnFixationStarted(P);
        engine.InteractionManager.OnFixationSettled(P);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        Check.That(captured).IsNotNull();
        Check.WithCustomMessage("the show message carries the offer's hit radius, not the fixation radius (90)")
            .That(captured!.HitRadiusPx).IsEqualTo(LayoutConstants.ActivationButtonHitRadiusPx);
        Check.WithCustomMessage("B is placed clear of the hold, not on the fixation point")
            .That(captured.ButtonCenter.DistanceTo(P)).IsStrictlyGreaterThan(LayoutConstants.ActivationButtonHitRadiusPx + 90);
    }

    [StaFact]
    public void RestingNeverActivates_ThroughTheAdapterAndTheVm()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        settings.Dwell.FixationRadiusPixels = 90;
        var provider = new TestFakes.FakeGazeProvider();
        using var engine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
        engine.ConfigureActivationOffers(Screen, scale: 2.0);

        var messenger = new WeakReferenceMessenger();
        int completions = 0;
        int cues = 0;
        messenger.Register<ActivationCompletedMessage>(this, (_, _) => completions++);
        messenger.Register<DwellActivationMessage>(this, (_, _) => cues++);
        _ = new ActivationButtonViewModel(messenger);   // in the loop, as production has it

        using var adapter = new GazeEngineAdapter(
            engine, settings, messenger, Dispatcher.CurrentDispatcher,
            NullLogger<GazeEngineAdapter>.Instance, new StubControlFinder());

        // A 3.5 s hold at one point: settle at 0.5 s, and the age clock completes at ~1.0 s. The
        // engine's button sits clear of the hold, so the button dwell never advances.
        long us = 0;
        for (int elapsed = 0; elapsed <= 3500; elapsed += 33)
        {
            us += 33_000;
            provider.Raise(new GazePoint(P.X, P.Y, us, true));
        }
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        Check.WithCustomMessage("a plain rest must never complete an activation").That(completions).IsEqualTo(0);
        Check.WithCustomMessage("a plain rest must never cue an activation").That(cues).IsEqualTo(0);
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Fixation);
    }

    [StaFact]
    public void OneDwell_Activates_AndAutoSelection_ResolvesAtP()
    {
        // The plan's row: settle, look at the button, 700 ms → exactly one activation, and Auto
        // Selection resolves at the offer's P. By completion time the fixation point has moved to the
        // button's run, so resolving at FixationPoint instead of P goes red here. Mutation: resolve at
        // _interactionManager.FixationPoint.
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        settings.Dwell.FixationRadiusPixels = 90;
        settings.SelectionMode = SelectionMode.AutoSelection;
        var provider = new TestFakes.FakeGazeProvider();
        using var engine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
        engine.ConfigureActivationOffers(Screen, scale: 2.0);

        var messenger = new WeakReferenceMessenger();
        var menus = new List<ShowActionMenuMessage>();
        messenger.Register<ShowActionMenuMessage>(this, (_, message) => menus.Add(message));
        var vm = new ActivationButtonViewModel(messenger);   // relays the fire, as production does
        var finder = new RecordingControlFinder();
        using var adapter = new GazeEngineAdapter(
            engine, settings, messenger, Dispatcher.CurrentDispatcher,
            NullLogger<GazeEngineAdapter>.Instance, finder);

        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        long us = 0;
        for (int i = 0; i < 20; i++)                          // settle at P
        {
            us += 33_000;
            provider.Raise(new GazePoint(P.X, P.Y, us, true));
        }
        var offer = engine.InteractionManager.ActivationOffers!.CurrentOffer!;
        for (int i = 0; i < 25; i++)                          // 750 ms on the button → fire
        {
            us += 33_000;
            provider.Raise(new GazePoint(offer.Button.X, offer.Button.Y, us, true));
        }

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (menus.Count == 0 && DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(5);
        }

        Check.That(vm.DwellProgress).IsEqualTo(1.0);
        Check.That(finder.Queries).Not.IsEmpty();
        Check.WithCustomMessage("Auto Selection must query the control finder at the offer's P")
            .That(finder.Queries[0]).IsEqualTo(offer.Point);
        Check.That(menus).HasSize(1);
        // GazePoint is the adapter's pass-through of the point it completed at; Center is the
        // display-clamped menu position, which is a different, screen-dependent value on purpose.
        Check.WithCustomMessage("the completion must land at the offer's P")
            .That(menus[0].GazePoint.X).IsCloseTo(offer.Point.X, 1e-9);
        Check.That(menus[0].GazePoint.Y).IsCloseTo(offer.Point.Y, 1e-9);
    }

    [StaFact]
    public void AutoSelectionNoControlNoFallback_DeclinesTheNamedOffer_AndReleasesTheLock()
    {
        // Review A C11/A2: with no control found and the pixel fallback off, the completion must
        // DECLINE the named offer (releasing the fired lock), not act. Mutation: let the fallback
        // complete instead → a menu opens and the fresh settle gets no offer, red.
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        settings.Dwell.FixationRadiusPixels = 90;
        settings.SelectionMode = SelectionMode.AutoSelection;
        settings.AutoSelection.FallbackToPixelOnNoControl = false;
        var provider = new TestFakes.FakeGazeProvider();
        using var engine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
        engine.ConfigureActivationOffers(Screen, scale: 2.0);

        var messenger = new WeakReferenceMessenger();
        var menus = new List<ShowActionMenuMessage>();
        messenger.Register<ShowActionMenuMessage>(this, (_, message) => menus.Add(message));
        var vm = new ActivationButtonViewModel(messenger);
        var finder = new RecordingControlFinder();   // finds nothing
        using var adapter = new GazeEngineAdapter(
            engine, settings, messenger, Dispatcher.CurrentDispatcher,
            NullLogger<GazeEngineAdapter>.Instance, finder);

        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

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
        while (finder.Queries.Count == 0 && DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(5);
        }
        // The resolve's continuation (the decline) posts after the finder returns; give it a pass.
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        // The decline is a request; the next sample applies it.
        us += 33_000;
        provider.Raise(new GazePoint(offer.Button.X, offer.Button.Y, us, true));

        Check.WithCustomMessage("the no-control completion must decline, not act").That(menus).IsEmpty();
        Check.That(engine.InteractionManager.ActivationOffers!.CurrentOffer).IsNull();

        // The lock was released: a fresh settle gets a fresh offer.
        var p2 = new PhysicalPoint(600, 500);
        for (int i = 0; i < 20; i++)
        {
            us += 33_000;
            provider.Raise(new GazePoint(p2.X, p2.Y, us, true));
        }
        Check.That(engine.InteractionManager.ActivationOffers!.CurrentOffer).IsNotNull();
    }

    [StaFact]
    public void WrongStateCompletion_LogsDeclined_NotAccepted()
    {
        // Review B's Low (fixup 02): the "Offer accepted" line must only follow a real activation. A
        // Pause-shaped wrong state during the resolve returns Declined, so the adapter must log a
        // declined reason. Mutation: log accepted unconditionally → the assertion goes red.
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        settings.Dwell.FixationRadiusPixels = 90;
        var provider = new TestFakes.FakeGazeProvider();
        using var engine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
        engine.ConfigureActivationOffers(Screen, scale: 2.0);

        var messenger = new WeakReferenceMessenger();
        var logger = new RecordingLogger<GazeEngineAdapter>();
        var vm = new ActivationButtonViewModel(messenger);
        using var adapter = new GazeEngineAdapter(
            engine, settings, messenger, Dispatcher.CurrentDispatcher, logger, new StubControlFinder());

        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

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

        // The wrong state arrives before the fire message is pumped: the offer is live, but the state
        // no longer allows the completion, so the manager declines the named offer.
        engine.InteractionManager.OnActivationButtonDwelled();
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.ActionMenuVisible);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!logger.Lines.Any(l => l.Contains("Offer declined")) && DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(5);
        }

        Check.WithCustomMessage("a declined completion must not be logged as an activation")
            .That(logger.Lines.Any(l => l.Contains("Offer accepted"))).IsFalse();
        Check.WithCustomMessage("the decline's result reason must be in the line")
            .That(logger.Lines.Any(l => l.Contains("Offer declined #") && l.Contains("(declined)"))).IsTrue();
    }

    [StaFact]
    public void Refusals_LogTheDiagnosisLine_WithTheRawPAndTheReasonWord()
    {
        // F3 (corner fix, 2026-10-17): the adapter renders the engine's refusal event as the line
        // the next corner report is diagnosed from. Mutants: drop the subscription → no line;
        // change a reason word → the line's tail is wrong.
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        settings.Dwell.FixationRadiusPixels = 90;
        var provider = new TestFakes.FakeGazeProvider();
        using var engine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
        engine.ConfigureActivationOffers(Screen, scale: 2.0);

        var logger = new RecordingLogger<GazeEngineAdapter>();
        using var adapter = new GazeEngineAdapter(
            engine, settings, new WeakReferenceMessenger(), Dispatcher.CurrentDispatcher,
            logger, new StubControlFinder());

        // The first sample applies the geometry rebuild and attaches the engine.
        provider.Raise(new GazePoint(P.X, P.Y, 33_000, true));

        var im = engine.InteractionManager;
        im.OnFixationStarted(new PhysicalPoint(4060, 1200));   // T+20 past the right edge
        im.OnFixationSettled(new PhysicalPoint(4060, 1200));

        Check.WithCustomMessage("logged: " + string.Join(" | ", logger.Lines))
            .That(logger.Lines).Contains("Offer refused at P=(4060,1200): off-screen");
    }

    private sealed class StubControlFinder : IControlFinder
    {
        public ControlInfo? FindTarget(PhysicalPoint point) => null;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }

    /// <summary>Records every control-finder query and finds nothing, so the fallback keeps the
    /// completion on the pixel point (the plan's resolved-at-P observation).</summary>
    private sealed class RecordingControlFinder : IControlFinder
    {
        public List<PhysicalPoint> Queries { get; } = new();
        public ControlInfo? FindTarget(PhysicalPoint point)
        {
            Queries.Add(point);
            return null;
        }
    }
}
