namespace EyeCtrl.Supervisor.Tests.Supervision;

using System.IO;
using System.Threading;
using EyeCtrl.Supervisor.Supervision;
using NFluent;
using Xunit;

/// <summary>
/// Ticket 06-B: the raw <c>supervision.enabled</c> read. <c>SettingsService.Load()</c> answers a
/// damaged or unparseable file with whole-file defaults (enabled=false) without throwing, so the
/// supervisor must not ask Load() — that would silently disable recovery exactly when something is
/// already wrong. A null answer means "cannot tell"; the caller then acts on its last known value.
/// Every test injects a GUID-suffixed writer-mutex name, so no test ever touches the production
/// mutex on this box.
/// </summary>
public class SupervisionEnabledReaderTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "eyectrl-supenabled-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string _writerMutexName =
        @"Local\EyeCtrl.Settings.Writer.Test." + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    [Fact]
    public void MissingFile_IsDisabled() =>
        Check.That(SupervisionEnabledReader.TryRead(_path, _writerMutexName)).IsEqualTo(false);

    [Theory]
    [InlineData("""{"supervision":{"enabled":true}}""", true)]
    [InlineData("""{"supervision":{"enabled":false}}""", false)]
    [InlineData("""{"supervision":{}}""", false)]              // key missing -> default
    [InlineData("""{"other":1}""", false)]                     // section missing -> default
    [InlineData("""{"supervision":{"enabled":true},"other":1}""", true)]
    public void ValidFile_ReadsTheValue(string json, bool expected)
    {
        File.WriteAllText(_path, json);
        Check.That(SupervisionEnabledReader.TryRead(_path, _writerMutexName)).IsEqualTo(expected);
    }

    [Theory]
    [InlineData("{ not json")]                                  // unparseable
    [InlineData("[1,2]")]                                       // non-object root
    [InlineData("""{"supervision":5}""")]                        // section is not an object
    [InlineData("""{"supervision":null}""")]                     // explicit null section is damaged, not "false"
    [InlineData("""{"supervision":{"enabled":"yes"}}""")]        // enabled is not a bool
    [InlineData("""{"supervision":{"enabled":null}}""")]         // explicit null value is damaged
    [InlineData("""{"supervision":{"enabled":true,"enabled":false}}""")] // duplicate key: ambiguous
    public void DamagedOrUnparseableFile_CannotTell(string json)
    {
        File.WriteAllText(_path, json);
        Check.That(SupervisionEnabledReader.TryRead(_path, _writerMutexName)).IsNull();
    }

    // ── Ticket 06 review F3: the read must not break an App save ─────────────

    [Fact]
    public void AtomicReplace_WhileAReadHandleIsOpen_Fails()
    {
        // The hazard the writer lock protects against: the App writer replaces the file with
        // File.Move(..., overwrite: true), and that fails with access denied while ANY handle has
        // the target open — a sharing read with ReadWrite|Delete does not help (measured on this
        // box). Pinned so nobody removes the lock on the assumption that sharing fixes it.
        File.WriteAllText(_path, """{"supervision":{"enabled":true}}""");
        var tmp = _path + ".tmp";
        using (var open = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            File.WriteAllText(tmp, """{"supervision":{"enabled":false}}""");
            Check.ThatCode(() => File.Move(tmp, _path, overwrite: true))
                .Throws<UnauthorizedAccessException>();
        }
    }

    [Fact]
    public void TryRead_WithABusyWriter_ReturnsNull_AndTheReplaceStillSucceeds()
    {
        // The writer's replace runs under the writer mutex; the reader takes the same lock, so no
        // read handle is open while a replace runs. While a writer holds the lock the read yields
        // null (the caller acts on its last disk value) — and the replace succeeds. The mutex is
        // thread-affine, so the busy writer runs on its own thread.
        File.WriteAllText(_path, """{"supervision":{"enabled":true}}""");
        using var writerHolds = new ManualResetEventSlim(false);
        using var releaseWriter = new ManualResetEventSlim(false);
        var writer = new Thread(() =>
        {
            using var mutex = new Mutex(false, _writerMutexName);
            mutex.WaitOne();
            writerHolds.Set();
            releaseWriter.Wait();
            mutex.ReleaseMutex();
        }) { IsBackground = true };
        writer.Start();
        Check.That(writerHolds.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).IsTrue();

        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, """{"supervision":{"enabled":false}}""");
            File.Move(tmp, _path, overwrite: true);      // no reader handle is open: succeeds

            Check.That(SupervisionEnabledReader.TryRead(_path, _writerMutexName)).IsNull();
        }
        finally
        {
            releaseWriter.Set();
            writer.Join();
        }

        Check.That(SupervisionEnabledReader.TryRead(_path, _writerMutexName)).IsEqualTo(false);
    }

    [Fact]
    public void TryRead_AfterAWriterAbandonedTheMutex_ReadsTheDisk_AndReleasesTheMutex()
    {
        // Ticket 06 re-check N1: WaitOne throwing AbandonedMutexException means this thread now
        // OWNS the mutex (WAIT_ABANDONED grants ownership). Treating it as not-owned would return
        // null AND skip the release, leaving the Supervisor's UI thread holding the writer mutex
        // forever — the App's Save waits on it with no timeout. An abandoned writer mutex is
        // exactly what the Supervisor's own ForceKill produces when the app hung inside Save.
        File.WriteAllText(_path, """{"supervision":{"enabled":true}}""");

        Mutex? abandoned = null;
        var abandoner = new Thread(() =>
        {
            abandoned = new Mutex(false, _writerMutexName);
            abandoned.WaitOne();
            // Deliberately no ReleaseMutex: the thread exits owning the mutex, abandoning it.
        });
        abandoner.Start();
        abandoner.Join();

        // Must be treated as acquired: read the disk value rather than falling back to null.
        Check.That(SupervisionEnabledReader.TryRead(_path, _writerMutexName)).IsEqualTo(true);

        // Keep the abandoned kernel object alive through the read: if it were finalized in the gap,
        // no handle would remain, the object would be destroyed, and TryRead would create a fresh
        // (non-abandoned) mutex — the run would pass without exercising the abandoned branch.
        GC.KeepAlive(abandoned);

        // ... and it must have been released again. The probe runs on a DIFFERENT thread: mutex
        // ownership is thread-affine, so a same-thread WaitOne would succeed even unreleased.
        var acquired = false;
        var prober = new Thread(() =>
        {
            using var mutex = new Mutex(false, _writerMutexName);
            acquired = mutex.WaitOne(TimeSpan.Zero);
            if (acquired) mutex.ReleaseMutex();
        });
        prober.Start();
        prober.Join();

        Check.That(acquired).IsTrue();
    }
}
