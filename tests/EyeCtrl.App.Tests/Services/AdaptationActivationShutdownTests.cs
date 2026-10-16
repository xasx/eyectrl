namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

/// <summary>
/// LIVE-PASS finding (session 2026-10-16 16:24:42): at exit, DI disposal tears the resolved singletons
/// down one by one — <c>AdaptationPreviewService.Dispose</c> ran cleanly ("Adaptation preview service
/// stopped"), then <c>AdaptationActivation</c> was disposed and its teardown called
/// <c>StopRuntime(provider)</c>, whose FIRST line resolves from the ALREADY-DISPOSED provider:
/// <c>ObjectDisposedException</c>, contained and logged as the ERR "Failed to cleanly deactivate the
/// adaptation runtime", with the whole deactivation teardown skipped. The defect predates the feature
/// (that first resolve is old code); it first surfaced once adaptation got enabled in a live session.
///
/// These tests drive the REAL bootstrapped graph — adaptation enabled, runtime started, real overlay
/// HWNDs — and watch a captured Serilog sink through provider disposal:
///  - the live shape (provider disposed directly with the runtime active) must log no deactivation
///    failure;
///  - the <c>App.OnExit</c> shape (an explicit deactivation BEFORE provider disposal) must run the
///    full teardown and leave the later container disposal a no-op.
///
/// <see cref="Collection("WpfApplication")"/> + <c>[StaFact]</c>: starting the runtime constructs the
/// cue and preview overlay windows (real HWNDs — P9), which needs an STA thread and the merged overlay
/// resource dictionary.
/// </summary>
[Collection("WpfApplication")]
public class AdaptationActivationShutdownTests
{
    /// <summary>Captures the events logged during one disposal window (the Task-12 pattern).</summary>
    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private sealed class StartedApp : IDisposable
    {
        public ServiceProvider Provider { get; }
        public CapturingSink Sink { get; } = new();
        public AdaptationActivation Activation { get; }

        private readonly ILogger _previousLogger = Log.Logger;

        public StartedApp(string label)
        {
            WpfApplicationGate.EnsureApplicationWithTokens();
            var appData = TestAppDataDir.Create(label);
            var settings = new EyeCtrlSettings();
            settings.Adaptation.Enabled = true;

            Provider = AppBootstrapper.BuildServiceProvider(
                settings,
                settingsFilePath: Path.Combine(appData, "settings.json"),
                dispatcher: Dispatcher.CurrentDispatcher,
                isTestMode: true,
                appDataDir: appData);
            AppBootstrapper.ResolveEagerServices(Provider);

            // The production entry point: a real GazeEngine instance (never started — the
            // activation only holds it for the coordinator's tick-clock subscription).
            Activation = Provider.GetRequiredService<AdaptationActivation>();
            Activation.Start(Provider.GetRequiredService<GazeEngine>());
        }

        /// <summary>Runs one action with the captured sink installed as the process logger.</summary>
        public void Capture(Action act)
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Sink(Sink)
                .CreateLogger();
            try
            {
                act();
            }
            finally
            {
                Log.Logger = _previousLogger;
            }
        }

        public void Dispose()
        {
            Provider.Dispose();   // no-op if a test already disposed it
        }
    }

    [StaFact]
    public void ProviderDisposal_WithRuntimeActive_LogsNoDeactivationFailure()
    {
        // The live crash shape: the container disposes singletons with the runtime still active,
        // and by AdaptationActivation's turn the provider is already disposed — StopRuntime's
        // first resolve must not turn that into a logged ERR with the teardown skipped.
        using var app = new StartedApp("adaptation-shutdown-live");
        Check.That(app.Activation.IsActive).IsTrue();   // the runtime must be up, or this proves nothing

        app.Capture(() => app.Provider.Dispose());

        AssertNoDeactivationFailure(app.Sink);
    }

    [StaFact]
    public void ExplicitDeactivation_BeforeProviderDisposal_TeardownRunsCleanly()
    {
        // The App.OnExit shape (the fix's primary path): deactivating explicitly while the
        // provider is alive runs the FULL teardown, and the later container disposal of the same
        // singleton must be a no-op — Dispose is idempotent (Deactivate no-ops once inactive).
        using var app = new StartedApp("adaptation-shutdown-exit");
        Check.That(app.Activation.IsActive).IsTrue();

        app.Capture(() =>
        {
            app.Activation.Dispose();
            app.Provider.Dispose();
        });

        AssertNoDeactivationFailure(app.Sink);
        Check.WithCustomMessage(
                "the explicit deactivation must have run the full teardown before the provider " +
                "went away — captured: " + Describe(app.Sink.Events))
            .That(app.Sink.Events.Any(e =>
                e.Level == LogEventLevel.Information
                && e.MessageTemplate.Text.Contains(
                    "Adaptation runtime deactivated", StringComparison.Ordinal)))
            .IsTrue();
    }

    private static void AssertNoDeactivationFailure(CapturingSink sink)
    {
        var failures = sink.Events
            .Where(e => e.Level == LogEventLevel.Error
                     && e.MessageTemplate.Text.Contains(
                            "Failed to cleanly deactivate the adaptation runtime",
                            StringComparison.Ordinal))
            .ToList();

        Check.WithCustomMessage(
                "provider disposal with the adaptation runtime active must not log a " +
                "deactivation failure (the live-pass ERR; contained, but the whole teardown " +
                "was skipped) — captured: " + Describe(sink.Events))
            .That(failures).IsEmpty();
    }

    private static string Describe(IEnumerable<LogEvent> events) =>
        string.Join("; ", events.Select(e => $"{e.Level}: {e.RenderMessage()}"));
}
