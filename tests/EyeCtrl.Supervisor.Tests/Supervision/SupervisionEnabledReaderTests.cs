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
/// </summary>
public class SupervisionEnabledReaderTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "eyectrl-supenabled-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    [Fact]
    public void MissingFile_IsDisabled() =>
        Check.That(SupervisionEnabledReader.TryRead(_path)).IsEqualTo(false);

    [Theory]
    [InlineData("""{"supervision":{"enabled":true}}""", true)]
    [InlineData("""{"supervision":{"enabled":false}}""", false)]
    [InlineData("""{"supervision":{}}""", false)]              // key missing -> default
    [InlineData("""{"other":1}""", false)]                     // section missing -> default
    [InlineData("""{"supervision":{"enabled":true},"other":1}""", true)]
    public void ValidFile_ReadsTheValue(string json, bool expected)
    {
        File.WriteAllText(_path, json);
        Check.That(SupervisionEnabledReader.TryRead(_path)).IsEqualTo(expected);
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
        Check.That(SupervisionEnabledReader.TryRead(_path)).IsNull();
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
        // The writer's replace runs under Local\EyeCtrl.Settings.Writer; the reader takes the same
        // lock, so no read handle is open while a replace runs. While a writer holds the lock the
        // read yields null (the caller acts on its last disk value) — and the replace succeeds.
        // The mutex is thread-affine, so the busy writer runs on its own thread; the name is
        // injectable so tests never touch the production name (box-wide rule).
        File.WriteAllText(_path, """{"supervision":{"enabled":true}}""");
        var mutexName = @"Local\EyeCtrl.Settings.Writer.Test." + Guid.NewGuid().ToString("N");
        using var writerHolds = new ManualResetEventSlim(false);
        using var releaseWriter = new ManualResetEventSlim(false);
        var writer = new Thread(() =>
        {
            using var mutex = new Mutex(false, mutexName);
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

            Check.That(SupervisionEnabledReader.TryRead(_path, mutexName)).IsNull();
        }
        finally
        {
            releaseWriter.Set();
            writer.Join();
        }

        Check.That(SupervisionEnabledReader.TryRead(_path, mutexName)).IsEqualTo(false);
    }
}
