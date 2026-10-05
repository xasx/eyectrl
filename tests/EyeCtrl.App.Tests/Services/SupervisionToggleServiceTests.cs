namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.App.Startup;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Settings;

/// <summary>
/// The settings row's turn-on/turn-off flows. Order is pinned because each step protects the
/// next: publishing before any adoption, launch evidence before persisting the opt-in, persist
/// before the Run-key handover, and the reachability confirm last. A failed turn-on must leave
/// the user exactly where they started — off, with their own launch registration — and must
/// disarm the live crash policy (see the *_CrashPolicy pins).
/// </summary>
public class SupervisionToggleServiceTests
{
    private sealed class FakeClock
    {
        public DateTime Now { get; private set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan by) => Now += by;
    }

    private sealed class FakeStartupRegistrar : IStartupRegistrar
    {
        public List<bool> Writes { get; } = new();
        public bool IsRegistered => false;
        public void SetStartOnBoot(bool enabled) => Writes.Add(enabled);
    }

    private sealed class Harness : IDisposable
    {
        public EyeCtrlSettings Settings { get; }
        public FakeStartupRegistrar Registrar { get; } = new();

        /// <summary>Every step the flow took, in order.</summary>
        public List<string> Steps { get; } = new();

        public bool SupervisorInstalled { get; set; } = true;
        public bool LaunchSucceeds { get; set; } = true;
        public bool Reachable { get; set; } = true;
        public bool SupervisorOwnsLaunch { get; set; } = true;
        public bool PersistThrows { get; set; }
        public bool ReconcileThrows { get; set; }
        public bool PublishingEnabled { get; private set; }
        public int StateChanges { get; private set; }
        public List<Action> Pending { get; } = new();

        public SupervisionToggleService Service { get; }
        public string Path { get; }

        public Harness(bool enabled = false, bool deferBackground = false)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"eyectrl_t04_{Guid.NewGuid()}.json");
            Settings = new EyeCtrlSettings();
            Settings.Supervision.Enabled = enabled;
            Settings.General.StartOnBoot = true;

            var clock = new FakeClock();
            var writer = new SupervisionEnabledWriter(Path);
            Action<bool> persist = value =>
            {
                Steps.Add($"persist:{value}");
                if (PersistThrows) throw new InvalidOperationException("the settings write failed");
                writer.Persist(value);
            };
            var bringUp = new SupervisorBringUp(
                Settings,
                enablePublishing: () => { PublishingEnabled = true; Steps.Add("publish"); },
                launchSupervisor: () => { Steps.Add("launch"); return LaunchSucceeds; },
                persistEnabled: persist,
                reconcileRunKey: ReconcileStep,
                tryReachSupervisor: () => { Steps.Add("probe"); return Reachable; },
                utcNow: () => clock.Now,
                sleep: clock.Advance);

            Action<Action> run = deferBackground
                ? work => Pending.Add(work)
                : work => work();

