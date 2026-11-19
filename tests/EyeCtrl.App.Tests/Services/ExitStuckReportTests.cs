namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Serilog;
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
/// </summary>
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
}
