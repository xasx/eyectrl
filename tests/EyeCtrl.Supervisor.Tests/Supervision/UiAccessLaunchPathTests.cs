namespace EyeCtrl.Supervisor.Tests.Supervision;

using System.ComponentModel;
using EyeCtrl.Core.Settings;
using EyeCtrl.Supervisor.Supervision;
using NFluent;
using Xunit;

/// <summary>
/// C9 S3 + fixup-01's launch-failure path, driven end to end through the REAL
/// <see cref="ProcessController"/> (its factory throws instead of starting a process), the real
/// <see cref="SupervisionStateMachine"/> and the real <see cref="SupervisionClock"/>.
///
/// <para>
/// A Win32 8235 (broken signature or trust) or 740 (a CreateProcess of the uiAccess exe) cannot
/// heal on a retry, so the supervisor must reach <c>FallenBack</c> on the FIRST attempt. Every
/// other launch failure keeps the strike + backoff path. The supervision-disabled stand-down is
/// still checked before the escalation, so a disabled Supervisor does not fall back either way.
/// </para>
/// </summary>
public class UiAccessLaunchPathTests
{
    [Fact]
    public void SignatureTrustFailure_ReachesFallbackAt_OnTheFirstAttempt()
    {
        var host = new LaunchFailureHarness(
            new Win32Exception(UiAccessLaunchFailure.SignatureOrTrustFailure));

        host.Startup();
        host.Run(seconds: 5);

        Check.WithCustomMessage(
                $"a 8235 did not escalate immediately: attempts={host.LaunchAttempts}, strikes={host.Strikes}, state={host.State}")
            .That(host.State).IsEqualTo(SupervisorState.FallenBack);
        Check.That(host.LaunchAttempts).IsEqualTo(1);
        Check.That(host.Strikes).IsEqualTo(1);
        Check.That(host.FallbackLaunches).IsEqualTo(1);

        // No further retry after the escalation: the dialog's Retry is the healing path.
        host.Run(seconds: 120);
        Check.That(host.LaunchAttempts).IsEqualTo(1);
        Check.That(host.FallbackLaunches).IsEqualTo(1);
    }

    [Fact]
    public void ElevationRequired_ReachesFallbackAt_OnTheFirstAttempt()
    {
        var host = new LaunchFailureHarness(new Win32Exception(UiAccessLaunchFailure.ElevationRequired));

        host.Startup();
        host.Run(seconds: 5);

        Check.That(host.State).IsEqualTo(SupervisorState.FallenBack);
        Check.That(host.LaunchAttempts).IsEqualTo(1);
        Check.That(host.FallbackLaunches).IsEqualTo(1);
    }

    [Fact]
    public void OrdinaryLaunchFailure_StillRetriesWithBackoff()
    {
        // A missing app exe (the installer-less default) is transient from the machine's point of
        // view: it keeps today's 0/5/15 s retries and only escalates when the crash budget is spent.
        var host = new LaunchFailureHarness(new InvalidOperationException("Process.Start threw"));

        host.Startup();
        host.Run(seconds: 2); // first tick schedules, second attempts

        Check.That(host.LaunchAttempts).IsEqualTo(1);
        Check.That(host.State).IsEqualTo(SupervisorState.Recovering);
        Check.That(host.FallbackLaunches).IsEqualTo(0);

        host.Run(seconds: 120);

        Check.That(host.State).IsEqualTo(SupervisorState.FallenBack);
        Check.That(host.LaunchAttempts).IsEqualTo(host.MaxRetries + 1);
        Check.That(host.FallbackLaunches).IsEqualTo(1);
    }

    [Fact]
    public void SignatureTrustFailure_WithSupervisionDisabled_StandsDownWithoutFallback()
    {
        // The enabled check stays FIRST in GoRecovering: a disabled Supervisor never escalates,
        // even for an unrecoverable failure, and books no strike.
        var host = new LaunchFailureHarness(
            new Win32Exception(UiAccessLaunchFailure.SignatureOrTrustFailure))
        {
            SupervisionEnabled = false,
        };

        host.Startup();
        host.Run(seconds: 5);

        Check.That(host.State).IsEqualTo(SupervisorState.IdleWatch);
        Check.That(host.LaunchAttempts).IsEqualTo(1);
        Check.That(host.Strikes).IsEqualTo(0);
        Check.That(host.FallbackLaunches).IsEqualTo(0);
        Check.That(host.PendingLaunchAt).IsNull();
    }

    [Fact]
    public void Classification_OnlyTheTwoUiAccessCodesAreUnrecoverable()
    {
        Check.That(UiAccessLaunchFailure.ClassifyLaunchFailure(
                new Win32Exception(UiAccessLaunchFailure.SignatureOrTrustFailure)))
            .IsEqualTo(LaunchAttemptResult.FailedUnrecoverable);
        Check.That(UiAccessLaunchFailure.ClassifyLaunchFailure(
                new Win32Exception(UiAccessLaunchFailure.ElevationRequired)))
            .IsEqualTo(LaunchAttemptResult.FailedUnrecoverable);

        Check.That(UiAccessLaunchFailure.ClassifyLaunchFailure(new Win32Exception(2)))
            .IsEqualTo(LaunchAttemptResult.Failed);
        Check.That(UiAccessLaunchFailure.ClassifyLaunchFailure(new InvalidOperationException()))
            .IsEqualTo(LaunchAttemptResult.Failed);
        Check.That(UiAccessLaunchFailure.ClassifyLaunchFailure(new Exception("boom")))
            .IsEqualTo(LaunchAttemptResult.Failed);
    }

