namespace EyeCtrl.App.Tests.Services;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using NFluent;

public class AdaptationResetHandlerTests
{
    private const long Sec = 1_000_000;

    private static (AdaptationCoordinator Coord, EffectiveSettingsProvider Provider, BufferingTelemetrySink Sink) Make()
    {
        var settings = new EyeCtrlSettings();
        settings.Adaptation.Enabled = true;
        var sink = new BufferingTelemetrySink(capacity: 512);
        var monitor = new StruggleMonitor(sink, settings.EyeCare);
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var controller = new AdaptationController(provider, settings.Adaptation);
        var coord = new AdaptationCoordinator(monitor, controller, settings.Adaptation, new TestFakes.FakeGazeProvider(), new InteractionManager());
        return (coord, provider, sink);
    }

    private static DwellOutcome Abandon(long ts, double progress) => new(
        ts, DwellContext.ActionMenu, "t", OutcomeKind.Abandoned, AbandonReason.GazeLeft, progress,
        new PhysicalPoint(15, 0), new PhysicalPoint(0, 0), 30, null);

    private static void DriveToRelaxedTolerance(AdaptationCoordinator coord, BufferingTelemetrySink sink)
    {
        coord.Tick(); // ResetOnEnable (now == Unseeded → sessionStart excludes nothing)
        for (int i = 0; i < 4; i++)
            sink.Report(Abandon(i * 10 * Sec, progress: 0.8)); // gate SustainFailure
        coord.OnGazeSample(new GazePoint(0, 0, 40 * Sec, true)); coord.Tick(); // anchor relaxSince
        coord.OnGazeSample(new GazePoint(0, 0, 43 * Sec, true)); coord.Tick(); // 3 s sustain → relax
    }

    [Fact]
    public void ResetMessage_ClearsMomentaryAdaptation()
    {
        var (coord, provider, sink) = Make();
        var messenger = new WeakReferenceMessenger();
        using var handler = new AdaptationResetHandler(messenger, coord);
        handler.Start();

        DriveToRelaxedTolerance(coord, sink);
        Check.That(provider.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50) > 50.0).IsTrue();

        messenger.Send(new ResetAdaptationMessage()); // the toolbar/tray escape
        Check.That(provider.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsEqualTo(50.0);
    }

    [Fact]
    public void AfterDispose_MessageIsIgnored()
    {
        var (coord, provider, sink) = Make();
        var messenger = new WeakReferenceMessenger();
        var handler = new AdaptationResetHandler(messenger, coord);
        handler.Start();
        handler.Dispose(); // unregisters

        DriveToRelaxedTolerance(coord, sink);
        Check.That(provider.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50) > 50.0).IsTrue();

        messenger.Send(new ResetAdaptationMessage()); // no live handler → no reset
        Check.That(provider.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50) > 50.0).IsTrue();
    }
}
