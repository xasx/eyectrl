namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Diagnostics;
using System.IO;
using System.Linq;

/// <summary>
/// The last-resort line a stuck exit writes: Serilog first (the sink is still open while a step hangs,
/// and it holds the day's file), then a direct append as the fallback for after
/// <c>Log.CloseAndFlush</c>. File-only: it never calls <see cref="Environment.FailFast(string)"/>.
/// <para>
/// Review-01 R1: the direct append alone was silently lost in exactly the hung case — the sink holds
/// <c>eyectrl-YYYYMMDD.log</c> with write access, so <c>File.AppendAllText</c> throws and the log ended
/// at <c>OnExit step: X</c> with no FTL line. This test installs the App's real sink arguments, lets a
/// real watchdog fire, and requires the FTL line to be on disk.
/// </para>
/// <para>
/// Review-02 N3: these tests swap the process-global <c>Log.Logger</c>, so they belong to an
/// exclusive collection — <c>WpfApplication</c> is <c>DisableParallelization = true</c>, which xUnit v3
/// runs beside no other test (the convention the other logger-swapping classes follow). They also run
/// one at a time within the class, so the two swaps cannot overlap.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class ExitStuckReportTests
{
    private const string BlockingStep = "stop password-field detector";

    [Fact]
    public void AppendStuckLine_AppendsOneLineToTheDaysLogFile()
    {
        string directory = TempTestDirectory.Create("exit-stuck-report");
        var now = new DateTime(2026, 11, 18, 21, 59, 26, 83);

        ExitStuckReport.AppendStuckLine(directory, "stop password-field detector", now);
        ExitStuckReport.AppendStuckLine(directory, "release single-instance mutex", now);

        string path = Path.Combine(directory, "eyectrl-20261118.log");
        Check.That(File.Exists(path)).IsTrue();

        string[] lines = File.ReadAllLines(path);
        Check.That(lines).HasSize(2);
        Check.That(lines[0]).Contains("21:59:26.083");
        Check.That(lines[0]).Contains("ExitWatchdog");
        Check.That(lines[0]).Contains("EyeCtrl exit stuck in step 'stop password-field detector'");
        Check.That(lines[1]).Contains("EyeCtrl exit stuck in step 'release single-instance mutex'");
    }

    [Fact]
    public void AppendStartupFailure_WritesOneWarningLine_ForAnUnexpectedAcquireFailure()
    {
        // F6 of fixup-01: the duplicate-instance exit runs before LoggingInitializer, so this day
        // file is the only place the cause of an unexpected acquisition failure can leave a trace.
        string directory = TempTestDirectory.Create("startup-failure-trace");

        ExitStuckReport.AppendStartupFailure(
            directory, new IOException("first line\nsecond line"));

        string[] lines = File.ReadAllLines(DayFilePath(directory));
        Check.That(lines).HasSize(1); // one physical line even when the message carries a newline
        Check.That(lines[0]).Contains("[WRN]");
        Check.That(lines[0]).Contains("Single-instance mutex acquisition failed unexpectedly");
        Check.That(lines[0]).Contains("IOException");
    }

    [Fact]
    public void FiredWatchdog_WithTheAppsRealSinkOpen_LeavesOneFtlLineNamingTheStep()
    {
        string directory = TempTestDirectory.Create("exit-stuck-report-sink");
        ILogger previous = Log.Logger;

        try
        {
            // The App's own sink configuration, on a temp directory. It holds the day's file with
            // write access — the state the exit is in while a step hangs.
            LoggingInitializer.Initialize(directory);
            Log.Information("OnExit step: {Step}", BlockingStep);
            Log.Information("OnExit step: {Step}", "dispose service provider");

            var time = new FakeTimeProvider();
            using var reported = new ManualResetEventSlim(false);
            new ExitWatchdog(time, () => BlockingStep, step =>
            {
                ExitStuckReport.Report(directory, step); // production's report, without the FailFast
                reported.Set();
            }).Start();

            time.Advance(ExitWatchdog.DefaultBudget + TimeSpan.FromSeconds(1));
            Check.That(reported.Wait(TimeSpan.FromSeconds(10))).IsTrue();

            // Serilog's file sink writes and flushes synchronously, so the line is on disk before
            // Report returns. Exactly one: the direct append in Report fails on the file the sink
            // still holds, which is the whole point of the Log.Fatal route.
            string[] lines = Directory.GetFiles(directory, "eyectrl-*.log")
                .SelectMany(ReadAllLinesShared)
                .ToArray();

            string[] stuck = lines
                .Where(line => line.Contains(
                    "EyeCtrl exit stuck in step '" + BlockingStep + "'", StringComparison.Ordinal))
                .ToArray();

            Check.WithCustomMessage("the day's log holds no stuck-exit line; the sink swallowed it")
                .That(stuck).HasSize(1);
            Check.That(stuck[0]).Contains("[FTL]");
            Check.That(lines).HasElementThatMatches(
                line => line.Contains("OnExit step: " + BlockingStep, StringComparison.Ordinal));
        }
        finally
        {
            // The static logger is process-global; leave it exactly as it was.
            Log.CloseAndFlush();
            Log.Logger = previous;
        }
    }

    /// <summary>
    /// Review-02 N1: the 2 s bound on the Serilog route. A step hung inside Serilog's own write lock
    /// would otherwise block the watchdog's report forever, and the FailFast would never run. The sink
    /// here blocks in <c>Emit</c>; <c>Report</c> must still return within the budget plus a little slack
    /// (it then takes the direct-append fallback).
    /// </summary>
    [Fact]
    public void Report_WhenTheSinkBlocks_ReturnsWithinTheBudget_InsteadOfWaitingForIt()
    {
        string directory = TempTestDirectory.Create("exit-stuck-report-blocked-sink");
        ILogger previous = Log.Logger;
        using var released = new ManualResetEventSlim(false);

        try
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Sink(new BlockingSink(released))
                .CreateLogger();

            var elapsed = Stopwatch.StartNew();
            // The inner Task.Wait is what turns an unbounded join/wait into a RED test instead of a
            // hung run: the honest bound is LogFatalBudget (2 s) plus the instant append.
            Task report = Task.Run(() =>
                ExitStuckReport.Report(directory, "stop password-field detector"));
            Check.WithCustomMessage("Report never returned; the Serilog route is unbounded")
                .That(report.Wait(TimeSpan.FromSeconds(10))).IsTrue();
            elapsed.Stop();

            Check.WithCustomMessage("Report outlasted the Log.Fatal budget")
                .That(elapsed.Elapsed).IsLessThan(ExitStuckReport.LogFatalBudget + TimeSpan.FromSeconds(2));
        }
        finally
        {
            released.Set(); // let the abandoned report thread finish instead of blocking for the run
            Log.CloseAndFlush();
            Log.Logger = previous;
        }
    }

    /// <summary>
    /// E2: the required order is stuck line → keeper release → FailFast. The release action is
    /// what a queued restart replacement waits on, so it must already have run — and it must run
    /// even though this is not the owner thread — before the process end can delay in WER.
    /// </summary>
    [Fact]
    public void Terminate_StuckLineThenKeeperReleaseThenFailFast()
    {
        string directory = TempTestDirectory.Create("exit-stuck-release-order");
        var events = new List<string>();

        ExitStuckReport.Terminate(
            directory,
            BlockingStep,
            releaseMutex: () =>
            {
                events.Add(StuckLineExists(directory)
                    ? "release-after-stuck-line"
                    : "release-before-stuck-line");
                return true; // the keeper released
            },
            failFast: message =>
            {
                events.Add("failfast");
                Check.That(message).Contains(BlockingStep);
            });

        // The first element also pins that the release ran: "release skipped" leaves only failfast.
        Check.That(events).ContainsExactly("release-after-stuck-line", "failfast");
        Check.That(ReadDayFile(directory).Contains(ExitStuckReport.ReleasedMessage, StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// E2 mutant guard: a throwing release must not skip <see cref="Environment.FailFast(string)"/>.
    /// The keeper is best-effort — the stuck process still has to end. F4: the log must not claim a
    /// release that never happened; it carries one WRN naming the failure instead.
    /// </summary>
    [Fact]
    public void Terminate_WhenTheReleaseThrows_StillFailsFast_AndLogsAWarning()
    {
        string directory = TempTestDirectory.Create("exit-stuck-release-throws");
        var events = new List<string>();

        ExitStuckReport.Terminate(
            directory,
            BlockingStep,
            releaseMutex: () => throw new InvalidOperationException("keeper unavailable"),
            failFast: _ => events.Add("failfast"));

        Check.That(events).ContainsExactly("failfast");

        string lines = ReadDayFile(directory);
        Check.WithCustomMessage("the log claims the mutex was released even though the release threw")
            .That(lines.Contains(ExitStuckReport.ReleasedMessage, StringComparison.Ordinal)).IsFalse();
        Check.That(lines.Contains("single-instance mutex release failed", StringComparison.Ordinal)).IsTrue();
        Check.That(lines.Contains("InvalidOperationException", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// F4 of fixup-01: the released line is evidence a reader will trust, so a bounded release that
    /// TIMED OUT must write the WRN (with "timed out"), never the "released" line.
    /// </summary>
    [Fact]
    public void Terminate_WhenTheReleaseTimedOut_LogsAWarningInsteadOfTheReleasedLine()
    {
        string directory = TempTestDirectory.Create("exit-stuck-release-timeout");
        var events = new List<string>();

        ExitStuckReport.Terminate(
            directory,
            BlockingStep,
            releaseMutex: () => false, // the keeper did not finish within ReleaseWait
            failFast: _ => events.Add("failfast"));

        Check.That(events).ContainsExactly("failfast");

        string lines = ReadDayFile(directory);
        Check.That(lines.Contains(ExitStuckReport.ReleasedMessage, StringComparison.Ordinal)).IsFalse();
        Check.That(lines.Contains("single-instance mutex release failed (timed out)", StringComparison.Ordinal)).IsTrue();
    }

    private static string DayFilePath(string directory)
        => Path.Combine(directory, $"eyectrl-{DateTime.Now:yyyyMMdd}.log");

    private static string ReadDayFile(string directory)
    {
        string path = DayFilePath(directory);
        if (!File.Exists(path)) return "";
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static bool StuckLineExists(string directory)
        => ReadDayFile(directory).Contains(
            "EyeCtrl exit stuck in step '" + BlockingStep + "'", StringComparison.Ordinal);

    /// <summary>
    /// Reads a log file the sink still holds open: Serilog opens the day file for write with
    /// <see cref="FileShare.Read"/>, so a plain reader is refused — a reader must also share write.
    /// </summary>
    private static IEnumerable<string> ReadAllLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    /// <summary>Stands in for a file sink blocked on its own write lock: <c>Emit</c> never returns
    /// until the test releases it.</summary>
    private sealed class BlockingSink : ILogEventSink
    {
        private readonly ManualResetEventSlim _release;

        internal BlockingSink(ManualResetEventSlim release) => _release = release;

        public void Emit(LogEvent logEvent) => _release.Wait();
    }
}