            Service = new SupervisionToggleService(
                Settings,
                bringUp,
                persist,
                supervisorInstalled: () => SupervisorInstalled,
                reconcileRunKey: ReconcileStep,
                runInBackground: run,
                postToUi: work => work());
            Service.StateChanged += () => StateChanges++;
        }

        private void ReconcileStep()
        {
            Steps.Add("runkey");
            if (ReconcileThrows) throw new InvalidOperationException("the registry write failed");
            Registrar.SetStartOnBoot(StartOnBootPolicy.ShouldSelfRegister(
                Settings.Supervision.Enabled, Settings.General.StartOnBoot, SupervisorOwnsLaunch));
        }

        public bool OnDiskEnabled() =>
            new SettingsService(Path, SettingsWriter.App).Load().Supervision.Enabled;

        public void Dispose()
        {
            File.Delete(Path);
            File.Delete(Path + ".tmp");
        }
    }

    [Fact]
    public void Enable_Reachable_PublishesLaunchesPersistsHandsOverThenConfirms()
    {
        using var h = new Harness(enabled: false);

        h.Service.RequestEnable();

        Check.That(h.Service.Enabled).IsTrue();
        Check.That(h.Service.InFlight).IsFalse();
        Check.That(h.OnDiskEnabled()).IsTrue();

        // The order, not just the end state: publish → launch → persist → Run-key → confirm.
        Check.That(h.Steps).ContainsExactly("publish", "launch", "persist:True", "runkey", "probe");

        // startOnBoot && a supervisor that demonstrably owns launch → the app hands the Run key
        // over (SetStartOnBoot(false)) and stops self-registering.
        Check.That(h.Registrar.Writes).ContainsExactly(false);
        Check.That(h.Service.StatusText).StartsWith("On");
    }

    [Fact]
    public void Enable_LaunchFailure_PersistsNothingAndKeepsTheRunKey()
    {
        using var h = new Harness(enabled: false);
        h.LaunchSucceeds = false;

        h.Service.RequestEnable();

        // Publishing stays on (harmless, and a late supervisor may still adopt), but a launch
        // that started no process must leave no trace: no persist, no Run-key handover.
        Check.That(h.Steps).ContainsExactly("publish", "launch");
        Check.That(h.Registrar.Writes).IsEmpty();
        Check.That(h.Service.Enabled).IsFalse();
        Check.That(h.OnDiskEnabled()).IsFalse();
        Check.That(h.Service.StatusText).Contains("could not be started");
    }

    [Fact]
    public void Enable_StartedButNeverReachable_RollsBackMemoryDiskAndRunKey()
    {
        using var h = new Harness(enabled: false);
        h.Reachable = false;

        h.Service.RequestEnable();

        // The bring-up ran in full — persist then handover — and the failed confirm undid both:
        // back to false on disk, and self-registration restored.
        Check.That(h.Steps.GetRange(0, 4))
            .ContainsExactly("publish", "launch", "persist:True", "runkey");
        Check.That(h.Steps.GetRange(h.Steps.Count - 2, 2)).ContainsExactly("persist:False", "runkey");
        Check.That(h.Registrar.Writes).ContainsExactly(false, true);
        Check.That(h.Service.Enabled).IsFalse();
        Check.That(h.OnDiskEnabled()).IsFalse();
        Check.That(h.Service.StatusText).Contains("did not answer");
    }

    [Fact]
    public void Disable_PersistsFalseAndRestoresTheRunKey()
    {
        using var h = new Harness(enabled: true);

        h.Service.RequestDisable();

        Check.That(h.Service.Enabled).IsFalse();
        Check.That(h.OnDiskEnabled()).IsFalse();
        Check.That(h.Steps).ContainsExactly("persist:False", "runkey");
        Check.That(h.Registrar.Writes).ContainsExactly(true);   // supervised=false → self-register
        Check.That(h.Service.StatusText).Contains("not restarted automatically");
    }

    [Fact]
    public void StatusText_Off_StatesTheStandDownWorldPlainly()
    {
        // F2 pin: the Off text describes the world where the Supervisor re-reads the flag at its
        // recovery decisions and stands down (ticket 06). No "keeps watching" promise.
        using var h = new Harness(enabled: false);

        Check.That(h.Service.StatusText).IsEqualTo(
            "Off \u2014 if EyeCtrl stops responding or crashes, it is not restarted automatically.");
    }

    [Fact]
    public void Disable_WhenPersistFails_KeepsTheLiveFlagOn()
    {
        // F5/R5 pin: the failure is at the WRITE, so disk still says enabled and the live flag
        // must keep matching it — the crash policy describes the recovery that exists.
        using var h = new Harness(enabled: true);
        h.PersistThrows = true;

        h.Service.RequestDisable();

        Check.That(h.Service.Enabled).IsTrue();
        Check.That(h.Service.StatusText).Contains("still on");
    }

    [Fact]
    public void Disable_WhenReconcileFails_KeepsSupervisionOffAndSaysSo()
    {
        // F5 pin: the persist landed (disk false), so a reconcile failure AFTER it must NOT
        // re-arm the live flag — kill-for-respawn against a disk that says unsupervised is
        // exactly the shape the split try exists to prevent.
        using var h = new Harness(enabled: true);
        h.ReconcileThrows = true;

        h.Service.RequestDisable();

        Check.That(h.Service.Enabled).IsFalse();
        Check.That(h.OnDiskEnabled()).IsFalse();
        Check.That(h.Service.StatusText).Contains("launch-at-logon could not be updated");
    }

    [Fact]
    public void ARequestWhileAnotherIsInFlight_IsIgnored()
    {
        using var h = new Harness(enabled: false, deferBackground: true);

        h.Service.RequestEnable();
        h.Service.RequestEnable();    // dropped by the latch
        h.Service.RequestDisable();   // also dropped: turning off mid-bring-up would race it

        Check.That(h.Pending).HasSize(1);
        Check.That(h.Steps).IsEmpty();   // nothing ran until the deferred work executes

        h.Pending[0]();

        Check.That(h.Service.Enabled).IsTrue();
        Check.That(h.OnDiskEnabled()).IsTrue();
    }

    [Fact]
    public void Enable_WhenTheSupervisorIsNotInstalled_RefusesWithTheReason()
    {
        using var h = new Harness(enabled: false);
        h.SupervisorInstalled = false;

        Check.That(h.Service.IsAvailable).IsFalse();

        h.Service.RequestEnable();

        Check.That(h.Steps).IsEmpty();
        Check.That(h.Service.StatusText).Contains("not installed");
    }

    [Fact]
    public void Enable_Success_ArmsTheLiveCrashPolicy()
    {
        // Item 5 is LIVE: the crash-policy handler reads Settings.Supervision.Enabled at fault
        // time (App.xaml.cs), so a successful turn-on must disarm log-and-continue on THIS
        // instance — supervised means die so the supervisor's respawn owns recovery.
        using var h = new Harness(enabled: false);

        h.Service.RequestEnable();

        var budget = new RollingFaultBudget(10, TimeSpan.FromSeconds(60), nowMs: () => 0);
        Check.That(DispatcherCrashPolicy.ShouldKeepRunning(h.Settings.Supervision.Enabled, budget))
            .IsFalse();
    }

    [Fact]
    public void Enable_NotReachable_LeavesTheLiveCrashPolicyStandalone()
    {
        // The mirror pin: a failed turn-on must not leave kill-for-respawn armed with no watcher.
        using var h = new Harness(enabled: false);
        h.Reachable = false;

        h.Service.RequestEnable();

        var budget = new RollingFaultBudget(10, TimeSpan.FromSeconds(60), nowMs: () => 0);
        Check.That(DispatcherCrashPolicy.ShouldKeepRunning(h.Settings.Supervision.Enabled, budget))
            .IsTrue();
    }

    [Fact]
    public void Enable_AndDisable_RebuildTheRowOnBothEdges()
    {
        using var h = new Harness(enabled: false);

        h.Service.RequestEnable();
        Check.That(h.StateChanges).IsEqualTo(2);   // progress, then outcome

        h.Service.RequestDisable();
        Check.That(h.StateChanges).IsEqualTo(4);
    }
}
