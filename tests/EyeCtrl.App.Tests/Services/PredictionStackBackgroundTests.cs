namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Text.Json;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Settings.Pages;
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
        factory.StackSwapped += (o, _) => swappedSeen.Add(o);

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
    public void InflatedUserFile_FirstBuildThenABackgroundRebuild_BacksUpTheOriginalOnce()
    {
        var basePath = Path.Combine(_baseDir, "Resources", "Dictionaries", "en.json");
        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
        File.WriteAllText(basePath, JsonSerializer.Serialize(new
        {
            words = new Dictionary<string, int> { ["hello"] = 100, ["hallo"] = 5 },
        }));

        var userPath = Path.Combine(_appDataDir, "user_dictionary_en.json");
        File.WriteAllText(userPath, JsonSerializer.Serialize(new Dictionary<string, int>
        {
            ["hello"] = 57 * 100 + 3,
            ["hallo"] = 57 * 5 + 2,
        }));
        var original = File.ReadAllText(userPath);

        using var factory = NewBackgroundFactory();
        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        Pump();
        Check.That(factory.Current).IsNotNull();

        // The rebuild writes the repaired snapshot through a fresh provider; the original
        // inflated file must be kept before that write.
        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        Pump();

        var backupPath = userPath + ".bak-inflated";
        Check.That(File.Exists(backupPath)).IsTrue();
        Check.That(File.ReadAllText(backupPath)).IsEqualTo(original);

        var written = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(userPath))!;
        Check.That(written["hello"]).IsEqualTo(3);
        Check.That(written["hallo"]).IsEqualTo(2);
    }

    [Fact]
    public void BackgroundRebuilds_WriteOnlyTheLearnedDelta_TheUserFileNeverGrows()
    {
        // The background path saves a merged snapshot through a FRESH provider in WriteStateToDisk.
        // Without the on-demand base load that provider writes the whole dictionary back, so each
        // rebuild adds another copy of the base to the user file.
        var basePath = Path.Combine(_baseDir, "Resources", "Dictionaries", "en.json");
        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
        File.WriteAllText(basePath, JsonSerializer.Serialize(new
        {
            words = new Dictionary<string, int> { ["hello"] = 100 },
        }));

        using var factory = NewBackgroundFactory();
        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        Pump();
        Check.That(factory.Current).IsNotNull();

        factory.Current!.LearnWord("zebratail");

        var userPath = Path.Combine(_appDataDir, "user_dictionary_en.json");
        for (var rebuild = 0; rebuild < 3; rebuild++)
        {
            factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
            Pump();

            var written = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(userPath))!;
            Check.WithCustomMessage(
                    $"rebuild {rebuild}: {string.Join(",", written.Select(kv => $"{kv.Key}={kv.Value}"))}")
                .That(written).HasSize(1);
            Check.That(written["zebratail"]).IsEqualTo(1);

            // A genuine delta file is not inflated: no spurious backup may appear.
            Check.WithCustomMessage($"rebuild {rebuild}: a backup was made for a delta file")
                .That(File.Exists(userPath + ".bak-inflated")).IsFalse();
        }
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
        factory.StackSwapped += (_, _) => swappedSeen++;

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
        factory.StackSwapped += (o, _) => swapped.Add(o);

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
        factory.StackSwapped += (o, _) => swaps.Add(o);

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
        factory.StackSwapped += (o, _) => swaps.Add(o);

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

    // ── Verdict publication (plan review C3/N7: only the WINNING swap publishes) ─────────

    // Plan review N7 + R1: only the WINNING swap publishes (round-1 C3), and the test must be
    // able to FAIL for exactly that regression. The two builds therefore DISAGREE: build #1's
    // seam returns an AVAILABLE stub (classifies Verified — the dangerous verdict), build #2's
    // an UNAVAILABLE one (classifies Failed) and wins. Deterministic shape: the draft's
    // thread-pool interleaving (loser parked inside the seam, finishing last) raced WHICH
    // build got seam call #1 — when the winner parked instead, Pump() deadlocked for 10s in
    // 4 of 5 runs. Instead, build #1 first runs all the way to its ENQUEUED marshal, THEN
    // Rebuild #2 supersedes it (so seam call #2 is provably the winner's), and only the
    // WINNER parks in the seam — until the stale marshal #1 has been drained through its
    // discard branch. A mutation that writes the ledger inside Build still fails this: the
    // loser's Verified would be readable BEFORE any swap has published (the winner is parked,
    // so nothing legitimate has written yet) — the mid-test NotYetVerified check pins that.
    [Fact]
    public void SupersededBuild_PublishesNothing_LoserFinishingLastWritesNothing()
    {
        using var scope = ModelTestFakes.Create();
        var (registry, _) = scope.WithHashedDownloadedBundled();

        var winnerMayFinish = new ManualResetEventSlim(false);
        var seamCalls = 0;
        var swaps = new List<(PredictionOrchestrator? Orchestrator,
            (string ModelId, ModelVerificationOutcome Outcome)? Verification)>();

        var factory = new PredictionStackFactory(work => _uiQueue.Enqueue(work))
        {
            PredictorFactoryOverride = (_, _, _, _) =>
            {
                if (Interlocked.Increment(ref seamCalls) == 1)
                    return new AvailableLmPredictor();   // build #1: classifies Verified
                winnerMayFinish.Wait();                  // build #2 parks in the seam
                return new UnavailableLmPredictor();     // classifies Failed — the winner
            },
        };
        var swapsGuard = new object();
        factory.StackSwapped += (o, v) => { lock (swapsGuard) swaps.Add((o, v)); };

        var settings = Settings();
        settings.Keyboard.MLSuggestionsEnabled = true;
        settings.Keyboard.MLModelSize = "135M";

        // Build #1 runs to completion on the pool. Wait until its marshal is ENQUEUED — that
        // means its pool-thread generation check has already passed (the same window as
        // GenerationAdvanceBetweenPoolCheckAndMarshal_TheStaleMarshalIsDropped).
        factory.Rebuild(settings, _baseDir, _appDataDir, registry);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_uiQueue.Count == 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        Check.WithCustomMessage("build #1 marshal never enqueued").That(_uiQueue.Count).IsStrictlyGreaterThan(0);

        // Build #2 supersedes it and parks in the seam — deterministic, because Rebuild #2 is
        // issued only after build #1's seam call already returned (marshal #1 is downstream
        // of it), so seam call #2 is always the winner's.
        factory.Rebuild(settings, _baseDir, _appDataDir, registry);

        // Run the stale marshal #1: its UI-side re-check sees generation 2 and must DISCARD
        // the superseded orchestrator (disposing its LM, PredictionOrchestrator.cs:150) and
        // publish NOTHING — no event, no ledger write.
        _uiQueue.Dequeue()();
        lock (swapsGuard) Check.That(swaps.Count).IsEqualTo(0);
        // Nothing has legitimately published yet (the winner is parked in the seam), so any
        // verdict in the ledger here was written by Build/classification itself — exactly the
        // C3 regression this test exists to catch (the loser's Verified leaking out).
        Check.That(registry.GetVerification("135M"))
            .IsEqualTo(ModelVerificationOutcome.NotYetVerified);

        // Release the winner: it finishes, swaps, and publishes Failed — exactly one swap,
        // and the ledger holds the winner's verdict, not the loser's.
        winnerMayFinish.Set();
        Pump();

        lock (swapsGuard) Check.That(swaps.Count).IsEqualTo(1);
        Check.That(registry.GetVerification("135M"))
            .IsEqualTo(ModelVerificationOutcome.Failed);
    }

    private sealed class AvailableLmPredictor : ILanguageModelPredictor
    {
        public bool IsAvailable => true;
        public void Predict(string context, string? prefix = null) { }
        public IReadOnlyList<PredictionResult>? TryGetResults() => null;
        public void Cancel() { }
        public void Dispose() { }
    }

    private sealed class UnavailableLmPredictor : ILanguageModelPredictor
    {
        public bool IsAvailable => false;
        public void Predict(string context, string? prefix = null) { }
        public IReadOnlyList<PredictionResult>? TryGetResults() => null;
        public void Cancel() { }
        public void Dispose() { }
    }

    // The winning swap's event argument equals the value written to the ledger — the two
    // publication channels cannot disagree.
    [Fact]
    public void WinningSwap_EventVerdict_MatchesLedger()
    {
        using var scope = ModelTestFakes.Create();
        var (registry, _) = scope.WithHashedDownloadedBundled();
        var factory = NewBackgroundFactory();
        (string ModelId, ModelVerificationOutcome Outcome)? eventVerdict = null;
        factory.StackSwapped += (_, v) => eventVerdict = v;

        var settings = Settings();
        settings.Keyboard.MLSuggestionsEnabled = true;
        settings.Keyboard.MLModelSize = "135M";
        factory.Rebuild(settings, _baseDir, _appDataDir, registry);
        Pump();

        // R3: NFluent-on-nullable-tuple is not a known overload — use HasValue.
        Check.That(eventVerdict.HasValue).IsTrue();
        Check.That(registry.GetVerification(eventVerdict!.Value.ModelId))
            .IsEqualTo(eventVerdict.Value.Outcome);
    }
}
