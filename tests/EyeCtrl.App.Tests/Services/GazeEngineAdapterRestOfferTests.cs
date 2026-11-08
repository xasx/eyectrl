namespace EyeCtrl.App.Tests.Services;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging;
using NFluent;
using Xunit;

/// <summary>
/// Rest activation R1 at the adapter seam, under the DEFAULT settings (RestFill): the show message
/// carries the rest style with B = P and the rest area, and the offer lines carry <c>style=rest</c>
/// plus the new <c>rearm</c> refusal word. Window-free: no VM or window is attached.
/// </summary>
public class GazeEngineAdapterRestOfferTests
{
    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);
    private static readonly PhysicalPoint P = new(2000, 1200);

    [StaFact]
    public void ARestOffer_IsShownAtP_WithItsStyle_AndItsLinesSayStyleRest_ThenTheLatchLogsRearm()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        settings.Dwell.FixationRadiusPixels = 90;
        var provider = new TestFakes.FakeGazeProvider();
        using var engine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
        engine.ConfigureActivationOffers(Screen, scale: 2.0);

        var messenger = new WeakReferenceMessenger();
        ShowActivationButtonMessage? shown = null;
        messenger.Register<ShowActivationButtonMessage>(this, (_, message) => shown = message);
        var logger = new RecordingLogger<GazeEngineAdapter>();
        using var adapter = new GazeEngineAdapter(
            engine, settings, messenger, Dispatcher.CurrentDispatcher, logger, new StubControlFinder());

        // The first sample applies the geometry rebuild and attaches the (rest-style) engine.
        long us = 33_000;
        provider.Raise(new GazePoint(P.X, P.Y, us, true));
        var im = engine.InteractionManager;
        im.OnFixationStarted(P);
        im.OnFixationSettled(P);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        Check.That(shown).IsNotNull();
        Check.That(shown!.Style).IsEqualTo(OfferPlacementStyle.AtRestPoint);
        Check.That(shown.ButtonCenter).IsEqualTo(P);
        Check.That(shown.OfferPoint).IsEqualTo(P);
        Check.That(shown.HitRadiusPx).IsEqualTo(ActivationOfferEngine.RestAreaFactor * 90);
        string all() => "logged: " + string.Join(" | ", logger.Lines);
        Check.WithCustomMessage(all()).That(logger.Lines).Contains("Offer shown #1 P=(2000,1200) B=(2000,1200) style=rest");

        // Resting on: the ring fills and the offer fires.
        for (int i = 0; i < 26; i++)
        {
            us += 30_000;
            provider.Raise(new GazePoint(P.X, P.Y, us, true));
        }
        Check.WithCustomMessage(all()).That(logger.Lines).Contains("Offer fired #1 style=rest");

        // A teardown closes it; a settle at the same spot is refused by the latch.
        im.RequestActivationOfferClear();
        us += 30_000;
        provider.Raise(new GazePoint(P.X, P.Y, us, true));
        im.OnFixationSettled(P);

        Check.WithCustomMessage(all()).That(logger.Lines).Contains("Offer closed #1 style=rest");
        Check.WithCustomMessage(all()).That(logger.Lines).Contains("Offer refused at P=(2000,1200): rearm");
    }

    [Fact]
    public void TheRefusalWord_ForTheLatch_IsRearm()
        => Check.That(GazeEngineAdapter.RefusalReasonWord(ActivationOfferRefusalReason.Rearm)).IsEqualTo("rearm");

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
}
