namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using EyeCtrl.App.Services;
using NFluent;
using Xunit;

/// <summary>
/// The parent's half of the handoff. Its job is to be certain a replacement started
/// before the app that the user depends on goes away — and to leave that app running
/// whenever it cannot be certain.
/// </summary>
public class SelfRestartStrategyTests
{
    private sealed class FakeChild : IRestartChild
    {
        public bool HasExited { get; set; }
        public int Kills { get; private set; }
        public void Kill() => Kills++;
    }

    private static SelfRestartStrategy Build(
        string? exePath = @"C:\EyeCtrl\EyeCtrl.exe",
        FakeChild? child = null,
        bool launchThrows = false,
        bool readySignalled = true,
        List<string>? launchLog = null,
        bool isScoped = false) =>
        new(
            resolveExePath: () => exePath,
            launch: (path, args) =>
            {
                launchLog?.Add($"{path} {args}");
                if (launchThrows) throw new InvalidOperationException("boom");
                return child ?? new FakeChild();
            },
            // The seam receives the parent-owned handle; tests ignore it and return the
            // canned result. The signature matches the production wait, which waits on the
            // real named event — exercising it headlessly would mean standing up a second
            // process, so the outcome is made injectable instead.
            waitForReady: (_, _) => readySignalled,
            readyWait: TimeSpan.FromSeconds(1),
            isScoped: isScoped);

    [Fact]
    public void Begin_RefusesWhenScoped_AndNeverLaunchesOrCreatesTheReadyEvent()
    {
        // Second line of defence behind AppRestartCoordinator.HandleRestart. Begin is the ONLY
        // creator of Local\EyeCtrl.App.RestartReady, so refusing here is what actually removes that
        // machine-global name from a scoped instance's surface — the coordinator gate alone would
        // leave it reachable by any future caller of Begin.
        var log = new List<string>();

        var outcome = Build(launchLog: log, isScoped: true).Begin();

        Check.That(outcome).IsEqualTo(RestartOutcome.RefusedScoped);
        Check.That(log).IsEmpty();
    }

    [Fact]
    public void Begin_IsUnaffectedWhenNotScoped()
    {
        // Guards against the refusal being applied unconditionally.
        Check.That(Build(isScoped: false).Begin()).IsEqualTo(RestartOutcome.Started);
    }

    [Fact]
    public void Succeeds_WhenTheChildSignalsReady()
    {
        Check.That(Build().Begin()).IsEqualTo(RestartOutcome.Started);
    }

    [Fact]
    public void PassesTheHandoffArgument()
    {
        var log = new List<string>();
        Build(launchLog: log).Begin();

        Check.That(log).HasSize(1);
        Check.That(log[0]).Contains(RestartHandoff.Argument);
        Check.That(log[0]).StartsWith(@"C:\EyeCtrl\EyeCtrl.exe");
    }

    [Fact]
    public void Aborts_WhenTheExePathCannotBeResolved()
    {
        // Environment.ProcessPath is null in some hosting scenarios. Exiting on a guess
        // would be exiting into nothing.
        Check.That(Build(exePath: null).Begin()).IsEqualTo(RestartOutcome.NoExePath);
    }

    [Fact]
    public void Aborts_WhenTheLaunchThrows()
    {
        Check.That(Build(launchThrows: true).Begin()).IsEqualTo(RestartOutcome.LaunchFailed);
    }

    [Fact]
    public void Aborts_WhenTheChildExitsBeforeSignalling()
    {
        var child = new FakeChild { HasExited = true };
        Check.That(Build(child: child, readySignalled: false).Begin())
             .IsEqualTo(RestartOutcome.ChildExitedEarly);
    }

    [Fact]
    public void Aborts_AndKillsTheChild_WhenReadinessNeverArrives()
    {
        // The child is alive but stuck. Leaving it queued on the mutex would mean a
        // zombie that springs to life the next time this instance exits.
        var child = new FakeChild { HasExited = false };
        var outcome = Build(child: child, readySignalled: false).Begin();

        Check.That(outcome).IsEqualTo(RestartOutcome.ChildNotReady);
        Check.That(child.Kills).IsEqualTo(1);
    }

    [Fact]
    public void DoesNotKillAHealthyChild()
    {
        var child = new FakeChild();
        Build(child: child).Begin();
        Check.That(child.Kills).IsEqualTo(0);
    }

    [Theory]
    [InlineData(RestartOutcome.NoExePath)]
    [InlineData(RestartOutcome.LaunchFailed)]
    [InlineData(RestartOutcome.ChildExitedEarly)]
    [InlineData(RestartOutcome.ChildNotReady)]
    [InlineData(RestartOutcome.RefusedScoped)]
    public void EveryFailureOutcome_HasAUserFacingExplanation(RestartOutcome outcome)
    {
        // The user completed a dwell and the app is still here; "why" must be sayable.
        var reason = SelfRestartStrategy.Describe(outcome);
        Check.That(reason).IsNotEmpty();
        Check.That(reason).Contains("running");
    }

    [Fact]
    public void EveryOutcome_IsCoveredByTheDescriptionTheory()
    {
        // The theory above is hand-enumerated, so a new RestartOutcome member would silently go
        // undescribed and fall through to the generic default arm. This is the guard: it derives
        // the set from the enum rather than from a list someone has to remember to extend.
        var described = new[]
        {
            RestartOutcome.Started, RestartOutcome.NoExePath, RestartOutcome.LaunchFailed,
            RestartOutcome.ChildExitedEarly, RestartOutcome.ChildNotReady, RestartOutcome.RefusedScoped,
        };

        Check.That(Enum.GetValues<RestartOutcome>()).IsEquivalentTo(described);
    }

    [Fact]
    public void RefusedScoped_IsDescribedAsADecision_NotAFailure()
    {
        // The default arm says "Restart failed", which would be wrong for something declined on
        // purpose. This pins the explicit arm.
        var reason = SelfRestartStrategy.Describe(RestartOutcome.RefusedScoped);

        Check.That(reason).Contains("scoped");
        Check.That(reason).Not.Contains("Restart failed");
    }
}
