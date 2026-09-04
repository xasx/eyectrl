namespace EyeCtrl.App.Tests.Services;

using System;
using System.IO;
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

    private static (AdaptationCoordinator Coord, EffectiveSettingsProvider Provider, BufferingTelemetrySink Sink,
                    AdaptationSettings Adaptation, string StatePath) Make()
    {
        var settings = new EyeCtrlSettings();
        settings.Adaptation.Enabled = true;
        var sink = new BufferingTelemetrySink(capacity: 512);
        var monitor = new StruggleMonitor(sink, settings.EyeCare);
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var controller = new AdaptationController(provider, settings.Adaptation);
        string statePath = Path.Combine(Path.GetTempPath(), "eyectrl_learner_reset_test_" + Guid.NewGuid() + ".json");
        var learner = new AdaptationLearner(provider, settings.Adaptation, stateFilePath: statePath);
        var coord = new AdaptationCoordinator(monitor, controller, learner, settings.Adaptation, new TestFakes.FakeGazeProvider(), new InteractionManager());
        return (coord, provider, sink, settings.Adaptation, statePath);
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
        var (coord, provider, sink, adaptation, _) = Make();
        var messenger = new WeakReferenceMessenger();
        using var handler = new AdaptationResetHandler(messenger, coord, new AdaptationLearner(provider, adaptation));
        handler.Start();

        DriveToRelaxedTolerance(coord, sink);
        Check.That(provider.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50) > 50.0).IsTrue();

        messenger.Send(new ResetAdaptationMessage()); // the toolbar/tray escape
        Check.That(provider.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsEqualTo(50.0);
    }

    [Fact]
    public void AfterDispose_MessageIsIgnored()
    {
        var (coord, provider, sink, adaptation, _) = Make();
        var messenger = new WeakReferenceMessenger();
        var handler = new AdaptationResetHandler(messenger, coord, new AdaptationLearner(provider, adaptation));
        handler.Start();
        handler.Dispose(); // unregisters

        DriveToRelaxedTolerance(coord, sink);
        Check.That(provider.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50) > 50.0).IsTrue();

        messenger.Send(new ResetAdaptationMessage()); // no live handler → no reset
        Check.That(provider.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50) > 50.0).IsTrue();
    }

    [Fact]
    public void LearnedResetMessage_ClearsLearnedOffsetsOnly()
    {
        var (coord, provider, sink, adaptation, statePath) = Make();
        try
        {
            DriveToRelaxedTolerance(coord, sink);
            provider.SetLearnedFill(AdaptationKnob.DwellTime, 0.3);
            var messenger = new WeakReferenceMessenger();
            using var handler = new AdaptationResetHandler(messenger, coord, learner: new AdaptationLearner(provider, adaptation));
            handler.Start();

            messenger.Send(new ResetLearnedOffsetsMessage());

            Check.That(provider.EnumerateLearnedFills()).IsEmpty();
            // Learned-only: the momentary relaxation is untouched (unlike ResetAdaptationMessage).
            Check.That(provider.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50) > 50.0).IsTrue();
        }
        finally { File.Delete(statePath); }
    }

    [Fact]
    public void LearnedResetMessage_AfterDispose_IsIgnored()
    {
        var (coord, provider, _, adaptation, statePath) = Make();
        try
        {
            provider.SetLearnedFill(AdaptationKnob.DwellTime, 0.3);
            var messenger = new WeakReferenceMessenger();
            var handler = new AdaptationResetHandler(messenger, coord, learner: new AdaptationLearner(provider, adaptation));
            handler.Start();
            handler.Dispose();

            messenger.Send(new ResetLearnedOffsetsMessage());

            Check.That(provider.EnumerateLearnedFills()).HasSize(1);
        }
        finally { File.Delete(statePath); }
    }
}
