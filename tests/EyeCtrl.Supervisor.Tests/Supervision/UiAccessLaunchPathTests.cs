namespace EyeCtrl.Supervisor.Tests.Supervision;

using System.ComponentModel;
using EyeCtrl.Core.Settings;
using EyeCtrl.Supervisor.Supervision;
using NFluent;
using Xunit;

/// <summary>
/// C9 S3's end of the launch-failure path: a uiAccess App that fails with Win32 8235 (a broken
/// signature or trust chain) must reach <c>FallbackAt</c> through the SAME route as any other
/// launch failure — the real <see cref="ProcessController"/> throws, the real
/// <see cref="SupervisionStateMachine"/> books the strike and the real
/// <see cref="SupervisionClock"/> retries with backoff until the crash budget is spent. No
/// process is started: the controller's factory throws instead of calling Process.Start.
/// </summary>
public class UiAccessLaunchPathTests
{
    [Fact]
    public void SignatureTrustFailure_ReachesFallbackAt_ViaTheExistingLaunchFailurePath()
    {
        var controller = new ProcessController(
            _ => throw new Win32Exception(UiAccessLaunchFailure.SignatureOrTrustFailure),
            _ => null);
        var host = new LaunchFailureHarness(controller);

        host.Startup();
        host.Run(seconds: 120);

        Check.WithCustomMessage(
                $"a 8235 launch never escalated: attempts={host.LaunchAttempts}, strikes={host.ConsecutiveFailures}, state={host.State}")
            .That(host.State).IsEqualTo(SupervisorState.FallenBack);
        Check.That(host.FallbackLaunches).IsEqualTo(1);
        // The existing path permits MaxRetries attempts, then escalates on the next failure.
        Check.That(host.LaunchAttempts).IsEqualTo(host.MaxRetries + 1);
        Check.That(host.ConsecutiveFailures).IsEqualTo(host.MaxRetries + 1);
    }

    /// <summary>Mirrors <c>SupervisorApp</c>: the clock tick owns the deferred launch and feeds the
    /// outcome, exactly as <c>Program.ISupervisionClockHost.TryLaunchPendingNow</c> does.</summary>
    private sealed class LaunchFailureHarness : ISupervisionClockHost
    {
        private readonly ProcessController _controller;

        public LaunchFailureHarness(ProcessController controller)
        {
            _controller = controller;
            Machine = new SupervisionStateMachine(
                Settings.MaxRetries, Settings.CrashWindowSeconds, () => true);
        }

        public SupervisionSettings Settings { get; } = new();
        public SupervisionStateMachine Machine { get; }
        public DateTime Now { get; private set; } = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        public DateTime? CleanExitRequestedAt => null;
        public DateTime? PendingLaunchAt { get; private set; }
        public int LaunchAttempts { get; private set; }
        public int FallbackLaunches { get; private set; }
        public int MaxRetries => Settings.MaxRetries;
        public int ConsecutiveFailures => Machine.ConsecutiveFailures;
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

        bool ISupervisionClockHost.SupervisionEnabled => true;

        void ISupervisionClockHost.ClearPendingLaunch() => PendingLaunchAt = null;

        bool ISupervisionClockHost.TryLaunchPendingNow()
        {
            LaunchAttempts++;
            try
            {
                _controller.Launch(@"C:\Program Files\EyeCtrl\EyeCtrl.App.exe", paused: false);
                return true;
            }
            catch (Exception)
            {
                // Program.TryLaunchPendingNow: any throw becomes LaunchFailed, never an escape.
                return false;
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
                }
            }
        }
    }
}