    [Fact]
    public void DescribeFailure_GivesEachCodeItsOwnReason_AndNullForOrdinaryFailures()
    {
        var trust = UiAccessLaunchFailure.DescribeFailure(
            new Win32Exception(UiAccessLaunchFailure.SignatureOrTrustFailure));
        var elevation = UiAccessLaunchFailure.DescribeFailure(
            new Win32Exception(UiAccessLaunchFailure.ElevationRequired));

        Check.That(trust).IsNotNull();
        Check.That(elevation).IsNotNull();
        Check.That(trust!).Contains("8235");
        Check.That(trust).Contains("signature or trust");
        Check.That(elevation!).Contains("740");
        Check.That(elevation).Contains("CreateProcess");
        Check.WithCustomMessage("the 8235 and 740 reasons must not be the same line")
            .That(trust).IsNotEqualTo(elevation);

        Check.That(UiAccessLaunchFailure.DescribeFailure(new InvalidOperationException())).IsNull();
        Check.That(UiAccessLaunchFailure.DescribeFailure(new Win32Exception(2))).IsNull();
    }

    /// <summary>Mirrors <c>SupervisorApp</c>: the clock tick owns the deferred launch and feeds the
    /// outcome, exactly as <c>Program.ISupervisionClockHost.TryLaunchPendingNow</c> does, including
    /// the shared <see cref="UiAccessLaunchFailure.ClassifyLaunchFailure"/> decision.</summary>
    private sealed class LaunchFailureHarness : ISupervisionClockHost
    {
        private readonly ProcessController _controller;

        public LaunchFailureHarness(Exception launchFailure)
        {
            _controller = new ProcessController(_ => throw launchFailure, _ => null);
            Machine = new SupervisionStateMachine(
                Settings.MaxRetries, Settings.CrashWindowSeconds, () => SupervisionEnabled);
        }

        public SupervisionSettings Settings { get; } = new();
        public SupervisionStateMachine Machine { get; }
        public bool SupervisionEnabled { get; set; } = true;
        public DateTime Now { get; private set; } = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        public DateTime? CleanExitRequestedAt => null;
        public DateTime? PendingLaunchAt { get; private set; }
        public int LaunchAttempts { get; private set; }
        public int FallbackLaunches { get; private set; }
        public int MaxRetries => Settings.MaxRetries;
        public int Strikes => Machine.ConsecutiveFailures;
        public SupervisorState State => Machine.State;

        public void Startup() => Feed(new LaunchRequest(Now));

        public void Run(int seconds)
        {
            for (var i = 0; i < seconds; i++)
            {
                SupervisionClock.Tick(Now, this);
                Now = Now.AddSeconds(1);
            }
        }

        void ISupervisionClockHost.Handle(SupervisionInput input) => Feed(input);

        bool ISupervisionClockHost.SupervisionEnabled => SupervisionEnabled;

        void ISupervisionClockHost.ClearPendingLaunch() => PendingLaunchAt = null;

        LaunchAttemptResult ISupervisionClockHost.TryLaunchPendingNow()
        {
            LaunchAttempts++;
            try
            {
                _controller.Launch(@"C:\Program Files\EyeCtrl\EyeCtrl.App.exe", paused: false);
                return LaunchAttemptResult.Started;
            }
            catch (Exception ex)
            {
                // Program.TryLaunchPendingNow: classify, never let a throw escape.
                return UiAccessLaunchFailure.ClassifyLaunchFailure(ex);
            }
        }

        void ISupervisionClockHost.WireLaunchedProcess()
        {
            // No process starts in this harness.
        }

        void ISupervisionClockHost.TickFallenBackAutoRetry(DateTime now)
        {
            // The FallenBack dialog's countdown is not part of this scenario.
        }

        private void Feed(SupervisionInput input)
        {
            var previous = Machine.State;
            Machine.Handle(input);
            foreach (var command in Machine.Commands)
            {
                switch (command)
                {
                    case SupervisorCommand.LaunchApp:
                        // Production stamps the due time after the tick's captured now, so the
                        // zero-delay first relaunch waits for the next tick.
                        PendingLaunchAt = Now.AddMilliseconds(1)
                            + RelaunchDelayPolicy.For(previous, Machine.State, Machine.NextBackoffDelay);
                        break;
                    case SupervisorCommand.LaunchFallbackAt:
                        FallbackLaunches++;
                        break;
                    case SupervisorCommand.StandDown:
                        PendingLaunchAt = null;
                        break;
                }
            }
        }
    }
}
