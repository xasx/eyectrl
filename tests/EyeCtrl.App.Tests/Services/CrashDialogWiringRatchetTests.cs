namespace EyeCtrl.App.Tests.Services;

using System.IO;
using EyeCtrl.App.Tests;

/// <summary>
/// W3(b) of crash-dialog-off-per-process: the production wiring is three entry points and no unit
/// test can run them (each is a real process start), so these source ratchets fail when the call is
/// dropped, is not the first statement, or the line's level wiring is lost. Fixup 01 (review-01 F1)
/// moved the App's call into its constructor - before the generated Main loads App.xaml - and F3
/// pins that success goes to Information and failure to Warning in all three processes. Each is a
/// required mutant kill from the ticket or the review.
/// </summary>
public class CrashDialogWiringRatchetTests
{
    private const string Disable = "CrashDialogPolicy.Disable()";

    private static string RepoFile(params string[] parts) =>
        File.ReadAllText(Path.Combine(
            new[] { SourceScanner.RepoRoot().FullName }.Concat(parts).ToArray()));

    /// <summary>The text between the method's opening brace and the call must be blank, i.e. it
    /// is the method's FIRST statement.</summary>
    private static void AssertFirstStatement(
        string source, string methodSignature, string call, string message)
    {
        int method = source.IndexOf(methodSignature, StringComparison.Ordinal);
        Check.WithCustomMessage($"{message}: the entry method is gone").That(method).IsStrictlyGreaterThan(-1);

        int brace = source.IndexOf('{', method);
        int callIndex = source.IndexOf(call, brace, StringComparison.Ordinal);
        Check.WithCustomMessage(message).That(callIndex).IsStrictlyGreaterThan(brace);

        // The call's own line must be the first line after the brace: everything before it can be
        // blank (the comment was stripped), so "var crashDialog =" is fine but any prior statement
        // is not.
        int lineStart = Math.Max(brace + 1, source.LastIndexOf('\n', callIndex) + 1);
        Check.WithCustomMessage(message + ": it must be on the method's first statement line")
            .That(source[(brace + 1)..lineStart].Trim()).IsEmpty();
    }

    /// <summary>
    /// The level wiring: success must be written at Information and failure at Warning, in that
    /// order. Review-01 r6 (the App's info lambda → <c>Log.Warning</c>) survived without this; the
    /// production Report path runs inside the real startup, so the order is pinned in source.
    /// </summary>
    private static void AssertInfoThenWarning(string source, int report, string message)
    {
        int length = Math.Min(source.Length - report, 240);
        string window = source.Substring(report, length);
        int info = window.IndexOf("line => Log.Information(", StringComparison.Ordinal);
        int warning = window.IndexOf("line => Log.Warning(", StringComparison.Ordinal);

        Check.WithCustomMessage(message + ": the success line must be logged at Information (INF)")
            .That(info).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(message + ": the failure line must be logged at Warning (WRN)")
            .That(warning).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(message + ": Information must be the first sink (the success line)")
            .That(info).IsStrictlyLessThan(warning);
    }

    [Fact]
    public void App_DisablesTheDialog_FirstInItsConstructor_AndLogsItAtInformation()
    {
        string source = SourceScanner.StripComments(RepoFile("src", "EyeCtrl.App", "App.xaml.cs"));

        // F1: the generated Main runs new App(), then InitializeComponent() (App.xaml, which merges
        // EyeCtrl.Controls' OverlayStyles.xaml), then Run() - so OnStartup is too late for a broken
        // Controls dll or a XAML parse error.
        AssertFirstStatement(
            source,
            "public App()",
            Disable,
            "App's parameterless constructor no longer sets WER no-UI as its first statement: the "
            + "generated Main loads App.xaml (and the merged Controls dictionaries) before OnStartup, "
            + "so a broken Controls dll or a XAML parse error would crash with the dialog on");

        int logging = source.IndexOf("LoggingInitializer.Initialize(logsDir);", StringComparison.Ordinal);
        int report = source.IndexOf("crashDialog.Report(", logging, StringComparison.Ordinal);
        Check.WithCustomMessage("App never reports the WER outcome once logging exists")
            .That(logging).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("App never reports the WER outcome once logging exists")
            .That(report).IsStrictlyGreaterThan(logging);

        AssertInfoThenWarning(source, report, "App's Report call");
    }

    [Fact]
    public void Supervisor_DisablesTheDialog_FirstInMain_AndLogsIt()
    {
        string source = SourceScanner.StripComments(RepoFile("src", "EyeCtrl.Supervisor", "Program.cs"));

        AssertFirstStatement(
            source,
            "private static int Main(string[] args)",
            Disable,
            "Supervisor Program.Main no longer sets WER no-UI as its first statement");

        int disable = source.IndexOf(Disable, StringComparison.Ordinal);
        int guard = source.IndexOf("new SingleInstanceGuard(", StringComparison.Ordinal);
        Check.WithCustomMessage(
                "the Supervisor sets WER no-UI after its single-instance guard: a duplicate start "
                + "can exit/FailFast before the flag is on.")
            .That(guard).IsStrictlyGreaterThan(disable);

        int logging = source.IndexOf("SupervisorLoggingInitializer.Initialize(logsDir);", StringComparison.Ordinal);
        int report = source.IndexOf("_crashDialog.Report(", logging, StringComparison.Ordinal);
        Check.WithCustomMessage("the Supervisor never reports the WER outcome once logging exists")
            .That(logging).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("the Supervisor never reports the WER outcome once logging exists")
            .That(report).IsStrictlyGreaterThan(logging);

        AssertInfoThenWarning(source, report, "the Supervisor's Report call");
    }

    [Fact]
    public void Setup_DisablesTheDialog_FirstInMain_BeforeTheWorkingDirectoryMove_AndLogsIt()
    {
        string source = SourceScanner.StripComments(RepoFile("src", "EyeCtrl.Setup", "Program.cs"));

        AssertFirstStatement(
            source,
            "public static int Main(string[] args)",
            Disable,
            "Setup Program.Main no longer sets WER no-UI as its first statement");

        int disable = source.IndexOf(Disable, StringComparison.Ordinal);
        int enter = source.IndexOf("SetupWorkingDirectory.Enter();", StringComparison.Ordinal);
        Check.WithCustomMessage(
                "Setup sets WER no-UI after SetupWorkingDirectory.Enter: a crash in the move can "
                + "still show the dialog.")
            .That(enter).IsStrictlyGreaterThan(disable);

        int logging = source.IndexOf("new CompositeSetupLog(", StringComparison.Ordinal);
        int report = source.IndexOf("crashDialog.Report(", logging, StringComparison.Ordinal);
        Check.WithCustomMessage("Setup never reports the WER outcome once its log exists")
            .That(logging).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("Setup never reports the WER outcome once its log exists")
            .That(report).IsStrictlyGreaterThan(logging);

        Check.WithCustomMessage(
                "Setup's success line must go to log.Info and the failure to log.Warn (review-01 F3)")
            .That(source[report..Math.Min(source.Length, report + 80)])
            .Contains("crashDialog.Report(log.Info, log.Warn)");
    }
}
