namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EyeCtrl.App.Tests;

/// <summary>
/// W3(b) of crash-dialog-off-per-process: the production wiring is three entry points and no unit
/// test can run them (each is a real process start), so these source ratchets fail when the call is
/// dropped, is not the first statement, runs after anything a static constructor or field
/// initializer puts in front of it, or the line's level wiring is lost. Fixup 01 (review-01 F1)
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

    /// <summary>
    /// Review-02 n2 of #380: the constructor-body ratchet above only sees what runs in that body —
    /// a <c>static App()</c> constructor would run BEFORE it, with every static field initializer
    /// running before the static constructor in turn. Anything user code does there happens while
    /// the WER crash dialog is still on, invisible to the first-statement rule. Fail on the
    /// construct itself: App must not grow a static constructor.
    /// </summary>
    [Fact]
    public void App_HasNoStaticConstructor_BeforeTheDialogIsDisabled()
    {
        string source = SourceScanner.StripComments(RepoFile("src", "EyeCtrl.App", "App.xaml.cs"));

        Check.WithCustomMessage(
                "a static App constructor exists: it (and every static field initializer) runs "
                + "BEFORE the instance constructor body the Disable() ratchet pins, so user code "
                + "would run while the WER crash dialog is still on.")
            .That(source.Contains("static App(", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>
    /// Review-02 n1 of #380: every field initializer on the class level — instance or static —
    /// runs BEFORE the constructor body, so the first-statement ratchet is blind to them (its own
    /// mutant, <c>Assembly.Load</c> in an initializer, survived the whole review). Anything with a
    /// call or a <c>new</c> is a side effect and must be on the allow-list. Today's one allocation
    /// is <c>RollingFaultBudget</c> (a struct holding two ints); <c>() =&gt; false</c> needs no
    /// entry — creating a delegate invokes nothing. Extending the list is a deliberate edit, like
    /// moving a statement past the Disable call. The scan reads comment-stripped source as text —
    /// a false flag (a string literal that looks like a call) fails visibly here and is a prompt
    /// to refine, never a silent pass.
    /// </summary>
    [Fact]
    public void App_FieldInitializers_ThatRunBeforeTheConstructorBody_StayTrivial()
    {
        string source = SourceScanner.StripComments(RepoFile("src", "EyeCtrl.App", "App.xaml.cs"));

        string[] allowList =
        [
            // The dispatcher-fault budget: allocation only, no I/O, no window, nothing that can
            // realistically throw before the WER flag is set (review-02 of #380, under F1).
            "RollingFaultBudget",
        ];

        var flagged = ClassLevelStatements(source, "public partial class App")
            .Where(s => IsSideEffectingInitializer(s))
            .ToList();

        foreach (var statement in flagged)
        {
            Check.WithCustomMessage(
                    $"a field initializer with a call or 'new' runs before the WER no-UI flag and "
                    + $"is not on the allow-list: << {statement.Trim()} >> — move it below the "
                    + $"constructor's first statement or allow-list it here deliberately.")
                .That(allowList.Any(statement.Contains)).IsTrue();
        }

        // The scan must not silently degrade into scanning nothing (a renamed class declaration
        // would do exactly that): today exactly one initializer is flagged and it is the known one.
        Check.WithCustomMessage(
                "the field-initializer scan did not see the RollingFaultBudget initializer it is "
                + "known to flag — its anchor or the scan is broken.")
            .That(flagged.Any(s => s.Contains("RollingFaultBudget", StringComparison.Ordinal)))
            .IsTrue();
    }

    /// <summary>
    /// One source statement that lives directly on the class level (brace depth 1): a field
    /// declaration, an expression-bodied member, or an extern declaration. These run before or
    /// regardless of the constructor body.
    /// </summary>
    private static List<string> ClassLevelStatements(string source, string classDeclaration)
    {
        int classIndex = source.IndexOf(classDeclaration, StringComparison.Ordinal);
        Check.WithCustomMessage("App.xaml.cs no longer declares the class this ratchet scans.")
            .That(classIndex).IsStrictlyGreaterThan(-1);

        int brace = source.IndexOf('{', classIndex);
        var statements = new List<string>();
        var depth = 0;
        int start = -1;
        for (int i = brace; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '{')
            {
                if (depth == 1 && start >= 0)
                {
                    // A brace directly on the class level while a statement is pending: a member
                    // body's brace just resets the scan, but a brace INSIDE an initializer (the
                    // pending statement already assigns) is a shape this textual scan cannot
                    // parse — fail visibly here rather than let the scan miss a side effect.
                    if (AssignmentIndex(source[start..i]) >= 0)
                    {
                        Check.WithCustomMessage(
                                $"a class-level initializer carries a brace, which this textual "
                                + $"scan cannot parse: << {source[start..Math.Min(i + 40, source.Length)]} "
                                + "… >> — refine the scan before trusting its result.")
                            .That(false).IsTrue();
                    }

                    start = -1;
                }

                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 1)
                    start = -1;
            }
            else if (depth == 1)
            {
                if (c == ';')
                {
                    if (start >= 0)
                        statements.Add(source[start..i]);
                    start = -1;
                }
                else if (start < 0 && !char.IsWhiteSpace(c))
                {
                    start = i;
                }
            }
        }

        return statements;
    }

    /// <summary>
    /// True for a class-level statement whose initializer has side effects: an assignment (not
    /// <c>=&gt;</c>, <c>==</c>, …) whose right-hand side allocates or calls something, skipping
    /// <c>const</c> (compile-time only), <c>extern</c> declarations (an attribute's named
    /// arguments are not runtime code), and expression-bodied members (<c>=&gt;</c> before the
    /// assignment).
    /// </summary>
    private static bool IsSideEffectingInitializer(string statement)
    {
        if (statement.Contains("extern", StringComparison.Ordinal))
            return false;

        int assignment = AssignmentIndex(statement);
        if (assignment < 0)
            return false;

        int arrow = statement.IndexOf("=>", StringComparison.Ordinal);
        if (arrow >= 0 && arrow < assignment)
            return false;

        if (statement.TrimStart().StartsWith("const", StringComparison.Ordinal))
            return false;

        string initializer = statement[(assignment + 1)..];
        return initializer.Contains("new ", StringComparison.Ordinal) || HasCall(initializer);
    }

    /// <summary>The first '=' that is an assignment: not '=>' and not part of '==', '+=', … .</summary>
    private static int AssignmentIndex(string statement)
    {
        for (int i = 0; i < statement.Length; i++)
        {
            if (statement[i] != '=')
                continue;

            if (i + 1 < statement.Length && statement[i + 1] == '>')
                continue;

            if (i > 0 && "=!<>+-*/%&|?:^".Contains(statement[i - 1]))
                continue;

            return i;
        }

        return -1;
    }

    /// <summary>True if any <c>(</c> directly follows an identifier char — a call or a typed <c>new(</c>.</summary>
    private static bool HasCall(string text)
    {
        for (int i = 1; i < text.Length; i++)
        {
            if (text[i] == '('
                && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_'))
            {
                return true;
            }
        }

        return false;
    }
}
