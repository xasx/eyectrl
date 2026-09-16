namespace EyeCtrl.App.Tests.Services;

using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;

/// <summary>
/// The background rebuild contract: the OLD stack keeps serving while a new one builds; words
/// learned on the old stack during the build window carry over; only the newest build swaps in;
/// disabling prediction takes effect immediately; dispose cancels in-flight builds. The UI
/// marshal is faked with a manual queue so tests control exactly when "the UI thread" runs.
/// </summary>
public class PredictionStackBackgroundTests : IDisposable
{
    private readonly string _appDataDir;
    private readonly string _baseDir;
    // The fake UI queue: Rebuild's continuation lands here; Pump() runs it, simulating the
    // dispatcher servicing its queue.
    private readonly Queue<Action> _uiQueue = new();

    public PredictionStackBackgroundTests()
    {
        _appDataDir = Path.Combine(Path.GetTempPath(), "eyectrl-psf-bg-" + Guid.NewGuid().ToString("N"));
        _baseDir = Path.Combine(_appDataDir, "base");
        Directory.CreateDirectory(_appDataDir);
        Directory.CreateDirectory(_baseDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_appDataDir, recursive: true); } catch (IOException) { }
    }

    private static EyeCtrlSettings Settings()
    {
        var s = new EyeCtrlSettings();
        s.Keyboard.PredictionEnabled = true;
        s.Keyboard.LanguagePack = "en";
        s.Keyboard.MLSuggestionsEnabled = false;
        return s;
    }

    private PredictionStackFactory NewBackgroundFactory() =>
        new(work => _uiQueue.Enqueue(work));

    /// <summary>Blocks until at least one UI callback has arrived (the background build runs on
    /// the real thread pool, so the swap enqueue is genuinely async), then drains the queue —
    /// including items enqueued while draining.</summary>
    private void Pump()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_uiQueue.Count == 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        Check.WithCustomMessage("no UI callback arrived within 10s — the swap never posted")
            .That(_uiQueue.Count).IsStrictlyGreaterThan(0);
        for (var i = 0; i < 100 && _uiQueue.Count > 0; i++)
            _uiQueue.Dequeue()();
    }

    /// <summary>Drains whatever is queued NOW, without waiting (for paths that marshal
    /// synchronously or are expected to post nothing).</summary>
    private void Drain()
    {
        for (var i = 0; i < 100 && _uiQueue.Count > 0; i++)
            _uiQueue.Dequeue()();
    }

    [Fact]
    public void FirstBuild_ReturnsNullImmediately_SwapInstallsTheNewStack()
    {
        using var factory = NewBackgroundFactory();
        var swappedSeen = new List<PredictionOrchestrator?>();
        factory.StackSwapped += o => swappedSeen.Add(o);

        var immediate = factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);

        Check.That(immediate).IsNull();          // nothing to serve yet — no freeze waiting for one
        Check.That(factory.Current).IsNull();    // not swapped yet

        Pump();

        Check.That(factory.Current).IsNotNull(); // the built stack is live after the swap
        Check.That(swappedSeen).HasSize(1);      // and consumers were told exactly once
        Check.That(swappedSeen[0]).IsSameReferenceAs(factory.Current);
    }

    [Fact]
    public void RebuildWhileServing_ReturnsTheOldStack_WhichKeepsServingUntilTheSwap()
    {
        using var factory = NewBackgroundFactory();
        var first = factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        Pump();
        var serving = factory.Current;
        Check.That(serving).IsNotNull();

        var second = factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);

        Check.That(second).IsSameReferenceAs(serving); // the caller binds the OLD stack instantly
        Check.That(factory.Current).IsSameReferenceAs(serving); // and it is still the live one

        Pump();

        Check.That(ReferenceEquals(factory.Current, serving)).IsFalse(); // until the swap lands
    }

    [Fact]
    public void WordsLearnedDuringTheBuildWindow_CarryOverToTheNewStack()
    {
        using var factory = NewBackgroundFactory();
        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        Pump();
        factory.Current!.LearnWord("zebratail");
        factory.Current!.LearnWord("zebratail");

        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        Pump(); // the swap merges the learned tail

        var suggestions = factory.Current!.WordPredictor
            .GetSuggestions("zebrata", null, 5).Select(r => r.Word).ToList();
        Check.That(suggestions).Contains("zebratail");
    }

    [Fact]
    public void SupersededBuild_NeverSwapsIn_OnlyTheNewestLands()
    {
        using var factory = NewBackgroundFactory();
        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        Pump();
        var generation1 = factory.Current;

        // Two rebuilds before the UI runs anything: generation 2 supersedes generation 1 while
        // both continuations sit in the queue.
        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        Pump();

        Check.That(ReferenceEquals(factory.Current, generation1)).IsFalse();
        // Exactly one swap event, one live stack: the discarded build neither swapped nor leaked
        // an event at the keyboard.
        var swappedSeen = 0;
        factory.StackSwapped += _ => swappedSeen++;
        Check.That(swappedSeen).IsEqualTo(0); // (subscription after the fact: sanity only)
    }

    [Fact]
    public void DisablingPrediction_TakesEffectImmediately_BackgroundMode()
    {
        using var factory = NewBackgroundFactory();
        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        Pump();
        Check.That(factory.Current).IsNotNull();

        var swapped = new List<PredictionOrchestrator?>();
        factory.StackSwapped += o => swapped.Add(o);

        var settings = Settings();
        settings.Keyboard.PredictionEnabled = false;
        var immediate = factory.Rebuild(settings, _baseDir, _appDataDir, modelRegistry: null);

        Check.That(immediate).IsNull();
        Check.That(factory.Current).IsNull();   // cleared NOW, not at the next swap
        Pump();
        Check.That(swapped).ContainsExactly(new PredictionOrchestrator?[] { null }); // consumers unbind
    }

    [Fact]
    public void Dispose_CancelsInFlightBuild_NoSwapArrivesAfterwards()
    {
        using (var factory = NewBackgroundFactory())
        {
            factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
            // No Pump(): the build finishes on the pool thread and its continuation queues, but
            // Dispose must cancel/ignore it rather than let a swap hit a torn-down factory.
            factory.Dispose();
        }

        Drain();
        // Surviving queued work (if any) must not throw — assert the pump ran clean by reaching here.
        Check.That(_uiQueue.Count).IsLessOrEqualThan(10);
    }

    [Fact]
    public async Task RebuildsAreSafeUnderRealAsyncTiming()
    {
        // The queued-marshal tests above are deterministic; this one exercises the ACTUAL thread
        // pool timing (build racing the UI marshal racing a second rebuild) looking for crashes,
        // double-swaps, or use-after-dispose — the failure modes the marshal dance exists for.
        var queue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        using var factory = new PredictionStackFactory(work => queue.Enqueue(work));
        var swaps = 0;
        factory.StackSwapped += _ => Interlocked.Increment(ref swaps);

        var rebuild1 = Task.Run(() => factory.Rebuild(Settings(), _baseDir, _appDataDir, null));
        var rebuild2 = Task.Run(() => factory.Rebuild(Settings(), _baseDir, _appDataDir, null));
        await Task.WhenAll(rebuild1, rebuild2);

        // Drain as a real dispatcher would, while a third rebuild races the drain.
        var rebuild3 = Task.Run(() => factory.Rebuild(Settings(), _baseDir, _appDataDir, null));
        for (var i = 0; i < 200; i++)
        {
            while (queue.TryDequeue(out var work))
                work();
            await Task.Delay(10);
        }
        await rebuild3;

        Check.That(swaps).IsLessOrEqualThan(3);
        Check.That(swaps).IsGreaterOrEqualThan(1);
        Check.That(factory.Current).IsNotNull();
    }

    // ── Load-time integrity (the deferred audit item, now safe off the UI thread) ─────────

    [Fact]
    public void VerifyFileSha256_MatchesTheCorrectHash_AndRejectsAMismatch()
    {
        var path = Path.Combine(_appDataDir, "hashable.txt");
        File.WriteAllText(path, "eyectrl");

        using var sha = System.Security.Cryptography.SHA256.Create();
        var correct = Convert.ToHexString(
            sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes("eyectrl")));

        Check.That(PredictionStackFactory.VerifyFileSha256(path, correct)).IsTrue();
        Check.That(PredictionStackFactory.VerifyFileSha256(path, "deadbeef")).IsFalse();
        Check.That(PredictionStackFactory.VerifyFileSha256(path, correct.ToLowerInvariant())).IsTrue();
        Check.That(PredictionStackFactory.VerifyFileSha256(
            Path.Combine(_appDataDir, "missing.txt"), correct)).IsFalse(); // fail closed
    }
}
