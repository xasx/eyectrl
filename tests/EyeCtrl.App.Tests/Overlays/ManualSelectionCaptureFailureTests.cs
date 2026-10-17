namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

/// <summary>
/// G-A2 (M2): a failed capture used to be swallowed by a bare <c>catch {}</c> and the stale (or
/// empty) picture stayed for the whole session — "it didn't magnify at all", with no trace anywhere.
///
/// <para>
/// Two guarantees are pinned here: the picture is CLEARED on failure (never a stale screenshot
/// presented as this target), and the failure is LOUD — a WARNING carrying the exception type and
/// message — but rate-limited to one line per 30 s, with the reporter's suppression semantics
/// (Core's <c>HandlerFaultReporter</c>, already the app's one rate-limited fault channel).
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class ManualSelectionCaptureFailureTests
{
    [StaFact]
    public void AThrowingCapture_ClearsTheOldPicture_AndWarns()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ManualSelectionOverlayWindow();
        var sink = new CapturingSink();
        var previousLogger = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();

            // The stale picture the old code kept for the rest of the session.
            window.ManualSelectionBrush.ImageSource =
                ManualSelectionCaptureBeforeShowTests.Solid(4, 20);
            window.Capture = (_, _) => throw new InvalidOperationException("boom");

            window.UpdateCenter(new PhysicalPoint(1000, 1000), 170, 2.0);

            Check.WithCustomMessage("a failed capture kept the previous session's picture.")
                .That(window.ManualSelectionBrush.ImageSource).IsNull();
            var warning = sink.Messages.FirstOrDefault(
                m => m.StartsWith("Magnifier capture FAILED", StringComparison.Ordinal));
            Check.WithCustomMessage(
                    $"no 'Magnifier capture FAILED' warning was emitted; saw: {string.Join(" | ", sink.Messages)}")
                .That(warning).IsNotNull();
            Check.WithCustomMessage($"the warning must carry the exception type and message, saw: {warning}")
                .That(warning!).Contains("InvalidOperationException").And.Contains("boom");
        }
        finally
        {
            Log.Logger = previousLogger;
            window.Close();
        }
    }

    [StaFact]
    public void ANullCapture_ClearsTheOldPicture_AndWarns()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ManualSelectionOverlayWindow();
        var sink = new CapturingSink();
        var previousLogger = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();

            window.ManualSelectionBrush.ImageSource =
                ManualSelectionCaptureBeforeShowTests.Solid(4, 20);
            window.Capture = (_, _) => null;

            window.UpdateCenter(new PhysicalPoint(1000, 1000), 170, 2.0);

            Check.That(window.ManualSelectionBrush.ImageSource).IsNull();
            Check.WithCustomMessage(
                    $"a null capture must be loud too; saw: {string.Join(" | ", sink.Messages)}")
                .That(sink.Messages.Any(m => m.StartsWith("Magnifier capture FAILED", StringComparison.Ordinal)))
                .IsTrue();
        }
        finally
        {
            Log.Logger = previousLogger;
            window.Close();
        }
    }

    [StaFact]
    public void RepeatedFailures_LogAtMostOncePerWindow()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ManualSelectionOverlayWindow();
        var sink = new CapturingSink();
        var previousLogger = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
            window.Capture = (_, _) => throw new InvalidOperationException("boom");

            window.UpdateCenter(new PhysicalPoint(1000, 1000), 170, 2.0);
            window.UpdateCenter(new PhysicalPoint(1000, 1000), 170, 2.0);

            Check.WithCustomMessage(
                    $"two failures inside 30 s must cost one line; saw: {string.Join(" | ", sink.Messages)}")
                .That(sink.Messages.Count(
                    m => m.StartsWith("Magnifier capture FAILED", StringComparison.Ordinal))).IsEqualTo(1);
        }
        finally
        {
            Log.Logger = previousLogger;
            window.Close();
        }
    }

    [StaFact]
    public void ASuccessThenAFailure_ClearsThePictureStateInTheShownLine()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ManualSelectionOverlayWindow();
        try
        {
            var center = new PhysicalPoint(1000, 1000);

            window.Capture = (_, _) =>
                new CaptureResult(ManualSelectionCaptureBeforeShowTests.Solid(680, 128), 620, 200, 680);
            window.UpdateCenter(center, 170, 2.0);
            var fresh = window.BuildShownLine(center, 170, recapture: true);
            Check.That(fresh).Contains("picture=fresh");

            // F3: after a failed capture the next shown line must say none - a stale "fresh" next to
            // a cleared picture is a lying field line, and picture=none is the A4 discriminator.
            window.Capture = (_, _) => throw new InvalidOperationException("boom");
            window.UpdateCenter(center, 170, 2.0);
            var failed = window.BuildShownLine(center, 170, recapture: true);

            Check.WithCustomMessage($"a failure kept the previous picture state: {failed}")
                .That(failed).Contains("picture=none").And.Contains("captured=0x0px");
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<string> Messages { get; } = new();
        public void Emit(LogEvent logEvent) => Messages.Add(logEvent.RenderMessage());
    }
}
