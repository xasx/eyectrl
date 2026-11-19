namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

/// <summary>
/// F2 of the 18.11 exit-hang ticket: the OnExit step list is real, ordered, and each step's name is
/// published before the step runs, so the watchdog can name a step that never returns.
/// </summary>
public class ShutdownSequenceTests
{
    private const string BlockingStep = "stop password-field detector";

    [Fact]
    public void Run_StepBlockedOnANeverSetEvent_LetsTheWatchdogTerminateWithinTheBudget()
    {
        var time = new FakeTimeProvider();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var terminated = new ManualResetEventSlim(false);
        string? terminatedStep = null;

        var sequence = new ShutdownSequence(_ => { }, (_, _) => { });
        sequence.Add(ExitSequencePlan.StartWatchdogStep, () =>
            new ExitWatchdog(time, () => sequence.CurrentStep, step =>
            {
                terminatedStep = step;
                terminated.Set();
            }).Start());
        sequence.Add(BlockingStep, () =>
        {
            entered.Set();
            release.Wait(); // never set while the test waits: the step hangs exactly like OnExit
        });
        sequence.Add(ExitSequencePlan.ReleaseMutexStep, () => { });

        var runner = new Thread(sequence.Run) { IsBackground = true };
        try
        {
            runner.Start();
            Check.That(entered.Wait(TimeSpan.FromSeconds(10))).IsTrue(); // the blocking step really runs

            time.Advance(ExitWatchdog.DefaultBudget + TimeSpan.FromSeconds(1));

            Check.That(terminated.Wait(TimeSpan.FromSeconds(5))).IsTrue();
            Check.That(terminatedStep).IsEqualTo(BlockingStep);
        }
        finally
        {
            release.Set(); // let the blocked runner finish; a background thread must not outlive the test
        }
    }

    [Fact]
    public void Create_RealPlan_StartsWithTheWatchdogAndEndsWithTheMutexRelease()
    {
        using var services = new ServiceCollection().BuildServiceProvider();

        ShutdownSequence sequence = ExitSequencePlan.Create(
            new ShutdownDependencies { ServiceProvider = services },
            _ => { }); // a recorder, never production's FailFast

        IReadOnlyList<string> names = sequence.StepNames;

        Check.That(names[0]).IsEqualTo(ExitSequencePlan.StartWatchdogStep);
        Check.That(names[^1]).IsEqualTo(ExitSequencePlan.ReleaseMutexStep);
    }

    [Fact]
    public void Create_WithoutAServiceProvider_StillStartsWithTheWatchdogAndEndsWithTheMutexRelease()
    {
        // The duplicate-instance path exits before the DI container exists; the sequence must still
        // be watchdog-first/mutex-last (the mutex is the thing that strands the owner when it is held).
        ShutdownSequence sequence = ExitSequencePlan.Create(new ShutdownDependencies(), _ => { });

        IReadOnlyList<string> names = sequence.StepNames;

        Check.That(names[0]).IsEqualTo(ExitSequencePlan.StartWatchdogStep);
        Check.That(names[^1]).IsEqualTo(ExitSequencePlan.ReleaseMutexStep);
    }

    [Fact]
    public void Create_RealPlan_PinsTheWholeStepOrder()
    {
        using var services = new ServiceCollection().BuildServiceProvider();

        ShutdownSequence sequence = ExitSequencePlan.Create(
            new ShutdownDependencies { ServiceProvider = services },
            _ => { });

        string[] expected =
        [
            "start exit watchdog",
            "dispose splash handle",
            "signal shutting down",
            "stop heartbeat timer",
            "dispose setup notice watch",
            "dispose supervisor request service",
            "dispose display geometry watcher",
            "dispose tray icon",
            "dispose lag mark host",
            "dispose ring arm log",
            "dispose blocked-input coordinator",
            "dispose event coordinator",
            "unregister audio diagnostics",
            "stop gaze engine",
            "save settings",
            "persist adaptation state",
            "report ring quantization",
            "dispose gaze adapter",
            "dispose gaze engine",
            "dispose external dwell service",
            "deactivate adaptation runtime",
            "stop password-field detector",
            "dispose service provider",
            "dispose heartbeat publisher",
            "release single-instance mutex",
        ];

        IReadOnlyList<string> actual = sequence.StepNames;
        Check.That(actual).HasSize(expected.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Check.That(actual[i]).IsEqualTo(expected[i]);
        }
    }

    [Fact]
    public void Run_LogsAndPublishesEveryStep_AndEndsInTheNativeExitStep()
    {
        var logged = new List<string>();
        var sequence = new ShutdownSequence(logged.Add, (_, _) => { });
        var ran = new List<string>();
        sequence.Add("first", () => ran.Add("first"));
        sequence.Add("second", () => ran.Add("second"));

        sequence.Run();

        Check.That(logged).ContainsExactly("first", "second");
        Check.That(ran).ContainsExactly("first", "second");
        Check.That(sequence.CurrentStep).IsEqualTo(ShutdownSequence.NativeExitStep);
    }

    [Fact]
    public void Run_AStepThatThrows_DoesNotSkipLaterSteps()
    {
        var failures = new List<string>();
        var sequence = new ShutdownSequence(_ => { }, (name, _) => failures.Add(name));
        bool laterRan = false;
        sequence.Add("throws", () => throw new InvalidOperationException("boom"));
        sequence.Add("later", () => laterRan = true);

        sequence.Run();

        Check.That(laterRan).IsTrue();
        Check.That(failures).ContainsExactly("throws");
    }
}
