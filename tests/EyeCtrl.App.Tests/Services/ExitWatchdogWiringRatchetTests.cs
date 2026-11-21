namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EyeCtrl.App.Tests;

/// <summary>
/// F3 of fixup-01 (review-01): the watchdog → keeper wiring is production-only, so no unit test can
/// run it (invoking the real terminate ends the process). These source ratchets fail when either
/// half is dropped — and each was a surviving mutant (r4: <c>App.OnExit</c> passes null instead of
/// the release lambda; r5: the production <c>Terminate</c> overload forwards null). Either one
/// silently brings the stranding back with an otherwise green suite.
/// </summary>
public class ExitWatchdogWiringRatchetTests
{
    private static string RepoFile(params string[] parts) =>
        File.ReadAllText(Path.Combine(
            new[] { SourceScanner.RepoRoot().FullName }.Concat(parts).ToArray()));

    [Fact]
    public void OnExit_PassesTheKeeperRelease_ToTerminate()
    {
        string source = SourceScanner.StripComments(
            RepoFile("src", "EyeCtrl.App", "App.xaml.cs"));

        int onExit = source.IndexOf("protected override void OnExit(", StringComparison.Ordinal);
        int terminate = source.IndexOf(
            "terminate: step => ExitStuckReport.Terminate(", onExit, StringComparison.Ordinal);
        int run = source.IndexOf("sequence.Run();", terminate, StringComparison.Ordinal);

        Check.That(onExit).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("OnExit no longer builds its step list with ExitStuckReport.Terminate")
            .That(terminate).IsStrictlyGreaterThan(onExit);
        Check.That(run).IsStrictlyGreaterThan(terminate);

        string wiring = source[terminate..run];
        Check.WithCustomMessage(
                "App.OnExit no longer passes the keeper release to ExitStuckReport.Terminate: a stuck "
                + "exit would FailFast while still holding the single-instance mutex (r4).")
            .That(wiring.Contains(
                "singleInstance.Release(SingleInstanceGuard.ReleaseWait)", StringComparison.Ordinal))
            .IsTrue();
    }

    [Fact]
    public void ProductionTerminate_ForwardsTheRelease_ToTheTestableOverload()
    {
        string source = Regex.Replace(
            SourceScanner.StripComments(RepoFile("src", "EyeCtrl.App", "Services", "ExitWatchdog.cs")),
            @"\s+", " ");

        Check.WithCustomMessage(
                "ExitStuckReport.Terminate no longer forwards releaseMutex to the testable overload: "
                + "the watchdog would FailFast without freeing the restart child (r5).")
            .That(source.Contains(
                "Terminate(string logsDirectory, string step, Func<bool>? releaseMutex = null) "
                + "=> Terminate(logsDirectory, step, releaseMutex,",
                StringComparison.Ordinal))
            .IsTrue();
    }
}
