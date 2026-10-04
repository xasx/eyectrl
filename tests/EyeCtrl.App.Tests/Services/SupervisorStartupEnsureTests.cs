namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Settings;

/// <summary>
/// Ticket 07: the write-free startup/retry ensure. It launches only when enabled and nothing
/// answers, never persists or reconciles the Run key (it has no dependency for either, so that is
/// structural), and stops after the bounded wait. Fakes only — no real launch, no real pipe.
/// </summary>
public class SupervisorStartupEnsureTests
{
    private sealed class FakeClock
    {
        public DateTime Now { get; private set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan by) => Now += by;
    }

    private sealed class Harness
    {
        public EyeCtrlSettings Settings { get; } = new();
        public List<string> Steps { get; } = new();
        public bool Reachable { get; set; }
        public bool LaunchSucceeds { get; set; } = true;
        public int Launches { get; private set; }

        /// <summary>Optional per-probe answers (FIFO); empty falls back to <see cref="Reachable"/>.</summary>
        public Queue<bool> ProbeResults { get; } = new();

        public SupervisorStartupEnsure Ensure { get; }

        public Harness(bool enabled)
        {
            Settings.Supervision.Enabled = enabled;
            var clock = new FakeClock();
            Ensure = new SupervisorStartupEnsure(
                Settings,
                enablePublishing: () => Steps.Add("publish"),
                launchSupervisor: () => { Launches++; Steps.Add("launch"); return LaunchSucceeds; },
                tryReachSupervisor: () =>
                {
                    Steps.Add("probe");
                    return ProbeResults.Count > 0 ? ProbeResults.Dequeue() : Reachable;
                },
                utcNow: () => clock.Now,
                sleep: clock.Advance);
        }
    }

    [Fact]
    public void EnabledAndUnreachable_LaunchesOnce_ThenReportsFailure()
    {
        var h = new Harness(enabled: true);

        Check.That(h.Ensure.Run()).IsFalse();

        Check.That(h.Steps.GetRange(0, 3)).ContainsExactly("publish", "probe", "launch");
        Check.That(h.Launches).IsEqualTo(1);
    }

    [Fact]
    public void EnabledAndAlreadyReachable_DoesNotLaunch()
    {
        var h = new Harness(enabled: true) { Reachable = true };

        Check.That(h.Ensure.Run()).IsTrue();

        Check.That(h.Steps).ContainsExactly("publish", "probe");
        Check.That(h.Launches).IsEqualTo(0);
    }

    [Fact]
    public void EnabledButLaunchFails_CompletesWithoutPolling()
    {
        var h = new Harness(enabled: true) { LaunchSucceeds = false };

        Check.That(h.Ensure.Run()).IsFalse();

        Check.That(h.Steps).ContainsExactly("publish", "probe", "launch");
        Check.That(h.Launches).IsEqualTo(1);
    }

    [Fact]
    public void LaunchedSupervisorThatComesUpOnALaterPoll_ReportsSuccess()
    {
        var h = new Harness(enabled: true);
        h.ProbeResults.Enqueue(false);   // the pre-launch probe
        h.ProbeResults.Enqueue(false);   // first poll
        h.ProbeResults.Enqueue(true);    // second poll

        Check.That(h.Ensure.Run()).IsTrue();

        Check.That(h.Launches).IsEqualTo(1);
        Check.That(h.Steps).ContainsExactly("publish", "probe", "launch", "probe", "probe");
    }

    [Fact]
    public void Disabled_DoesNothing()
    {
        var h = new Harness(enabled: false);

        Check.That(h.Ensure.Run()).IsFalse();

        Check.That(h.Steps).IsEmpty();
        Check.That(h.Launches).IsEqualTo(0);
    }
}
