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
        List<string>? launchLog = null) =>
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
            readyWait: TimeSpan.FromSeconds(1));

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
    public void EveryFailureOutcome_HasAUserFacingExplanation(RestartOutcome outcome)
    {
        // The user completed a dwell and the app is still here; "why" must be sayable.
        var reason = SelfRestartStrategy.Describe(outcome);
        Check.That(reason).IsNotEmpty();
        Check.That(reason).Contains("running");
    }
}
