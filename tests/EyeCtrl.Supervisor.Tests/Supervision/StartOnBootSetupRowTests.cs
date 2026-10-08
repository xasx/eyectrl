namespace EyeCtrl.Supervisor.Tests.Supervision;

using System.IO;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Supervisor.Ipc;
using EyeCtrl.Supervisor.Supervision;

/// <summary>
/// What the setup window's logon row shows: the value on disk, or the parked choice labelled as
/// parked — never the Supervisor's startup snapshot. Status texts are crisis-surface strings, so
/// they are asserted as English literals, never through <c>T(key)</c>.
/// </summary>
public sealed class StartOnBootSetupRowTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "eyectrl-logon-row-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string _writerMutexName =
        @"Local\EyeCtrl.Settings.Writer.Test." + Guid.NewGuid().ToString("N");
    private readonly List<TaskCompletionSource<AppRequestResult>> _replies = new();
    private readonly SettingDelivery<bool> _delivery;
    private readonly StringTable _strings = StringTable.EmbeddedEnglish;

    public StartOnBootSetupRowTests()
    {
        _delivery = new SettingDelivery<bool>(
            "StartOnBoot",
            (value, _) => SettingRequest.SetStartOnBoot(value),
            _ =>
            {
                var reply = new TaskCompletionSource<AppRequestResult>();
                _replies.Add(reply);
                return reply.Task;
            },
            work => work());
    }

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    private StartOnBootTracker Tracker(bool loadedAtStartup) =>
        new(_path, loadedAtStartup, _writerMutexName);

    private StartOnBootSetupRow Open(StartOnBootTracker tracker) => new(_delivery, () => tracker.Current);

    private static AppRequestResult Saved => new(SettingRequestStatus.Saved, "test");

    [Fact]
    public void TheRow_ShowsTheValueOnDisk_NotTheStartupSnapshot()
    {
        // The Supervisor loaded false at startup; the App has saved true since.
        File.WriteAllText(_path, """{"general":{"startOnBoot":true}}""");

        using var row = Open(Tracker(loadedAtStartup: false));

        Check.That(row.Value).IsTrue();
        Check.That(row.IsParked).IsFalse();
        Check.That(row.Note(_strings)).IsNull();
    }

    [Fact]
    public void AChoiceTheAppCannotTake_IsShownParked_WithTheParkedNote()
    {
        File.WriteAllText(_path, """{"general":{"startOnBoot":false}}""");
        using var row = Open(Tracker(loadedAtStartup: false));

        row.Choose(true);
        Check.That(row.StatusText(_strings)).IsEqualTo("Asking EyeCtrl to save the logon setting…");
        _replies[0].SetResult(AppRequestResult.NotAvailable("test: no App"));

        Check.That(row.Value).IsTrue();
        Check.That(row.IsParked).IsTrue();
        Check.That(row.Note(_strings)).IsEqualTo("Applies when EyeCtrl next runs");
        Check.That(row.StatusText(_strings)).IsEqualTo("Applies when EyeCtrl next runs");
    }

    [Fact]
    public void AReopenedWindow_StillShowsTheParkedChoice_NotTheDisk()
    {
        File.WriteAllText(_path, """{"general":{"startOnBoot":false}}""");
        var tracker = Tracker(loadedAtStartup: false);
        using (var first = Open(tracker))
        {
            first.Choose(true);
            _replies[0].SetResult(AppRequestResult.NotAvailable("test: no App"));
        }

        using var reopened = Open(tracker);

        Check.That(reopened.Value).IsTrue();
        Check.That(reopened.Note(_strings)).IsEqualTo("Applies when EyeCtrl next runs");
    }

    [Fact]
    public void AnAcknowledgedChoice_SaysSaved_AndKeepsShowingTheSavedValue()
    {
        File.WriteAllText(_path, """{"general":{"startOnBoot":false}}""");
        using var row = Open(Tracker(loadedAtStartup: false));
        var changes = 0;
        row.Changed += () => changes++;

        row.Choose(true);
        _replies[0].SetResult(Saved);

        Check.That(row.Value).IsTrue();   // not the false read when the window opened
        Check.That(row.IsParked).IsFalse();
        Check.That(row.Note(_strings)).IsNull();
        Check.That(row.StatusText(_strings)).IsEqualTo("Saved: EyeCtrl will start at logon.");
        Check.That(changes).IsEqualTo(2);   // sending, saved
    }

    [Fact]
    public void TurningItOff_SaysItWillNotStart()
    {
        File.WriteAllText(_path, """{"general":{"startOnBoot":true}}""");
        using var row = Open(Tracker(loadedAtStartup: true));

        row.Choose(false);
        _replies[0].SetResult(Saved);

        Check.That(row.Value).IsFalse();
        Check.That(row.StatusText(_strings)).IsEqualTo("Saved: EyeCtrl will not start at logon.");
    }

    [Fact]
    public void ARefusedChoice_SaysSo_AndTheRowFallsBackToTheDisk()
    {
        File.WriteAllText(_path, """{"general":{"startOnBoot":false}}""");
        using var row = Open(Tracker(loadedAtStartup: false));

        row.Choose(true);
        _replies[0].SetResult(new AppRequestResult(SettingRequestStatus.Rejected, "test"));

        Check.That(row.Value).IsFalse();
        Check.That(row.StatusText(_strings)).IsEqualTo("EyeCtrl refused the logon setting. It was not saved.");
    }

    [Fact]
    public void AClosedWindow_StopsListening()
    {
        var row = Open(Tracker(loadedAtStartup: false));
        var changes = 0;
        row.Changed += () => changes++;
        row.Dispose();

        row.Choose(true);

        Check.That(changes).IsEqualTo(0);
    }

    [Fact]
    public void TheRow_NeverWritesTheSnapshot()
    {
        // The window no longer holds a setter into EyeCtrlSettings for this key; the row has no
        // access to it at all. Pin the contract the window relies on: choosing leaves a snapshot
        // the Supervisor loaded untouched, and nothing is written to disk by the Supervisor.
        File.WriteAllText(_path, """{"general":{"startOnBoot":false}}""");
        var snapshot = new EyeCtrlSettings();
        using var row = Open(Tracker(loadedAtStartup: snapshot.General.StartOnBoot));

        row.Choose(true);
        _replies[0].SetResult(AppRequestResult.NotAvailable("test: no App"));

        Check.That(snapshot.General.StartOnBoot).IsFalse();
        Check.That(File.ReadAllText(_path)).IsEqualTo("""{"general":{"startOnBoot":false}}""");
    }
}
