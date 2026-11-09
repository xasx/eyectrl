namespace EyeCtrl.App.Tests.Startup;

using System.Text.RegularExpressions;
using EyeCtrl.App.Tests.Localization;

/// <summary>
/// App.xaml.cs is integration wiring that cannot be composed headlessly; pin the <c>--ring-arm</c>
/// block's shape: parsed beside <c>--paused</c>, both policies set from the launch, the startup line
/// ALWAYS written (naming whether ring events are on), the event log created through
/// <c>RingArmLog.Create</c> from the launch and the settings' <c>diagnostics.logRingEvents</c>, and
/// disposed in OnExit. (The LagMark host's source pins are the same pattern.)
/// </summary>
public class RingArmWiringTests
{
    private static string AppSource() =>
        LocalizationTestData.SourceFiles()
            .Single(file => file.Relative == "src/EyeCtrl.App/App.xaml.cs").Text;

    [Fact]
    public void TheArgument_IsParsedNextToPaused_AndBothPoliciesAreSet()
    {
        var text = AppSource();

        Check.WithCustomMessage("App.xaml.cs no longer parses --ring-arm")
            .That(Regex.IsMatch(text, @"RingArmArgument\s*\.\s*Parse\s*\(\s*e\s*\.\s*Args\s*\)")).IsTrue();
        Check.WithCustomMessage("InteractionManager's pin policy is no longer set from the launch")
            .That(Regex.IsMatch(text, @"im\s*\.\s*RingPinPolicy\s*=\s*ringArmLaunch\s*\.\s*PinPolicy")).IsTrue();
        Check.WithCustomMessage("the cursor VM's hold policy is no longer set from the launch")
            .That(Regex.IsMatch(text, @"cursorVm\s*\.\s*RingHoldPolicy\s*=\s*ringArmLaunch\s*\.\s*HoldPolicy")).IsTrue();
        Check.WithCustomMessage(
                "the unknown value no longer warns through RingArmLog.UnknownArmLine — the readout's "
                + "pattern matches that line (fixup 01 F1 / mutant X15)")
            .That(Regex.IsMatch(text,
                @"if\s*\(\s*ringArmLaunch\s*\.\s*UnknownValue\s+is\s+\{\s*\}\s+\w+\s*\)\s*\r?\n\s*Log\s*\.\s*Warning\s*\(\s*""\{RingArm\}""\s*,\s*RingArmLog\s*\.\s*UnknownArmLine\s*\(")).IsTrue();

        var lines = text.Replace("\r\n", "\n").Split('\n');
        int paused = Array.FindIndex(lines, l => l.Contains("IsPausedLaunch = e.Args.Contains"));
        int parse = Array.FindIndex(lines, l => Regex.IsMatch(l, @"RingArmArgument\s*\.\s*Parse\s*\(\s*e\s*\.\s*Args\s*\)"));
        Check.That(paused).IsStrictlyGreaterThan(-1);
        Check.That(parse).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("--ring-arm must be parsed next to --paused (OnStartup's argument block)")
            .That(Math.Abs(parse - paused)).IsStrictlyLessThan(5);
    }

    [Fact]
    public void TheStartupLine_IsWrittenForEveryArm_NotOnlyWhenTheArgumentWasGiven()
    {
        // Mutation: move the startup line inside `if (Specified)` → today's launches stop naming their
        // arm, and the readout can no longer spot a silent fallback after a supervisor relaunch.
        var text = AppSource();
        var statement = Regex.Match(text,
            @"(?m)^\s*(?<statement>Log\s*\.\s*Information\s*\(\s*""\{RingArm\}""\s*,\s*RingArmLog\s*\.\s*StartupLine\s*\(\s*ringArmLaunch\s*,"
            + @"\s*RingArmLog\s*\.\s*LogsEvents\s*\(\s*ringArmLaunch\s*,\s*settings\s*\.\s*Diagnostics\s*\)\s*\)\s*\)\s*;)\s*$");
        Check.WithCustomMessage("App.xaml.cs no longer writes the Ring arm startup line")
            .That(statement.Success).IsTrue();
        var above = text[..statement.Index].Replace("\r\n", "\n").TrimEnd('\n', ' ', '\t');
        var lastLine = above[(above.LastIndexOf('\n') + 1)..].Trim();
        Check.WithCustomMessage(
                $"the startup line is guarded by '{lastLine}': every launch must name its arm")
            .That(lastLine.StartsWith("if", StringComparison.Ordinal)
                  || lastLine.StartsWith("else", StringComparison.Ordinal)).IsFalse();
    }

    [Fact]
    public void TheEventLog_IsCreatedFromTheLaunchAndTheSettingsKey_AndIsDisposed()
    {
        // F2: Setup never passes --ring-arm, so the settings key must reach the one construction site.
        // Mutation: construct the log under `if (ringArmLaunch.Specified)` again, or pass a fresh
        // DiagnosticsSettings → a sitting gets no ring events.
        var text = AppSource();
        Check.WithCustomMessage("the RingArmLog is no longer created through RingArmLog.Create(launch, settings.Diagnostics, ...)")
            .That(Regex.IsMatch(text,
                @"_ringArmLog\s*=\s*RingArmLog\s*\.\s*Create\s*\(\s*ringArmLaunch\s*,\s*settings\s*\.\s*Diagnostics\s*,\s*im\s*,\s*cursorVm\s*,")).IsTrue();
        Check.WithCustomMessage("App.xaml.cs constructs a RingArmLog directly, past RingArmLog.Create")
            .That(text.Contains("new RingArmLog(", StringComparison.Ordinal)).IsFalse();
        Check.WithCustomMessage("OnExit no longer disposes the ring arm log")
            .That(Regex.IsMatch(text, @"_ringArmLog\s*\?\.\s*Dispose\s*\(\s*\)")).IsTrue();
    }
}
