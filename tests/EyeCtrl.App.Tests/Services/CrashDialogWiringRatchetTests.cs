namespace EyeCtrl.App.Tests.Services;

using System.IO;
using EyeCtrl.App.Tests;

/// <summary>
/// W3(b) of crash-dialog-off-per-process: the production wiring is three entry points and no unit
/// test can run them (each is a real process start), so these source ratchets fail when the call is
/// dropped from any of them, moved after the App's single-instance guard, or its log line is
/// forgotten. Each is a required mutant kill from the ticket.
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

    [Fact]
    public void App_DisablesTheDialog_BeforeTheSingleInstanceGuard_AndLogsIt()
    {
        string source = SourceScanner.StripComments(RepoFile("src", "EyeCtrl.App", "App.xaml.cs"));

        int onStartup = source.IndexOf("protected override void OnStartup(", StringComparison.Ordinal);
        int disable = source.IndexOf(Disable, onStartup, StringComparison.Ordinal);
        int guard = source.IndexOf("new SingleInstanceGuard(", onStartup, StringComparison.Ordinal);

        Check.That(onStartup).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "App.OnStartup no longer sets WER no-UI: a crash before the guard would show a "
                + "dialog the gaze-only owner cannot dismiss.")
            .That(disable).IsStrictlyGreaterThan(onStartup);
        Check.WithCustomMessage(
                "App.OnStartup sets WER no-UI after the single-instance guard: the duplicate-exit "
                + "and restart paths can FailFast before the flag is on.")
            .That(guard).IsStrictlyGreaterThan(disable);

        int logging = source.IndexOf("LoggingInitializer.Initialize(logsDir);", StringComparison.Ordinal);
        int report = source.IndexOf("crashDialog.Report(", logging, StringComparison.Ordinal);
        Check.WithCustomMessage("App never reports the WER outcome once logging exists")
            .That(logging).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("App never reports the WER outcome once logging exists")
            .That(report).IsStrictlyGreaterThan(logging);
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
    }
}
