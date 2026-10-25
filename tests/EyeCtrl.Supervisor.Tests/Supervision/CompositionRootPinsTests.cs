namespace EyeCtrl.Supervisor.Tests.Supervision;

using System.IO;
using System.Text.RegularExpressions;
using EyeCtrl.Tests.TestSupport;
using NFluent;
using Xunit;

/// <summary>
/// The composition-root one-liners the supervisor harnesses cannot reach (A7 fixup-02): Program.cs
/// is integration wiring that cannot be composed in a test, like
/// <see cref="SupervisorLanguageResolverTests"/>. Each pinned line has a mutant that the
/// behavioural tests cannot see because they model Program in a harness: the bound-pid filter
/// removed (PIDP), a feeder left untagged (TAG), and the <c>ClearPendingLaunch</c> case not
/// clearing the queued relaunch (CPLP). Source pins, not style: each regex is the production
/// statement that carries the safety property.
/// </summary>
public class CompositionRootPinsTests
{
    [Fact]
    public void TheExitIntake_IsWiredToBothTaggedFeeders_AndClearPendingLaunchClearsTheQueue()
    {
        var program = File.ReadAllText(
            Path.Combine(GermanDraft.RepoRoot(), "src", "EyeCtrl.Supervisor", "Program.cs"));
        var wireTime = File.ReadAllText(
            Path.Combine(GermanDraft.RepoRoot(), "src", "EyeCtrl.Supervisor", "Supervision", "WireTimeExit.cs"));

        Check.WithCustomMessage(
                "Program.HandleInput no longer vets exits with "
                + "`_exitIntake.Accepts(exit, _pc.BoundProcess)`: a late exit from a previous "
                + "instance or a duplicate would strike the machine.")
            .That(Regex.IsMatch(program,
                @"input\s+is\s+ProcessExited\s+\w+\s*&&\s*!\s*_exitIntake\s*\.\s*Accepts\s*\(\s*\w+\s*,\s*_pc\s*\.\s*BoundProcess\s*\)"))
            .IsTrue();

        Check.WithCustomMessage(
                "OnBoundProcessExited must tag the exit with proc.Id; an untagged exit bypasses the "
                + "bound-pid filter.")
            .That(Regex.IsMatch(program,
                @"new\s+ProcessExited\s*\(\s*DateTime\s*\.\s*UtcNow\s*,\s*proc\s*\.\s*Id\s*\)"))
            .IsTrue();

        Check.WithCustomMessage("WireTimeExit must tag its posted exit with the wire-time pid.")
            .That(Regex.IsMatch(wireTime,
                @"new\s+ProcessExited\s*\(\s*now\s*\(\s*\)\s*,\s*pid\s*\)"))
            .IsTrue();

        var clearCase = Regex.Match(program,
            @"case\s+SupervisorCommand\.ClearPendingLaunch\s*:(?<body>[\s\S]*?)break\s*;");
        Check.WithCustomMessage("Program.DrainCommands no longer handles ClearPendingLaunch.")
            .That(clearCase.Success).IsTrue();
        Check.WithCustomMessage(
                "The ClearPendingLaunch case must set `_pendingLaunchAt = null`, or the next tick can "
                + "launch a second instance behind the FallenBack retry.")
            .That(Regex.IsMatch(clearCase.Groups["body"].Value, @"_pendingLaunchAt\s*=\s*null\s*;"))
            .IsTrue();
    }
}
