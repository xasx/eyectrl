namespace EyeCtrl.Core.Tests.Services;

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;

/// <summary>
/// Phase 2 of the instance-scoping startup check reads names back off constructed objects rather
/// than re-deriving them. These tests cover the getters that makes possible, and — for the settings
/// writer mutex — that the injected name is actually bound rather than merely reported.
/// </summary>
public class ReceiverNameTests
{
    [Fact]
    public void SingleInstanceGuard_ReportsItsDefaultName()
    {
        using var guard = new SingleInstanceGuard();

        Check.That(guard.EffectiveName).IsEqualTo(SingleInstanceGuard.DefaultMutexName);
    }

    [Fact]
    public void SingleInstanceGuard_ReportsAnInjectedName()
    {
        var name = @"Local\EyeCtrl.App.Singleton_" + Guid.NewGuid().ToString("N");
        using var guard = new SingleInstanceGuard(name);

        Check.That(guard.EffectiveName).IsEqualTo(name);
    }

    [Fact]
    public void SettingsService_ReportsItsDefaultWriterMutexName()
    {
        var svc = new SettingsService(
            Path.Combine(Path.GetTempPath(), "unused.json"), SettingsWriter.App);

        Check.That(svc.EffectiveWriterMutexName).IsEqualTo(SettingsService.DefaultWriterMutexName);
        Check.That(SettingsService.DefaultWriterMutexName).IsEqualTo(@"Local\EyeCtrl.Settings.Writer");
    }

    [Fact]
    public void SettingsService_ReportsAnInjectedWriterMutexName()
    {
        var name = @"Local\EyeCtrl.Settings.Writer_" + Guid.NewGuid().ToString("N");
        var svc = new SettingsService(
            Path.Combine(Path.GetTempPath(), "unused.json"), SettingsWriter.App,
            logger: null, writerMutexName: name);

        Check.That(svc.EffectiveWriterMutexName).IsEqualTo(name);
    }

    [Fact]
    public async Task SettingsService_UsesTheInjectedMutexWhenSaving()
    {
        // Proves the name is bound, not merely reported: hold the injected mutex and confirm Save
        // blocks on it. A name that is reported but unused would satisfy phase 2 while still
        // contending on the production mutex against the default instance.
        var dir = Path.Combine(Path.GetTempPath(), "eyectrl-writer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var name = @"Local\EyeCtrl.Settings.Writer_" + Guid.NewGuid().ToString("N");
        var svc = new SettingsService(
            Path.Combine(dir, "settings.json"), SettingsWriter.App,
            logger: null, writerMutexName: name);

        try
        {
            using var held = new Mutex(initiallyOwned: true, name: name);
            var saveFinished = Task.Run(() => svc.Save(new EyeCtrlSettings()));

            Check.That(saveFinished.Wait(TimeSpan.FromMilliseconds(400))).IsFalse();  // blocked

            held.ReleaseMutex();

            Check.That(saveFinished.Wait(TimeSpan.FromSeconds(5))).IsTrue();          // released
            await saveFinished;                                                        // observe faults
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
