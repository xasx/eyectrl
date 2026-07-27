namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Guards the DI seam the whole telemetry substrate rests on. Every instrumented surface takes
/// <c>IStruggleTelemetrySink? sink = null</c>, so if the registration is ever dropped the app degrades
/// to *silently collecting nothing* — no exception, no failing test. These assertions make that
/// regression loud.
/// </summary>
/// <remarks>
/// Deliberately only builds the container and resolves the sinks; it does NOT resolve the view models.
/// <c>AppBootstrapper</c> registers <c>WeakReferenceMessenger.Default</c>, so constructing the VM
/// singletons here would register live message handlers on the process-wide messenger and leak into
/// sibling tests. The companion constructor-contract guard in
/// <c>EyeCtrl.ViewModels.Tests.Adaptation.OverlayTelemetryWiringTests</c> covers the VM side without DI.
/// </remarks>
public class TelemetrySinkRegistrationTests
{
    // Telemetry is gated on AdaptationSettings.Enabled (§9.1), so the delivery assertions below opt in;
    // AdaptationDisabled_* cover the gated-off side.
    private static ServiceProvider BuildProvider(bool adaptationEnabled = true)
    {
        var settings = new EyeCtrlSettings();
        settings.Adaptation.Enabled = adaptationEnabled;
        return AppBootstrapper.BuildServiceProvider(
            settings,
            settingsFilePath: Path.Combine(Path.GetTempPath(), "eyectrl-telemetry-registration-test.json"),
            dispatcher: Dispatcher.CurrentDispatcher,
            isTestMode: true);
    }

    [Fact]
    public void BothSinkInterfaces_Resolve_ToTheSameDispatcherSink()
    {
        using var sp = BuildProvider();

        var struggle = sp.GetService<IStruggleTelemetrySink>();
        var quality = sp.GetService<IGazeQualitySink>();

        Check.That(struggle).IsNotNull();
        Check.That(quality).IsNotNull();
        // One instance must serve both paths, so both streams serialize onto the same thread.
        Check.That(struggle).IsInstanceOf<DispatcherTelemetrySink>();
        Check.That(ReferenceEquals(struggle, quality)).IsTrue();
    }

    [Fact]
    public void ResolvedSink_DeliversThrough_ToTheRegisteredBuffer()
    {
        using var sp = BuildProvider();

        // Dispatcher.CurrentDispatcher is this thread's, so DispatcherTelemetrySink forwards
        // synchronously (CheckAccess() == true) and no message pump is needed.
        var sink = sp.GetRequiredService<IStruggleTelemetrySink>();
        var buffer = sp.GetRequiredService<BufferingTelemetrySink>();

        sink.Report(new DwellOutcome(
            1_000, DwellContext.Toolbar, "wiring-probe", OutcomeKind.Activated, AbandonReason.None,
            1.0, new PhysicalPoint(0, 0), new PhysicalPoint(0, 0), 0, null));

        var snap = buffer.Snapshot();
        Check.That(snap).HasSize(1);
        Check.That(snap[0].TargetId).IsEqualTo("wiring-probe");
    }

    [Fact]
    public void GazeQualityAggregator_Resolves_AndFeedsTheRegisteredBuffer()
    {
        using var sp = BuildProvider();

        var aggregator = sp.GetRequiredService<GazeQualityAggregator>();
        var buffer = sp.GetRequiredService<BufferingTelemetrySink>();

        // Default window is 500 ms; two samples a full window apart close exactly one window.
        aggregator.OnSample(new GazePoint(100, 100, 0, true), fixationJitter: 5, fatigueScore: 0.1);
        aggregator.OnSample(new GazePoint(100, 100, 500_000, true), fixationJitter: 5, fatigueScore: 0.1);

        Check.That(buffer.SnapshotQuality()).HasSize(1);
    }

    [Fact]
    public void AdaptationDisabled_DropsOutcomes_BeforeTheBuffer()
    {
        // §9.1 "disabled = current cost": with adaptation off — the default — a reported outcome is dropped
        // at the DispatcherTelemetrySink, before the dispatcher marshal and before the buffer retains it.
        using var sp = BuildProvider(adaptationEnabled: false);

        var sink = sp.GetRequiredService<IStruggleTelemetrySink>();
        var buffer = sp.GetRequiredService<BufferingTelemetrySink>();

        sink.Report(new DwellOutcome(
            1_000, DwellContext.Toolbar, "wiring-probe", OutcomeKind.Activated, AbandonReason.None,
            1.0, new PhysicalPoint(0, 0), new PhysicalPoint(0, 0), 0, null));

        Check.That(buffer.Snapshot()).IsEmpty();
    }

    [Fact]
    public void AdaptationDisabled_AggregatorEmitsNothing()
    {
        // The per-sample half of the same gate: the aggregator does no windowing at all while disabled, so
        // the gaze thread carries only a delegate call and a bool test.
        using var sp = BuildProvider(adaptationEnabled: false);

        var aggregator = sp.GetRequiredService<GazeQualityAggregator>();
        var buffer = sp.GetRequiredService<BufferingTelemetrySink>();

        aggregator.OnSample(new GazePoint(100, 100, 0, true), fixationJitter: 5, fatigueScore: 0.1);
        aggregator.OnSample(new GazePoint(100, 100, 500_000, true), fixationJitter: 5, fatigueScore: 0.1);

        Check.That(buffer.SnapshotQuality()).IsEmpty();
    }

    [Fact]
    public void EnablingAtRuntime_StartsCollecting_WithoutRewiring()
    {
        // The gate is read per sample/report rather than latched at construction, so flipping the setting
        // takes effect on the live objects — no re-registration, no re-subscription.
        using var sp = BuildProvider(adaptationEnabled: false);

        var settings = sp.GetRequiredService<EyeCtrlSettings>();
        var aggregator = sp.GetRequiredService<GazeQualityAggregator>();
        var buffer = sp.GetRequiredService<BufferingTelemetrySink>();

        aggregator.OnSample(new GazePoint(100, 100, 0, true), 5, 0.1);
        Check.That(buffer.SnapshotQuality()).IsEmpty();

        settings.Adaptation.Enabled = true;

        // A fresh window starts at the first post-enable sample: the pre-enable sample was discarded, so
        // this pair closes exactly one window rather than one straddling the toggle.
        aggregator.OnSample(new GazePoint(100, 100, 100_000, true), 5, 0.1);
        aggregator.OnSample(new GazePoint(100, 100, 600_000, true), 5, 0.1);

        var quality = buffer.SnapshotQuality();
        Check.That(quality).HasSize(1);
        Check.That(quality[0].SampleCount).IsEqualTo(2); // not 3 — the disabled-stretch sample is gone
    }
}
