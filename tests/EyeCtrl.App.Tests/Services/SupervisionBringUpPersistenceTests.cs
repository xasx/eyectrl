namespace EyeCtrl.App.Tests.Services;

using System;
using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The restart flow's bring-up persists <c>supervision.enabled</c> so the replacement launches
/// supervised. Ownership makes that subtle: the App writer's merge always overlays the on-disk
/// Supervision section, so a plain <see cref="SettingsService.Save"/> through the App writer
/// silently discards the in-memory flip whenever a valid section exists. The bring-up goes
/// through the sanctioned write path instead. Reproduces the ticket-04 lead.
/// </summary>
public class SupervisionBringUpPersistenceTests
{
    [Fact]
    public void BringUp_PersistsEnabledOverOnDiskFalse_SoTheReplacementStartsSupervised()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_t04_{Guid.NewGuid()}.json");
        try
        {
            // The user's case: a VALID on-disk Supervision section with enabled=false.
            // AppWriter_DoesNotPersist_SupervisionSection pins that the App writer cannot
            // overwrite such a section.
            var seed = new EyeCtrlSettings();
            seed.Dwell.ActivationButtonDwellMs = 1234;
            new SettingsService(path, SettingsWriter.App).Save(seed);
            var seeded = new SettingsService(path, SettingsWriter.Supervisor).Load();
            seeded.Supervision.Enabled = false;
            new SettingsService(path, SettingsWriter.Supervisor).Save(seeded);

            var app = new SettingsService(path, SettingsWriter.App);
            var settings = app.Load();
            var messenger = new WeakReferenceMessenger();

            // The production-shaped composition for this step: the sanctioned writer as the
            // persist step of the shared bring-up.
            var writer = new SupervisionEnabledWriter(path);
            var bringUp = new SupervisorBringUp(
                settings,
                enablePublishing: () => { },
                launchSupervisor: () => true,
                persistEnabled: writer.Persist,
                reconcileRunKey: () => { },
                tryReachSupervisor: () => true);

            var coordinator = new AppRestartCoordinator(
                messenger, settings,
                tryReachSupervisor: () => true,
                selfRestart: () => RestartOutcome.Started,
                shutdown: () => { },
                reportInProgress: () => { },
                reportUnavailable: _ => { },
                runInBackground: work => work(),
                postToUi: work => work(),
                supervisorInstalled: () => true,
                bringUp: bringUp);

            messenger.Send(new RestartAppRequestedMessage(StartSupervisor: true));

            // The whole point of the restart offer is that the replacement launches
            // supervised, and the replacement reads Enabled from this file.
            var onDisk = new SettingsService(path, SettingsWriter.App).Load();
            Check.That(onDisk.Supervision.Enabled).IsTrue();
            Check.That(onDisk.Dwell.ActivationButtonDwellMs).IsEqualTo(1234);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }
}
