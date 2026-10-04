namespace EyeCtrl.Supervisor.Tests.Supervision;

using System.IO;
using EyeCtrl.Supervisor.Supervision;
using NFluent;
using Xunit;

/// <summary>
/// Ticket 06-B (reviewer input): the enabled predicate must read the disk every time it is
/// evaluated, and a damaged file must fall back to the last value successfully READ FROM DISK —
/// never to the in-memory settings object, which the Supervisor's setup window mutates on open
/// and on every unsaved toggle click. The tracker deliberately has no in-memory input, so an
/// unsaved click is structurally incapable of influencing recovery; these tests pin the disk
/// behaviour and the last-disk fallback.
/// </summary>
public class SupervisionEnabledTrackerTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "eyectrl-supenabled-tracker-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string _writerMutexName =
        @"Local\EyeCtrl.Settings.Writer.Test." + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    [Fact]
    public void Current_ReadsTheDiskOnEveryEvaluation()
    {
        File.WriteAllText(_path, """{"supervision":{"enabled":true}}""");
        var tracker = new SupervisionEnabledTracker(_path, seedFromDisk: false, writerMutexName: _writerMutexName);
        Check.That(tracker.Current).IsTrue();

        File.WriteAllText(_path, """{"supervision":{"enabled":false}}""");
        Check.That(tracker.Current).IsFalse();

        File.WriteAllText(_path, """{"supervision":{"enabled":true}}""");
        Check.That(tracker.Current).IsTrue();
    }

    [Fact]
    public void Current_DamagedFile_KeepsTheLastDiskValue_NotAMemoryValue()
    {
        File.WriteAllText(_path, """{"supervision":{"enabled":true}}""");
        var tracker = new SupervisionEnabledTracker(_path, seedFromDisk: false, writerMutexName: _writerMutexName);
        Check.That(tracker.Current).IsTrue();          // last disk value = true

        File.WriteAllText(_path, "{ damaged");          // e.g. after an unsaved toggle click in memory

        Check.That(tracker.Current).IsTrue();           // the last DISK value wins
    }

    [Fact]
    public void Current_DamagedFileWithADifferentLastDiskValue_KeepsThatValue()
    {
        File.WriteAllText(_path, """{"supervision":{"enabled":false}}""");
        var tracker = new SupervisionEnabledTracker(_path, seedFromDisk: true, writerMutexName: _writerMutexName);  // stale seed
        Check.That(tracker.Current).IsFalse();          // the good disk read updates the last value

        File.WriteAllText(_path, "not json at all");
        Check.That(tracker.Current).IsFalse();
    }

    [Fact]
    public void Current_DamagedFileAtFirstRead_UsesTheSeedFromTheStartupLoad()
    {
        File.WriteAllText(_path, "{ damaged");
        var tracker = new SupervisionEnabledTracker(_path, seedFromDisk: true, writerMutexName: _writerMutexName);
        Check.That(tracker.Current).IsTrue();
    }

    [Fact]
    public void Current_MissingFile_IsDisabled()
    {
        var tracker = new SupervisionEnabledTracker(_path, seedFromDisk: true, writerMutexName: _writerMutexName);
        Check.That(tracker.Current).IsFalse();          // no file: defaults say disabled, seed overridden
    }
}
