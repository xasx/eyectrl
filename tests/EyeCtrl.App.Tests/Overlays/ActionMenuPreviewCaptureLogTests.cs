namespace EyeCtrl.App.Tests.Overlays;

using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

/// <summary>
/// B2: the action-menu preview's capture used to fail SILENTLY (a bare <c>catch {}</c>), so a
/// missing preview was indistinguishable from a menu that never had one. Failures now warn
/// (rate-limited), cancellations — a re-show or layer change superseding the capture — stay silent
/// by design, and a success writes one INFO line with the geometry actually used.
///
/// <para>
/// The capture is an injected seam and the ellipse insertion is exercised on a constructed window
/// with no <c>Show()</c>: nothing is painted on the owner's screen.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class ActionMenuPreviewCaptureLogTests
{
    [StaFact]
    public void Success_InsertsThePreview_AndLogsTheGeometry()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ActionMenuWindow();
        var sink = new CapturingSink();
        var previousLogger = Log.Logger;
        var previousContext = SynchronizationContext.Current;
        try
        {
            Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            window.PreviewCapture = (_, _) =>
                new CaptureResult(ManualSelectionCaptureBeforeShowTests.Solid(200, 128), 0, 0, 200);

            var task = window.CapturePreviewAsync(
                new Point(200, 200), 200, new PhysicalPoint(500, 500), 100, 1.0, "#FFFFFF",
                CancellationToken.None);
            PumpUntil(() => task.IsCompleted);

            Check.That(task.IsCompletedSuccessfully).IsTrue();
            Check.WithCustomMessage("the preview ellipse was not inserted into the menu canvas.")
                .That(window.MenuCanvas.Children.OfType<Ellipse>().Any(e => e.Fill is ImageBrush)).IsTrue();

            var info = sink.Messages.FirstOrDefault(m => m.StartsWith("Menu preview: ", StringComparison.Ordinal));
            Check.WithCustomMessage($"no preview INFO line; saw: {string.Join(" | ", sink.Messages)}")
                .That(info).IsNotNull();
            Check.WithCustomMessage($"the INFO line lacks the geometry: {info}")
                .That(info!).Contains("center=(100,100)").And.Contains("captureRadius=100px")
                .And.Contains("scale=1.00");
        }
        finally
        {
            Log.Logger = previousLogger;
            SynchronizationContext.SetSynchronizationContext(previousContext);
            window.Close();
        }
    }

    [StaFact]
    public void AFailedCapture_Warns_InsteadOfDisappearing()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ActionMenuWindow();
        var sink = new CapturingSink();
        var previousLogger = Log.Logger;
        var previousContext = SynchronizationContext.Current;
        try
        {
            Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            window.PreviewCapture = (_, _) => throw new InvalidOperationException("boom");

            var task = window.CapturePreviewAsync(
                new Point(200, 200), 200, new PhysicalPoint(500, 500), 100, 1.0, "#FFFFFF",
                CancellationToken.None);
            PumpUntil(() => task.IsCompleted);

            var warning = sink.Messages.FirstOrDefault(
                m => m.StartsWith("Menu preview capture FAILED", StringComparison.Ordinal));
            Check.WithCustomMessage($"a failed preview capture was silent; saw: {string.Join(" | ", sink.Messages)}")
                .That(warning).IsNotNull();
            Check.That(warning!).Contains("InvalidOperationException").And.Contains("boom");
            Check.That(window.MenuCanvas.Children.OfType<Ellipse>()
                .Any(e => e.Fill is ImageBrush)).IsFalse();
        }
        finally
        {
            Log.Logger = previousLogger;
            SynchronizationContext.SetSynchronizationContext(previousContext);
            window.Close();
        }
    }

    [StaFact]
    public void ACancelledCapture_StaysSilent()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ActionMenuWindow();
        var sink = new CapturingSink();
        var previousLogger = Log.Logger;
        var previousContext = SynchronizationContext.Current;
        try
        {
            Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var captureCalls = 0;
            window.PreviewCapture = (_, _) => { captureCalls++; return null; };

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var task = window.CapturePreviewAsync(
                new Point(200, 200), 200, new PhysicalPoint(500, 500), 100, 1.0, "#FFFFFF", cts.Token);
            PumpUntil(() => task.IsCompleted);

            Check.That(captureCalls).IsEqualTo(0);
            Check.WithCustomMessage($"a superseded capture must not log; saw: {string.Join(" | ", sink.Messages)}")
                .That(sink.Messages.Any(m => m.StartsWith("Menu preview", StringComparison.Ordinal))).IsFalse();
        }
        finally
        {
            Log.Logger = previousLogger;
            SynchronizationContext.SetSynchronizationContext(previousContext);
            window.Close();
        }
    }

    private static void PumpUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var started = Environment.TickCount64;
        while (!condition() && Environment.TickCount64 - started < timeoutMs)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        if (!condition()) throw new TimeoutException("the pumped dispatcher never completed the preview capture");
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<string> Messages { get; } = new();
        public void Emit(LogEvent logEvent) => Messages.Add(logEvent.RenderMessage());
    }
}
