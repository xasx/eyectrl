namespace EyeCtrl.Supervisor.Tests.Supervision;

using System.IO;
using System.Threading;
using EyeCtrl.Core.Enums;
using EyeCtrl.Supervisor.Supervision;
using NFluent;
using Xunit;

/// <summary>
/// The raw <c>general.uiLanguage</c> read. A null answer means "cannot tell"; the caller then acts
/// on its last known value. Every test injects a GUID-suffixed writer-mutex name, so no test ever
/// touches the production mutex on this box.
/// </summary>
public class UiLanguageReaderTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "eyectrl-uilang-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string _writerMutexName =
        @"Local\EyeCtrl.Settings.Writer.Test." + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    [Fact]
    public void MissingFile_IsSystem() =>
        Check.That(UiLanguageReader.TryRead(_path, _writerMutexName)).IsEqualTo(UiLanguage.System);

    [Theory]
    [InlineData("""{"general":{"uiLanguage":"system"}}""", UiLanguage.System)]
    [InlineData("""{"general":{"uiLanguage":"german"}}""", UiLanguage.German)]
    [InlineData("""{"general":{"uiLanguage":"english"}}""", UiLanguage.English)]
    [InlineData("""{"general":{"uiLanguage":"GERMAN"}}""", UiLanguage.German)]       // case-insensitive
    [InlineData("""{"general":{"uiLanguage":1}}""", UiLanguage.German)]              // enum number
    [InlineData("""{"general":{}}""", UiLanguage.System)]                            // key missing -> default
    [InlineData("""{"other":1}""", UiLanguage.System)]                               // section missing -> default
    public void ValidFile_ReadsTheValue(string json, UiLanguage expected)
    {
        File.WriteAllText(_path, json);
        Check.That(UiLanguageReader.TryRead(_path, _writerMutexName)).IsEqualTo(expected);
    }

    [Theory]
    [InlineData("{ not json")]                                          // unparseable
    [InlineData("[1,2]")]                                               // non-object root
    [InlineData("""{"general":5}""")]                                   // section is not an object
    [InlineData("""{"general":null}""")]                                // explicit null section is damaged
    [InlineData("""{"general":{"uiLanguage":"deutsch"}}""")]            // unknown token
    [InlineData("""{"general":{"uiLanguage":99}}""")]                   // undefined enum number
    [InlineData("""{"general":{"uiLanguage":null}}""")]                 // explicit null value is damaged
    [InlineData("""{"general":{"uiLanguage":true}}""")]                 // not a string or number
    [InlineData("""{"general":{"uiLanguage":"german","uiLanguage":"english"}}""")] // duplicate key: ambiguous
    public void DamagedOrUnparseableFile_CannotTell(string json)
    {
        File.WriteAllText(_path, json);
        Check.That(UiLanguageReader.TryRead(_path, _writerMutexName)).IsNull();
    }

    [Fact]
    public void TryRead_WithABusyWriter_ReturnsNull_AndTheReplaceStillSucceeds()
    {
        // The writer holds the writer mutex; the reader takes the same lock, so no read handle is
        // open while a replace runs. The mutex is thread-affine, so the busy writer runs on its
        // own thread.
        File.WriteAllText(_path, """{"general":{"uiLanguage":"german"}}""");
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
            File.WriteAllText(tmp, """{"general":{"uiLanguage":"english"}}""");
            File.Move(tmp, _path, overwrite: true);      // no reader handle is open: succeeds

            Check.That(UiLanguageReader.TryRead(_path, _writerMutexName)).IsNull();
        }
        finally
        {
            releaseWriter.Set();
            writer.Join();
        }

        Check.That(UiLanguageReader.TryRead(_path, _writerMutexName)).IsEqualTo(UiLanguage.English);
    }

    [Fact]
    public void TryRead_AfterAWriterAbandonedTheMutex_ReadsTheDisk_AndReleasesTheMutex()
    {
        // WaitOne throwing AbandonedMutexException means this thread now OWNS the mutex.
        // Treating it as not-owned would return null AND skip the release, leaving the
        // Supervisor's UI thread holding the writer mutex forever.
        File.WriteAllText(_path, """{"general":{"uiLanguage":"german"}}""");

        Mutex? abandoned = null;
        var abandoner = new Thread(() =>
        {
            abandoned = new Mutex(false, _writerMutexName);
            abandoned.WaitOne();
            // Deliberately no ReleaseMutex: the thread exits owning the mutex, abandoning it.
        });
        abandoner.Start();
        abandoner.Join();

        Check.That(UiLanguageReader.TryRead(_path, _writerMutexName)).IsEqualTo(UiLanguage.German);

        // Keep the abandoned kernel object alive through the read: if it were finalized in the
        // gap, no handle would remain and TryRead would create a fresh (non-abandoned) mutex.
        GC.KeepAlive(abandoned);

        // ... and it must have been released again. The probe runs on a DIFFERENT thread: mutex
        // ownership is thread-affine.
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
