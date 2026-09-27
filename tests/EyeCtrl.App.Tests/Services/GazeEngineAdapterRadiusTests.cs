namespace EyeCtrl.App.Tests.Services;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using NFluent;
using Xunit;

/// <summary>
/// Fixup-02 low item (reviewer A's M5): the show message is sized from the LIVE rule's radius, read by
/// the adapter from the engine at onset. Passing the raw setting instead survives every other test —
/// the coordinator test injects its own radius, and the Core test only covers the accessor — so this
/// moves the effective radius away from the setting and asserts the message follows it.
/// </summary>
public class GazeEngineAdapterRadiusTests
{
    [StaFact]
    public void ShowMessage_CarriesTheEffectiveRadius_NotTheRawSetting()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        settings.Dwell.FixationRadiusPixels = 30;
        settings.Adaptation.Enabled = true;
        var effective = new EffectiveSettingsProvider(settings.Adaptation);
        var provider = new TestFakes.FakeGazeProvider();
        using var engine = new GazeEngine(
            provider, new TestFakes.FakeInputSimulator(), settings, effectiveSettings: effective);

        var messenger = new WeakReferenceMessenger();
        ShowActivationButtonMessage? captured = null;
        messenger.Register<ShowActivationButtonMessage>(this, (_, message) => captured = message);

        using var adapter = new GazeEngineAdapter(
            engine, settings, messenger, Dispatcher.CurrentDispatcher,
            NullLogger<GazeEngineAdapter>.Instance, new StubControlFinder());

        // Full forgiving fill → +50% band edge = 15 → effective radius 45, applied on the next sample.
        effective.SetMomentaryFill(
            AdaptationKnob.FixationRadius, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        provider.Raise(new GazePoint(500, 500, 33_000, true));
        Check.That(engine.EffectiveFixationRadiusPixels).IsEqualTo(45.0);

        // The onset path: the InteractionManager raises Show synchronously, the adapter reads the live
        // radius there and dispatches the message.
        engine.InteractionManager.OnFixationStarted(new PhysicalPoint(700, 600));
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        Check.That(captured).IsNotNull();
        Check.WithCustomMessage("the message must carry the effective radius (45), not the setting (30)")
            .That(captured!.FixationRadiusPixels).IsEqualTo(45.0);
        Check.That(captured.Position.X).IsEqualTo(700);
        Check.That(captured.Position.Y).IsEqualTo(600);
    }

    private sealed class StubControlFinder : IControlFinder
    {
        public ControlInfo? FindTarget(PhysicalPoint point) => null;
    }
}
