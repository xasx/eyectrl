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
    /// Review-02 n2 of #380, review-01 F1c of D2: the constructor-body ratchet only sees what
    /// runs in that body — a <c>static App()</c> constructor would run BEFORE it, with every
    /// static field initializer running before the static constructor in turn — and App is
    /// partial, so the construct can hide in ANY of its files, not just App.xaml.cs (#380
    /// review-02's "single partial file" is now pinned by the scan itself). Fail on the
    /// construct itself: no App partial may declare a static constructor.
    /// </summary>
    [Fact]
    public void App_HasNoStaticConstructor_BeforeTheDialogIsDisabled()
    {
        var offenders = AppPartialSources()
            .Where(f => SourceScanner.StripComments(File.ReadAllText(f))
                .Contains("static App(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Check.WithCustomMessage(
                $"a static App constructor exists (in: {string.Join(", ", offenders)}): it (and "
                + "every static field initializer) runs BEFORE the instance constructor body the "
                + "Disable() ratchet pins, so user code would run while the WER crash dialog is "
                + "still on.")
            .That(offenders).IsEmpty();
    }

    /// <summary>
    /// Review-01 F1 of D2, seen-by-reading: a constructor initializer runs before the body —
    /// <c>public App() : this(Early())</c> executes while the WER crash dialog is still on —
    /// and <see cref="AssertFirstStatement"/> starts at the brace, so it cannot see it. No App
    /// constructor may carry a <c>: this(...)</c> / <c>: base(...)</c> chain.
    /// </summary>
    [Fact]
    public void App_ConstructorInitializers_DoNotRunCode_BeforeTheConstructorBody()
    {
        var offenders = AppPartialSources()
            .Select(f => (File: f, Text: SourceScanner.StripComments(File.ReadAllText(f))))
            .SelectMany(p => CtorInitializerUses(p.File, p.Text))
            .ToList();

        Check.WithCustomMessage(
                $"an App constructor initializer runs code before the constructor body the "
                + $"Disable() ratchet pins: {string.Join("; ", offenders)} — remove the chain or "
                + "move its work below the constructor's first statement.")
            .That(offenders).IsEmpty();
    }

    /// <summary>
    /// Review-02 n1 of #380, review-01 F1a/F1b/F1c of D2: every field initializer on the class
    /// level — instance or static, in ANY <c>partial class App</c> file — runs BEFORE the
    /// constructor body, so the first-statement ratchet is blind to them (its own mutant,
    /// <c>Assembly.Load</c> in an initializer, survived the whole review). The flagged set must
    /// equal EXACTLY the one known trivial initializer — a substring/type allow-list let
    /// <c>RollingFaultBudget _e2 = new(File.ReadAllText(...).Length, ...)</c>, real I/O, slip
    /// through (F1a) — so any addition is a deliberate edit of that single equality, like
    /// moving the statement below the Disable call. Detection: any '(' in the initializer
    /// counts as a call (an identifier call, a typed or target-typed <c>new</c>, and the
    /// generic form <c>Foo&lt;T&gt;()</c> — e.g. <c>Log.ForContext&lt;App&gt;()</c> — alike,
    /// F1b), except the parameter parens of a leading <c>() =&gt;</c> lambda whose body is
    /// still scanned; and a '.' counts as a member read, so the no-paren
    /// <c>= Some.SideEffectingProperty;</c> getter invocation is caught too. The one known
    /// statement: <see cref="RollingFaultBudget"/>'s budget — a sealed class holding an int, a
    /// long, a <c>Func&lt;long&gt;</c> and a <c>Queue&lt;long&gt;</c> it allocates; its
    /// constructor only assigns those fields, does no I/O, and lives in EyeCtrl.Core, the same
    /// assembly <c>CrashDialogPolicy</c> needs anyway (review-02 of #380 under F1; D2 review-01
    /// F2). The scan reads comment-stripped source as text — a false flag (a string literal
    /// that looks like a call) fails visibly here and is a prompt to refine, never a silent
    /// pass.
    /// </summary>
    [Fact]
    public void App_FieldInitializers_ThatRunBeforeTheConstructorBody_StayTrivial()
    {
        var flagged = AppPartialSources()
            .Select(f => SourceScanner.StripComments(File.ReadAllText(f)))
            .SelectMany(source => ClassLevelStatements(source, "partial class App"))
            .Where(IsSideEffectingInitializer)
            .Select(Normalize)
            .ToList();

        Check.WithCustomMessage(
                $"the class-level initializers that run before the WER no-UI flag are not exactly "
                + $"the one known trivial statement — got [{string.Join("; ", flagged)}] — move "
                + "anything else below the constructor's first statement, or edit this equality "
                + "deliberately.")
            .That(flagged).ContainsExactly(KnownTrivialInitializer);
    }

    /// <summary>
    /// The only class-level initializer with side effects today, normalized whitespace and
    /// WITHOUT the trailing semicolon (the scan captures up to it): the dispatcher-fault budget.
    /// The ratchet asserts the flagged set equals exactly this.
    /// </summary>
    private const string KnownTrivialInitializer =
        "private readonly RollingFaultBudget _dispatcherFaultBudget = new(10, TimeSpan.FromSeconds(60))";

    /// <summary>
    /// Every .cs file under src/EyeCtrl.App (obj/ and bin/ excluded) whose comment-stripped
    /// source declares <c>partial class App</c> — today exactly App.xaml.cs, and this scan is
    /// what pins "App is a single partial file" instead of assuming it (review-01 F1c).
    /// </summary>
    private static List<string> AppPartialSources()
    {
        string root = Path.Combine(SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App");
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (Path: f, Text: SourceScanner.StripComments(File.ReadAllText(f))))
            .Where(p => p.Text.Contains("partial class App", StringComparison.Ordinal))
            .Select(p => p.Path)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Check.WithCustomMessage(
                "no .cs file under src/EyeCtrl.App declares 'partial class App' — the ratchet's "
                + "anchor (class shape or project layout) has moved; refine the scan.")
            .That(files).Not.IsEmpty();
        return files;
    }

    /// <summary>Collapses every whitespace run to one space, trims both ends.</summary>
    private static string Normalize(string statement) =>
        string.Join(' ', statement.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The ': this(...)' / ': base(...)' chains of any App constructor in the file, with the
    /// file name — each one runs code before the body the first-statement ratchet pins.
    /// </summary>
    private static List<string> CtorInitializerUses(string file, string source)
    {
        var offenders = new List<string>();
        int index = 0;
        while ((index = source.IndexOf("App(", index, StringComparison.Ordinal)) >= 0)
        {
            if (index == 0 || !char.IsLetterOrDigit(source[index - 1]))
            {
                int openParen = index + "App(".Length - 1;
                int closeParen = source.IndexOf(')', openParen);
                int brace = source.IndexOf('{', openParen);
                if (closeParen >= 0 && brace >= 0 && closeParen < brace)
                {
                    string between = source[closeParen..brace];
                    if (between.Contains(": this(", StringComparison.Ordinal)
                        || between.Contains(": base(", StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetFileName(file)}: {Normalize(between)}");
                    }
                }
            }

            index += "App(".Length;
        }

        return offenders;
    }

    /// <summary>
    /// One source statement that lives directly on the class level (brace depth 1): a field
    /// declaration, an expression-bodied member, or an extern declaration. These run before or
    /// regardless of the constructor body.
    /// </summary>
    private static List<string> ClassLevelStatements(string source, string classDeclaration)
    {
        int classIndex = source.IndexOf(classDeclaration, StringComparison.Ordinal);
        Check.WithCustomMessage(
                "a partial App file no longer declares the class this ratchet scans (or lost the "
                + "declaration's opening brace).")
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
    /// <c>=&gt;</c>, <c>==</c>, …) whose right-hand side invokes or reads a member, skipping
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

        // 'const' sits among the declaration modifiers (statement text never contains it
        // otherwise): a const initializer is compile-time, not runtime code.
        if (statement.Contains("const ", StringComparison.Ordinal))
            return false;

        string initializer = statement[(assignment + 1)..];
        return HasCall(initializer) || HasMemberRead(initializer);
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

    /// <summary>
    /// True if the initializer invokes anything: every <c>(</c> counts — an identifier call, a
    /// typed or target-typed <c>new</c>, and the <c>(</c> of a generic call like
    /// <c>Foo&lt;T&gt;()</c> or <c>Log.ForContext&lt;App&gt;()</c> (review-01 F1b: the old
    /// identifier-before-paren rule could not see <c>&gt;(</c>) — except the parameter parens
    /// of a leading <c>() =&gt;</c> lambda, whose body is still scanned.
    /// </summary>
    private static bool HasCall(string initializer)
    {
        string trimmed = initializer.TrimStart();

        // A parameterless '() =>' lambda creation invokes nothing when created; skip its
        // parameter parens but scan the body: '() => Something()' must still be flagged.
        if (trimmed.StartsWith("()", StringComparison.Ordinal))
        {
            int arrow = trimmed.IndexOf("=>", StringComparison.Ordinal);
            if (arrow >= 0 && trimmed[2..arrow].All(char.IsWhiteSpace))
                return trimmed.IndexOf('(', arrow + 2) >= 0;
        }

        return trimmed.IndexOf('(') >= 0;
    }

    /// <summary>
    /// True if the initializer allocates explicitly or READS a member: a '.' covers the
    /// no-paren getter invocation <c>= Some.SideEffectingProperty;</c> (review-01 F1,
    /// seen-by-reading), 'new ' the explicit allocation (target-typed 'new(' is caught by
    /// <see cref="HasCall"/>'s paren rule).
    /// </summary>
    private static bool HasMemberRead(string initializer) =>
        initializer.Contains("new ", StringComparison.Ordinal)
        || initializer.Contains('.', StringComparison.Ordinal);
}
