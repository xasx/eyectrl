namespace EyeCtrl.Supervisor.Tests.Supervision;

using System;
using System.IO;
using EyeCtrl.Core.Enums;
using EyeCtrl.Supervisor.Supervision;
using NFluent;
using Xunit;

/// <summary>
/// The last-known fallback: contention or a damaged file must never silently change the Supervisor
/// to System — on this de-DE machine that would show German to a user who chose English, exactly
/// around crashes and restarts. System is used only while nothing has been read successfully.
/// </summary>
public class SupervisorLanguageTrackerTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "eyectrl-uilang-tracker-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string _writerMutexName =
        @"Local\EyeCtrl.Settings.Writer.Test." + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    private SupervisorLanguageTracker Tracker() => new(_path, _writerMutexName);

    [Fact]
    public void BeforeAnythingIsRead_SystemIsTheOnlyChoice()
    {
        // No file: the tracker reads the positive default System.
        var tracker = Tracker();
        Check.That(tracker.Current).IsEqualTo(UiLanguage.System);
    }

    [Fact]
    public void AfterAnUnreadableFileFromTheStart_SystemIsUsed()
    {
        File.WriteAllText(_path, "{ not json");
        Check.That(Tracker().Current).IsEqualTo(UiLanguage.System);
    }

    [Fact]
    public void DamagedFile_KeepsTheLastSuccessfullyReadValue()
    {
        // English, then German, then damaged: "last" must mean the most recent successful read.
        // Keeping the FIRST read would make the Supervisor revert to English after the user
        // switched to German and the App's OnExit save contended with a read.
        File.WriteAllText(_path, """{"general":{"uiLanguage":"english"}}""");
        var tracker = Tracker();
        Check.That(tracker.Current).IsEqualTo(UiLanguage.English);

        File.WriteAllText(_path, """{"general":{"uiLanguage":"german"}}""");
        Check.That(tracker.Current).IsEqualTo(UiLanguage.German);

        File.WriteAllText(_path, "{ not json");
        Check.That(tracker.Current).IsEqualTo(UiLanguage.German);
    }

    [Fact]
    public void ContendedFile_KeepsTheLastSuccessfullyReadValue()
    {
        // The tracker itself never opens the file while the writer mutex is taken, so the last
        // known value survives a save in progress — including the App's synchronous OnExit save.
        File.WriteAllText(_path, """{"general":{"uiLanguage":"english"}}""");
        var tracker = Tracker();
        Check.That(tracker.Current).IsEqualTo(UiLanguage.English);

        using var held = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = new System.Threading.Thread(() =>
        {
            using var mutex = new System.Threading.Mutex(false, _writerMutexName);
            mutex.WaitOne();
            held.Set();
            release.Wait();
            mutex.ReleaseMutex();
        }) { IsBackground = true };
        holder.Start();
        Check.That(held.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).IsTrue();

        try
        {
            Check.That(tracker.Current).IsEqualTo(UiLanguage.English);
        }
        finally
        {
            release.Set();
            holder.Join();
        }
    }

    [Fact]
    public void ANewValueOnDisk_IsPickedUp()
    {
        File.WriteAllText(_path, """{"general":{"uiLanguage":"english"}}""");
        var tracker = Tracker();
        Check.That(tracker.Current).IsEqualTo(UiLanguage.English);

        File.WriteAllText(_path, """{"general":{"uiLanguage":"german"}}""");
        Check.That(tracker.Current).IsEqualTo(UiLanguage.German);
    }
}
