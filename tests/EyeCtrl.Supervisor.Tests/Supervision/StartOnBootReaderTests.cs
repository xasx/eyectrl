namespace EyeCtrl.Supervisor.Tests.Supervision;

using System.IO;
using EyeCtrl.Supervisor.Supervision;

/// <summary>
/// The raw, writer-mutex-guarded <c>general.startOnBoot</c> read, and the tracker the setup row
/// reads it through. A null read means "cannot tell". Every test injects a GUID-suffixed
/// writer-mutex name, so no test ever touches the production mutex on this box.
/// </summary>
public sealed class StartOnBootReaderTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "eyectrl-startonboot-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string _writerMutexName =
        @"Local\EyeCtrl.Settings.Writer.Test." + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    [Fact]
    public void MissingFile_IsTheDefault_Off() =>
        Check.That(StartOnBootReader.TryRead(_path, _writerMutexName)).IsEqualTo(false);

    [Theory]
    [InlineData("""{"general":{"startOnBoot":true}}""", true)]
    [InlineData("""{"general":{"startOnBoot":false}}""", false)]
    [InlineData("""{"general":{"uiLanguage":"german"}}""", false)]   // key missing -> default
    [InlineData("""{"supervision":{"enabled":true}}""", false)]       // section missing -> default
    public void ValidFile_ReadsTheValue(string json, bool expected)
    {
        File.WriteAllText(_path, json);
        Check.That(StartOnBootReader.TryRead(_path, _writerMutexName)).IsEqualTo(expected);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"general":5}""")]
    [InlineData("""{"general":null}""")]
    [InlineData("""{"general":{"startOnBoot":"true"}}""")]
    [InlineData("""{"general":{"startOnBoot":1}}""")]
    [InlineData("""{"general":{"startOnBoot":null}}""")]
    [InlineData("""{"general":{"startOnBoot":true,"startOnBoot":false}}""")]
    public void DamagedOrUnparseableFile_CannotTell(string json)
    {
        File.WriteAllText(_path, json);
        Check.That(StartOnBootReader.TryRead(_path, _writerMutexName)).IsNull();
    }

    [Fact]
    public void ABusyWriter_CannotTell_AndTheReaderNeverOpensTheFile()
    {
        File.WriteAllText(_path, """{"general":{"startOnBoot":true}}""");
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
            Check.That(StartOnBootReader.TryRead(_path, _writerMutexName)).IsNull();
        }
        finally
        {
            releaseWriter.Set();
            writer.Join(TimeSpan.FromSeconds(5));
        }

        Check.That(StartOnBootReader.TryRead(_path, _writerMutexName)).IsEqualTo(true);
    }

    [Fact]
    public void TheTracker_ReadsTheDiskEveryTime()
    {
        File.WriteAllText(_path, """{"general":{"startOnBoot":false}}""");
        var tracker = new StartOnBootTracker(_path, loadedAtStartup: true, _writerMutexName);

        Check.That(tracker.Current).IsFalse();
        File.WriteAllText(_path, """{"general":{"startOnBoot":true}}""");
        Check.That(tracker.Current).IsTrue();
    }

    [Fact]
    public void TheTracker_KeepsTheLastDiskValue_WhenTheFileCannotBeRead()
    {
        File.WriteAllText(_path, """{"general":{"startOnBoot":true}}""");
        var tracker = new StartOnBootTracker(_path, loadedAtStartup: false, _writerMutexName);
        Check.That(tracker.Current).IsTrue();

        File.WriteAllText(_path, "{ damaged");
        Check.That(tracker.Current).IsTrue();
    }

    [Fact]
    public void TheTracker_FallsBackToTheStartupValue_BeforeAnyReadSucceeds()
    {
        File.WriteAllText(_path, "{ damaged");

        Check.That(new StartOnBootTracker(_path, loadedAtStartup: true, _writerMutexName).Current).IsTrue();
        Check.That(new StartOnBootTracker(_path, loadedAtStartup: false, _writerMutexName).Current).IsFalse();
    }
}
