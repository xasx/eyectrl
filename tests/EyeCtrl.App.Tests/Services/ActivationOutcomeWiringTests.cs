namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;

/// <summary>
/// The composition root actually WIRES the outcome tracker into the adapter (salvaged from #322,
/// R3 review F2 / mutant O4; round-button B1). With the bootstrapper's <c>noteOutcomeAccepted: null</c>, the app would write no
/// <c>Activation outcome</c> line at all and every other suite stays green — so this drives one
/// accept through the REAL bootstrapped graph (the container's adapter, messenger and tracker,
/// resolved from <see cref="AppBootstrapper.BuildServiceProvider"/>), and asserts the outcome line
/// arrives through the process's static Serilog logger, which is where the production tracker
/// writes. The A3/A7 real-container pattern (BannerSlotWiringTests / AdaptationActivationShutdownTests);
/// window-free (no overlay service is resolved, no window shown).
/// </summary>
[Collection("WpfApplication")]
public class ActivationOutcomeWiringTests
{
    /// <summary>Captures the static logger's events during the accept (the Task-12 pattern).</summary>
    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    [StaFact]
    public void AnAcceptedActivation_ThroughTheBootstrappedContainer_WritesOneOutcomeLine()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var appData = TestAppDataDir.Create("activation-outcome-wiring");
        var settings = new EyeCtrlSettings();

        using var provider = AppBootstrapper.BuildServiceProvider(
            settings,
            settingsFilePath: Path.Combine(appData, "settings.json"),
            dispatcher: Dispatcher.CurrentDispatcher,
            isTestMode: true,
            appDataDir: appData);

        // The production wiring under test: resolving the adapter constructs it with the tracker
        // singleton's NoteAccepted (the bootstrapper's lambda resolves both, in that order).
        _ = provider.GetRequiredService<ActivationOutcomeLog>();
        var adapter = provider.GetRequiredService<GazeEngineAdapter>();
        Check.That(adapter).IsNotNull();

        // A live offer, without a gaze provider: the engine the GazeEngine would build for the
        // default settings on its first sample (ApplyPendingActivationOfferRebuild), attached to
        // the container's real InteractionManager.
        var im = provider.GetRequiredService<GazeEngine>().InteractionManager;
        im.AttachActivationOffers(new ActivationOfferEngine(
            new PhysicalRect(0, 0, 3840, 2400), 2.0,
            settings.Cursor.CircleSize, settings.Dwell.ActivationButtonDwellMs,
            settings.Dwell.FixationRadiusPixels));

        var p = new PhysicalPoint(2000, 1200);
        im.OnFixationStarted(p);
        im.OnFixationSettled(p);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        var offer = im.ActivationOffers!.CurrentOffer!;
        Check.WithCustomMessage("the settle must have created a live offer").That(offer).IsNotNull();

        var sink = new CapturingSink();
        var previousLogger = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(sink)
            .CreateLogger();
        try
        {
            // The completion the VM publishes; the container's adapter accepts it on the
            // container's messenger and notes the container's tracker.
            provider.GetRequiredService<IMessenger>().Send(new ActivationCompletedMessage(offer));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

            Check.WithCustomMessage("the accept must have opened the activation's menu state")
                .That(im.CurrentState).IsEqualTo(InteractionState.ActionMenuVisible);
            Check.WithCustomMessage("no outcome before the session ends")
                .That(sink.Events.Select(e => e.RenderMessage())
                    .Count(l => l.Contains("Activation outcome", StringComparison.Ordinal))).IsEqualTo(0);

            im.OnActionMenuDismissed();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

            var outcomes = sink.Events
                .Select(e => e.RenderMessage())
                .Where(l => l.Contains("Activation outcome", StringComparison.Ordinal))
                .ToList();
            Check.WithCustomMessage("exactly one outcome line through the composed tracker; sink: "
                    + string.Join(" | ", sink.Events.Select(e => e.RenderMessage())))
                .That(outcomes).HasSize(1);
            Check.That(outcomes[0]).Contains("Activation outcome #1 mode=None result=closed heldMs=");
        }
        finally
        {
            Log.Logger = previousLogger;
        }
    }
}
