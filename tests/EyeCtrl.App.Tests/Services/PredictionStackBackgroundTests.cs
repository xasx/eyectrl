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
    public void LearningDuringTheBuildWindow_CarriesOver_WordsAndBigrams()
    {
        using var factory = NewBackgroundFactory();
        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        Pump();

        // The learning below happens AFTER Rebuild returns and BEFORE the swap — the real
        // build window. (Learning before Rebuild would only prove the save-before-build
        // ordering: the word would reach the new stack via its file loads and the delta
        // merge would never run — that is how the round-1 version of this test failed to
        // cover its own subject.)
        var rebuild = factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        rebuild!.LearnSequence(new[] { "zebratail", "surfsalot" });
        rebuild!.LearnWord("zebratail");

        Pump(); // the swap merges the window's learned tail into the new stack

        var suggestions = factory.Current!.WordPredictor
            .GetSuggestions("zebrata", null, 5).Select(r => r.Word).ToList();
        Check.That(suggestions).Contains("zebratail");

        // Bigrams: the merged tail must survive the new stack's own save — the destruction
        // path the review demonstrated (swap-time save overwritten by the new stack's next
        // save used to erase the window's bigrams).
        factory.Dispose(); // persists the new stack's state (words AND bigrams)
        var bigramsPath = Path.Combine(_appDataDir, "user_dictionary_en_bigrams.json");
        Check.That(File.ReadAllText(bigramsPath)).Contains("surfsalot");
    }

    [Fact]
    public void SupersededBuild_NeverSwapsIn_OnlyTheNewestLands()
    {
        using var factory = NewBackgroundFactory();
        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        Pump();
        var serving = factory.Current;

        // Subscribe BEFORE the rebuilds: a swap count is only meaningful when the subscription
        // is in place for every event (the previous version subscribed after the pump and
        // asserted 0 == 0 — it could not fail).
        var swappedSeen = 0;
        factory.StackSwapped += _ => swappedSeen++;

        // Two rebuilds before the UI runs anything: generation 2 supersedes generation 1 while
        // both continuations sit in the queue. Exactly ONE swap may land.
        var firstBinding = factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        var secondBinding = factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        Check.That(ReferenceEquals(firstBinding, serving)).IsTrue(); // both callers got the old stack
        Check.That(ReferenceEquals(secondBinding, serving)).IsTrue();
        Pump();

        Check.That(swappedSeen).IsEqualTo(1);                            // one swap, not two
        Check.That(factory.Current).IsNotNull();
        Check.That(ReferenceEquals(factory.Current, serving)).IsFalse(); // a NEW stack landed, once
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
        using var factory = NewBackgroundFactory();
        var swaps = new List<PredictionOrchestrator?>();
        factory.StackSwapped += o => swaps.Add(o);

        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        // No Pump(): the build finishes on the pool thread and its continuation may queue, but
        // Dispose must cancel/ignore it rather than let a swap hit a torn-down factory.
        factory.Dispose();

        Drain(); // run any marshal that DID queue: its re-check must drop it silently
        Thread.Sleep(200); // give a late continuation a beat to (wrongly) enqueue

        Check.That(swaps).IsEmpty();   // the thing this test's name has always promised
        Check.That(factory.Current).IsNull();
    }

    [Fact]
    public void DisableDuringInFlightBuild_TheBuildMustNotSwitchPredictionBackOn()
    {
        // Review finding 1 (the blocker): the disable path never bumped the generation
        // counter, so a build started before the disable passed every generation check and
        // swapped a live prediction stack back in — re-enabling what the user turned off,
        // ONNX session included, until the next rebuild.
        using var factory = NewBackgroundFactory();
        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        Pump();

        var inFlight = factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        Check.That(inFlight).IsNotNull(); // an old stack is serving while the build runs

        var disabled = Settings();
        disabled.Keyboard.PredictionEnabled = false;
        Check.That(factory.Rebuild(disabled, _baseDir, _appDataDir, modelRegistry: null)).IsNull();

        // Drain for long enough that the in-flight build's continuation and marshal both
        // arrive and run — a single Pump() can drain-and-exit before the build's marshal is
        // enqueued, which would pass this test without exercising anything.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            while (_uiQueue.Count > 0)
                _uiQueue.Dequeue()();
            Thread.Sleep(50);
        }

        Check.That(factory.Current).IsNull(); // prediction stays OFF — the build died in its checks
    }

    [Fact]
    public void GenerationAdvanceBetweenPoolCheckAndMarshal_TheStaleMarshalIsDropped()
    {
        // The marshal re-check exists for exactly one window: a build's CONTINUATION passed the
        // pool-thread generation check, then a newer Rebuild bumped the generation BEFORE the
        // marshaled swap ran. Deleting the re-check (review mutation A) survived the whole suite
        // because no test could hit that window — the queue-level supersede test never gets a
        // stale item past the pool check. This test runs the FIRST marshal only AFTER a second
        // Rebuild has advanced the generation, which is the window itself.
        using var factory = NewBackgroundFactory();
        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        Pump();
        var serving = factory.Current;

        var swaps = new List<PredictionOrchestrator?>();
        factory.StackSwapped += o => swaps.Add(o);

        // Generation 1: wait until its marshal is ENQUEUED — that means its pool-thread
        // generation check has already passed.
        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_uiQueue.Count == 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        Check.WithCustomMessage("generation-1 marshal never enqueued").That(_uiQueue.Count).IsStrictlyGreaterThan(0);

        // Now advance the generation (rebuild 2), THEN run the stale gen-1 marshal.
        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        _uiQueue.Dequeue()(); // the stale marshal: its re-check must drop it

        Check.That(swaps).IsEmpty();                              // no event from the stale build
        Check.That(ReferenceEquals(factory.Current, serving)).IsTrue(); // nothing swapped

        Pump(); // generation 2's marshal lands
        Check.That(swaps).HasSize(1);
        Check.That(ReferenceEquals(factory.Current, serving)).IsFalse();
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
