namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.App.Startup;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Settings;

/// <summary>
/// Drives the production composition seam against a REAL settings file. Every other App test
/// composes supervision in memory, which is how the original defect — the App's persist discarded
/// by the App writer — survived the suite; these tests exercise the factory <c>App.xaml.cs</c>
/// calls, so the sanctioned write path and the live Run-key predicate are pinned at their source.
/// </summary>
public class SupervisionCompositionTests
{
    private sealed class FakeStartupRegistrar : IStartupRegistrar
    {
        public List<bool> Writes { get; } = new();
        public bool IsRegistered => true;
        public void SetStartOnBoot(bool enabled) => Writes.Add(enabled);
    }

    private sealed class Harness : IDisposable
    {
        public EyeCtrlSettings Settings { get; }
        public FakeStartupRegistrar Registrar { get; } = new();
        public SupervisorBringUp BringUp { get; }
        public SupervisionToggleService Toggle { get; }
        public string Path { get; }

        public Harness()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"eyectrl_t04_comp_{Guid.NewGuid()}.json");

            // A VALID on-disk Supervision section with enabled=false — the user's file shape.
            // (The App writer writes the whole snapshot on a fresh file, defaults included.)
            new SettingsService(Path, SettingsWriter.App).Save(new EyeCtrlSettings());

            Settings = new SettingsService(Path, SettingsWriter.App).Load();
            Settings.General.StartOnBoot = true;

            (BringUp, Toggle) = SupervisionComposition.Create(
                Settings,
                Path,
                Registrar,
                enablePublishing: () => { },
                launchSupervisor: () => true,
                tryReachSupervisor: () => true,
                isSupervisorInstalled: () => true,
                isLaunchRegistered: () => true,
                runInBackground: work => work(),
                postToUi: work => work());
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
    public void ToggleOn_PersistsThroughTheSanctionedPath_AndHandsOverTheRunKey()
    {
        using var h = new Harness();

        h.Toggle.RequestEnable();

        Check.That(h.Settings.Supervision.Enabled).IsTrue();
        Check.That(h.OnDiskEnabled()).IsTrue();
        // supervised && a registered launch owner && StartOnBoot → the app stops self-registering.
        Check.That(h.Registrar.Writes).ContainsExactly(false);
    }

    [Fact]
    public void RestartBringUp_PersistsThroughTheSanctionedPath()
    {
        using var h = new Harness();

        Check.That(h.BringUp.TryBringUp()).IsEqualTo(SupervisorBringUpOutcome.Reachable);

        Check.That(h.Settings.Supervision.Enabled).IsTrue();
        Check.That(h.OnDiskEnabled()).IsTrue();
    }

    [Fact]
    public void ToggleOff_PersistsFalse_AndReconcilesSelfRegistration()
    {
        using var h = new Harness();

        h.Toggle.RequestEnable();
        h.Toggle.RequestDisable();

        Check.That(h.Settings.Supervision.Enabled).IsFalse();
        Check.That(h.OnDiskEnabled()).IsFalse();
        // enable handed over (false), turn-off restored (true) — the live predicate both times.
        Check.That(h.Registrar.Writes).ContainsExactly(false, true);
    }

    [Fact]
    public void RunKeyReconciler_ReadsTheLiveFlag()
    {
        // F6 pin: the startup StartOnBoot handler reconciles with the CURRENT world, not the
        // supervision value captured when the handler was registered.
        var settings = new EyeCtrlSettings();
        settings.General.StartOnBoot = true;
        var registrar = new FakeStartupRegistrar();
        var reconcile = SupervisionComposition.CreateRunKeyReconciler(
            settings, registrar, () => true);

        settings.Supervision.Enabled = true;
        reconcile();
        settings.Supervision.Enabled = false;
        reconcile();

        Check.That(registrar.Writes).ContainsExactly(false, true);
    }
}
