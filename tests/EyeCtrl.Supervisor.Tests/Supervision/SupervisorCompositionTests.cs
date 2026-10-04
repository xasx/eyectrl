namespace EyeCtrl.Supervisor.Tests.Supervision;

using System.IO;
using EyeCtrl.Core.Settings;
using EyeCtrl.Supervisor.Supervision;
using NFluent;
using Xunit;

/// <summary>
/// Ticket 04 re-check 2 R2-1 (owned by ticket 06): the two <c>SupervisorApp</c> decisions that
/// carry the safety guarantees, driven through the production <see cref="SupervisorComposition"/>
/// factory (a WPF <c>Application</c> cannot be composed in a test).
/// <para>P1: the machine's enabled predicate reads the DISK through the tracker — never the
/// in-memory settings the setup window edits on open and on unsaved clicks — with a damaged file
/// falling back to the last disk value.</para>
/// <para>P2: the startup launch decision consults the app-instance probe, defaulting to
/// <see cref="SupervisorComposition.DefaultAppInstanceProbe"/>.</para>
/// </summary>
public class SupervisorCompositionTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 7, 16, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "eyectrl-supcomposition-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string _writerMutexName =
        @"Local\EyeCtrl.Settings.Writer.Test." + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        try { if (File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp"); } catch { }
    }

    private static EyeCtrlSettings Settings(bool enabled, bool startOnBoot = true)
    {
        var settings = new EyeCtrlSettings();
        settings.Supervision.Enabled = enabled;
        settings.General.StartOnBoot = startOnBoot;
        return settings;
    }

    // ── P1: the enabled predicate reads the disk ─────────────────────────────

    [Fact]
    public void Machine_WhenDiskSaysFalseAndMemorySaysTrue_StandsDownOnRecovery()
    {
        // The setup window has set the in-memory snapshot to true (an unsaved click, or a stale
        // startup Load), but the disk says false. The machine's predicate must follow the disk:
        // recovering here would be r1 F2 all over again.
        File.WriteAllText(_path, """{"supervision":{"enabled":false}}""");
        var composition = SupervisorComposition.Create(
            Settings(enabled: true), _path, appAlreadyRunning: () => false, writerMutexName: _writerMutexName);
        var machine = composition.Machine;

        machine.Handle(new LaunchRequest(T0));
        machine.Handle(new HeartbeatReceived(T0.AddSeconds(1)));
        machine.Handle(new HeartbeatStale(T0.AddSeconds(22)));

        Check.That(machine.State).IsEqualTo(SupervisorState.IdleWatch);
        Check.That(machine.ConsecutiveFailures).IsEqualTo(0);
        Check.That(machine.Commands).Not.Contains(SupervisorCommand.ForceKill);
        Check.That(machine.Commands).Not.Contains(SupervisorCommand.LaunchApp);
    }

    [Fact]
    public void Machine_WhenTheFileIsDamaged_FallsBackToTheLastDiskValue()
    {
        // Disk true (the last usable value), memory false. The damaged file must not flip recovery
        // off: the last DISK value wins, not the in-memory snapshot.
        File.WriteAllText(_path, """{"supervision":{"enabled":true}}""");
        var composition = SupervisorComposition.Create(
            Settings(enabled: false), _path, appAlreadyRunning: () => false, writerMutexName: _writerMutexName);
        Check.That(composition.EnabledTracker.Current).IsTrue();   // primes the last disk value
        File.WriteAllText(_path, "{ damaged");                      // e.g. a torn or truncated file

        var machine = composition.Machine;
        machine.Handle(new LaunchRequest(T0));
        machine.Handle(new HeartbeatReceived(T0.AddSeconds(1)));
        machine.Handle(new HeartbeatStale(T0.AddSeconds(22)));

        Check.That(machine.State).IsEqualTo(SupervisorState.Recovering);
        Check.That(machine.Commands).Contains(SupervisorCommand.ForceKill);
    }

    // ── P2: the startup decision consults the probe ──────────────────────────

    [Fact]
    public void Startup_WhenTheAppIsAlreadyRunning_DoesNotEmitTheLaunch()
    {
        var composition = SupervisorComposition.Create(
            Settings(enabled: true, startOnBoot: true), _path,
            appAlreadyRunning: () => true, writerMutexName: _writerMutexName);

        Check.That(composition.AppAlreadyRunning).IsTrue();
        Check.That(composition.ShouldEmitStartupLaunch).IsFalse();
    }

    [Fact]
    public void Startup_WhenNoAppIsRunning_EmitsTheLaunch()
    {
        var composition = SupervisorComposition.Create(
            Settings(enabled: true, startOnBoot: true), _path,
            appAlreadyRunning: () => false, writerMutexName: _writerMutexName);

        Check.That(composition.AppAlreadyRunning).IsFalse();
        Check.That(composition.ShouldEmitStartupLaunch).IsTrue();
    }

    [Fact]
    public void Startup_WhenDisabled_DoesNotEmitTheLaunch()
    {
        var composition = SupervisorComposition.Create(
            Settings(enabled: false, startOnBoot: true), _path,
            appAlreadyRunning: () => false, writerMutexName: _writerMutexName);

        Check.That(composition.ShouldEmitStartupLaunch).IsFalse();
    }

    [Fact]
    public void Startup_InjectedProbe_IsUsedVerbatim()
    {
        // Deterministic pin that an injected probe replaces the default (a default-only mutant
        // would otherwise pass on a box with no EyeCtrl running).
        Func<bool> injected = () => true;
        var composition = SupervisorComposition.Create(
            Settings(enabled: true), _path, appAlreadyRunning: injected, writerMutexName: _writerMutexName);

        Check.That(composition.AppAlreadyRunningProbe).IsSameReferenceAs(injected);
    }

    [Fact]
    public void Startup_DefaultProbe_IsTheProductionAppInstanceProbe()
    {
        // enabled=false short-circuits the probe, so the production app mutex is never opened.
        var composition = SupervisorComposition.Create(
            Settings(enabled: false), _path, appAlreadyRunning: null, writerMutexName: _writerMutexName);

        Check.That(composition.AppAlreadyRunningProbe).IsSameReferenceAs(SupervisorComposition.DefaultAppInstanceProbe);
    }
}
