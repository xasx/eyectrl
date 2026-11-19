namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Tests.TestSupport;
using System.IO;

/// <summary>
/// The last-resort line a stuck exit writes straight to the day's Serilog file (Serilog itself may
/// already be closed). File-only: it never calls <see cref="Environment.FailFast(string)"/>.
/// </summary>
public class ExitStuckReportTests
{
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
}
