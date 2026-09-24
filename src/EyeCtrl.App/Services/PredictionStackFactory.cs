namespace EyeCtrl.App.Services;

using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.LM;
using Microsoft.Extensions.Logging;
using Serilog;

/// <summary>
/// Builds prediction stacks (dictionaries + sentence-completion trie + tokenizer + ONNX LM).
/// <para>
/// <b>Background mode (production):</b> constructed with a <c>postToUi</c> marshal,
/// <see cref="Rebuild"/> returns the CURRENT stack immediately — the old one keeps serving while
/// the new one builds on the thread pool. Building the stack is multi-second work (dictionaries,
/// a 2–3 MB trie, and an ONNX <c>InferenceSession</c> for a 360M model); running it on the UI
/// thread froze the splash at boot and froze all gaze UI on every settings-apply while a
/// gaze-only user was mid-interaction. When the build completes, the factory merges the words
/// learned on the old stack during the build window into the new stack, saves + disposes the
/// old, swaps, and raises <see cref="StackSwapped"/> on the UI thread.
/// </para>
/// <para>
/// <b>Synchronous mode (default constructor, tests):</b> <see cref="Rebuild"/> builds inline and
/// returns the new stack — the historical contract, kept byte-for-byte so existing callers and
/// tests are unaffected.
/// </para>
/// </summary>
public class PredictionStackFactory : IDisposable
{
    /// <summary>
    /// Minimal MEL-to-Serilog bridge for Core types constructed outside DI (review finding 9:
    /// the dictionary provider's IO failures were silently swallowed because no logger was
    /// passed). Category = T's name, level-mapped.
    /// </summary>
    private static class SerilogLoggerProvider<T>
    {
        public static ILogger<T> Create() => new Bridge();
        private sealed class Bridge : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var message = $"{typeof(T).Name}: {formatter(state, exception)}";
                if (logLevel == LogLevel.Error)
                    global::Serilog.Log.Error(exception, "{Message}", message);
                else
                    global::Serilog.Log.Warning(exception, "{Message}", message);
            }
        }
    }

    private PredictionOrchestrator? _current;
    private volatile bool _disposed;
    private readonly Action<Action>? _postToUi;
    private readonly CancellationTokenSource _disposeCts = new();
    private int _buildGeneration;

    // Tombstones for ForgetWords, stamped with the build generation current at forget time.
    // Only a build that was ALREADY in flight (generation <= the stamp) loaded pre-forget disk
    // state — for it, the swap's ApplyLearnedDelta (which only ever ADDS) would resurrect the
    // word, so the swap drains it. A LATER build loads the rewritten files and needs no
    // correction; its swap is where older stamps expire, so a deliberate re-learn in between
    // (Add Word key) is not fighting a stale tombstone. Guarded by its own lock: ForgetWords
    // arrives on the UI thread, the drain runs in the swap (also UI, but the map deserves no
    // ordering assumptions).
    private readonly object _forgetLock = new();
    private readonly Dictionary<string, int> _pendingForgets = [];

    /// <summary>
    /// Raised on the UI THREAD (through the constructor's marshal) after a background build
    /// swapped a new stack in — including a swap to <c>null</c>. The second argument is the
    /// verdict THE SWAPPED BUILD reported, or null when it verified nothing (prediction off,
    /// model off/absent, no manifest hash, swap-to-null). Subscribers re-bind their consumer
    /// and patch the stored snapshot (KeyboardCoordinator). Not raised in synchronous mode:
    /// the caller already holds the returned stack, and the sync branch reports to the ledger
    /// directly.
    /// </summary>
    public event Action<PredictionOrchestrator?, (string ModelId, ModelVerificationOutcome Outcome)?>? StackSwapped;

    public PredictionStackFactory(Action<Action>? postToUi = null)
    {
        _postToUi = postToUi;
    }

    public PredictionOrchestrator? Current => _current;

    /// <summary>Test override for predictor construction. Production: null — the real
    /// SmallLMPredictor(modelsDir, modelSize, preferGPU, tokenizer, modelPath) runs inside
    /// Build.</summary>
    internal Func<string, bool, LMTokenizer?, string, ILanguageModelPredictor?>?
        PredictorFactoryOverride { get; init; }

    public PredictionOrchestrator? Rebuild(
        EyeCtrlSettings settings,
        string baseDir,
        string appDataDir,
        ModelRegistryService? modelRegistry)
    {
        if (!settings.Keyboard.PredictionEnabled)
        {
            // Cheap (a save + dispose, no builds) and must take effect immediately even in
            // background mode: the user just turned prediction OFF.
            // The generation bump is load-bearing (review finding 1): without it, an in-flight
            // build started BEFORE the disable still passes every generation check and swaps a
            // live prediction stack back in — silently re-enabling what the user turned off.
            Interlocked.Increment(ref _buildGeneration);
            SaveAndDisposeCurrent();
            _current = null;
            if (_postToUi is not null)
                _postToUi(() => StackSwapped?.Invoke(null, null));
            return null;
        }

        var generation = Interlocked.Increment(ref _buildGeneration);

        if (_postToUi is null)
        {
            // Synchronous mode: save + dispose the old, build the new from the just-saved files,
            // return it — identical to the pre-background behaviour. No tombstone drain here,
            // on purpose: the save wrote post-forget state and the build loads those files, so
            // there is no stale-disk window to correct (the asymmetry with the swap is real but
            // not an omission — stamps from this mode simply expire at any later background swap).
            SaveAndDisposeCurrent();
            var (orchestrator, verification) = Build(settings, baseDir, appDataDir, modelRegistry, CancellationToken.None);
            _current = orchestrator;
            // Single-threaded here — no discard window: report to the ledger immediately, then
            // hand the caller the stack it just built.
            if (verification is not null && modelRegistry is not null)
                modelRegistry.ReportVerification(
                    verification.Value.ModelId, verification.Value.Outcome);
            return orchestrator;
        }

        // Background mode. Snapshot the old stack's full learned state NOW, on the caller's (UI)
        // thread — cheap dictionary copies, no IO (review finding 3: the save this replaces was
        // a 6-file, up-to-37 MB write set ON the UI thread). The snapshot is persisted at the
        // HEAD of the build task, so the new stack still loads a disk state that includes
        // everything learned before the rebuild started (the ordering the synchronous flow got
        // for free), while serialization and writing happen off the UI thread.
        var before = _current?.WordPredictor.CaptureLearnedState();

        _ = Task.Run(
            () =>
            {
                if (before is not null)
                {
                    try
                    {
                        WriteStateToDisk(before, baseDir, appDataDir, settings);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to persist learned state before background build; the build continues from the last on-disk state");
                    }
                }
                return Build(settings, baseDir, appDataDir, modelRegistry, _disposeCts.Token);
            },
            _disposeCts.Token)
            .ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        Log.Error(t.Exception, "Background prediction-stack build failed; the previous stack keeps serving");
                        return;
                    }

                    // Explicit type: a ternary between null and the (value-tuple) result has no
                    // natural type (same trap as Build's `verification` local).
                    (PredictionOrchestrator? Orchestrator, (string ModelId, ModelVerificationOutcome Outcome)? Verification)? built =
                        t.IsCanceled ? null : t.Result;
                    var orchestrator = built?.Orchestrator;
                    var swapVerdict = built?.Verification;
                    if (orchestrator is null || _disposed || Volatile.Read(ref _buildGeneration) != generation)
                    {
                        // Superseded by a newer rebuild, cancelled by Dispose, or shutting down.
                        // DISCARD AND DISPOSE: a discarded stack still owns an LM predictor
                        // (an ONNX InferenceSession is native memory) — dropping the reference
                        // without Dispose leaks it.
                        orchestrator?.Dispose();
                        return;
                    }

                    _postToUi(() =>
                    {
                        // Re-check under the marshal: a newer rebuild may have arrived while this
                        // continuation waited for the UI thread.
                        if (_disposed || Volatile.Read(ref _buildGeneration) != generation)
                        {
                            orchestrator.Dispose();
                            return;
                        }
                        SwapInWithLearnedTail(orchestrator, before, generation);

                        // Publication point (plan review C3): the build CLASSIFIED; only the
                        // winning swap reports — to the ledger and to the swap event — so a
                        // superseded build can never write Failed over a repair's Verified.
                        if (swapVerdict is not null && modelRegistry is not null)
                            modelRegistry.ReportVerification(
                                swapVerdict.Value.ModelId, swapVerdict.Value.Outcome);
                        StackSwapped?.Invoke(_current, swapVerdict);
                    });
                },
                TaskScheduler.Default);

        // The OLD stack (or null on first build) — it keeps serving until the swap lands.
        return _current;
    }

    /// <summary>
    /// The background-mode swap, on the UI thread (word learning cannot interleave): diff the old
    /// stack's CURRENT full learned state against the pre-build snapshot and apply the delta to
    /// the new stack — words AND bigrams AND phrases AND casing. Then dispose the old stack
    /// WITHOUT saving: its pre-window state is already on disk (the build-task head wrote the
    /// snapshot), and its window state now lives in the new stack, whose own next save persists
    /// the union. Saving here instead would let the new stack's in-memory (pre-tail) state
    /// clobber the tail at ITS next save — the silent-destruction bug of review finding 2.
    /// </summary>
    private void SwapInWithLearnedTail(
        PredictionOrchestrator built, PredictionLearnedState? before, int generation)
    {
        var old = _current;
        if (old is not null && before is not null)
            built.WordPredictor.ApplyLearnedDelta(before, old.WordPredictor.CaptureLearnedState());

        // Drain forget-tombstones AFTER the delta: deletions that raced this build must win over
        // both the delta (which only adds) and the pre-forget state the build loaded from disk.
        // Only stamps at-or-before this build's generation apply; older stamps expire here.
        // Known and deliberate cost (round-2 review N4): a re-learn issued INSIDE the build
        // window is dropped — ApplyLearnedDelta cannot lower, and the tombstone wins; the word
        // is on disk only if a later save wrote it, so re-teaching after the swap sticks.
        ApplyPendingForgetsTo(built.WordPredictor, generation);

        try
        {
            old?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to dispose the superseded prediction stack");
        }
        _current = built;
    }

    /// <summary>
    /// Removes words from the live prediction stack NOW (in-memory plus an immediate persist) and
    /// records them as tombstones so a background build already in flight cannot resurrect them
    /// at swap time. No live stack yet (prediction disabled or pre-keyboard-init): the tombstone
    /// still arms the next swap, and the caller's own file rewrite is the durable half.
    /// </summary>
    public void ForgetWords(IEnumerable<string> words)
    {
        List<string> forgotten = [];
        var generation = Volatile.Read(ref _buildGeneration);
        lock (_forgetLock)
        {
            foreach (var word in words)
            {
                if (string.IsNullOrWhiteSpace(word)) continue;
                var lower = word.ToLowerInvariant();
                // Arm/refresh the tombstone AND queue the live removal regardless of whether the
                // word was already pending: gating the live remove on the tombstone dedup meant a
                // repeated forget (delete, re-learn, delete again) left the word LIVE while the
                // page showed it gone (review finding 1).
                _pendingForgets[lower] = generation;
                forgotten.Add(lower);
            }
        }

        var current = _current;
        if (current is not null && forgotten.Count > 0)
        {
            foreach (var word in forgotten)
                current.WordPredictor.ForgetWord(word);
            current.WordPredictor.SaveUserDictionary();
        }
    }

    /// <summary>The live stack's predictor, when one is serving (null before the first build,
    /// while prediction is disabled, and pre-keyboard-init). Read on the UI thread only —
    /// that is where _current is written (the swap posts to it) and where the settings page
    /// asks; WordFrequencies copies the bare underlying dictionary, so a cross-thread read
    /// would race the learning writes.</summary>
    public WordPredictor? CurrentWordPredictor => _current?.WordPredictor;

    /// <summary>
    /// Applies the forget-tombstones that THIS build needs and expires the rest, against the
    /// freshly swapped-in stack. Internal for direct testing; the swap is its only production
    /// caller. A stamp applies when the forget happened at-or-after this build's generation
    /// started (the build read pre-forget disk); a stamp from an EARLIER generation is expired
    /// by this build's clean disk read and is dropped, not applied — so a re-learn that happened
    /// between the forget and a later build survives (review finding 2).
    /// </summary>
    internal void ApplyPendingForgetsTo(WordPredictor predictor, int buildGeneration)
    {
        lock (_forgetLock)
        {
            if (_pendingForgets.Count == 0) return;
            List<string> applies = [];
            List<string> expired = [];
            foreach (var (word, forgottenAt) in _pendingForgets)
                (forgottenAt >= buildGeneration ? applies : expired).Add(word);
            foreach (var word in expired)
                _pendingForgets.Remove(word);
            foreach (var word in applies)
            {
                predictor.ForgetWord(word);
                _pendingForgets.Remove(word);
            }
        }
    }

    /// <summary>
    /// Persists a learned-state snapshot through a provider, off the UI thread. Mirrors what
    /// <c>WordPredictor.SaveUserDictionary</c> writes, but from an already-captured snapshot.
    /// </summary>
    private static void WriteStateToDisk(
        PredictionLearnedState state, string baseDir, string appDataDir, EyeCtrlSettings settings)
    {
        var langCode = KeyboardSettings.IsValidLanguagePack(settings.Keyboard.LanguagePack)
            ? settings.Keyboard.LanguagePack
            : "";
        var provider = new FileDictionaryProvider(
            Path.Combine(baseDir, "Resources", "Dictionaries", $"{langCode}.json"),
            appDataDir,
            langCode,
            SerilogLoggerProvider<FileDictionaryProvider>.Create());

        provider.Save(new Dictionary<string, int>(state.Words));
        provider.SaveBigrams(Unwrap(state.Bigrams));
        provider.SaveBigramTimestamps(Unwrap(state.BigramTimestamps));
        provider.SavePhrases(Unwrap(state.Phrases));
        provider.SavePhraseTimestamps(Unwrap(state.PhraseTimestamps));
        provider.SaveCasingPreferences(Unwrap(state.Casing));
    }

    private static Dictionary<string, Dictionary<string, TValue>> Unwrap<TValue>(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, TValue>> nested)
    {
        var result = new Dictionary<string, Dictionary<string, TValue>>(nested.Count);
        foreach (var (key, inner) in nested)
            result[key] = new Dictionary<string, TValue>(inner);
        return result;
    }

    /// <summary>
    /// The whole C2 decision in one pure function (plan review M1 — table-tested, so a
    /// mutation of any cause fails). Returns whether the verdict should be REPORTED at all:
    /// a model that is absent or not hash-gated is exempt (custom models never carry a hash).
    /// </summary>
    internal static (bool Reports, ModelVerificationOutcome Outcome) ClassifyLmOutcome(
        bool modelOnDisk, bool hashGated, bool hashOk,
        bool predictorAvailable, bool tokenizerLoaded)
    {
        if (!modelOnDisk || !hashGated)
            return (false, ModelVerificationOutcome.NotYetVerified);
        if (!hashOk || !predictorAvailable || !tokenizerLoaded)
            return (true, ModelVerificationOutcome.Failed); // N3: Failed IS published
        return (true, ModelVerificationOutcome.Verified);
    }

    private (PredictionOrchestrator? Orchestrator, (string ModelId, ModelVerificationOutcome Outcome)? Verification) Build(
        EyeCtrlSettings settings,
        string baseDir,
        string appDataDir,
        ModelRegistryService? modelRegistry,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        // Declared BEFORE the prediction-disabled early-outs and the LM block; stays null for
        // every path that verifies nothing.
        (string ModelId, ModelVerificationOutcome Outcome)? verification = null;

        var langCode = settings.Keyboard.LanguagePack;
        if (!KeyboardSettings.IsValidLanguagePack(langCode))
        {
            // Defense in depth on top of SettingsService.Load's sanitize: the code is a path
            // ingredient on the dictionary/trie READ paths and the user-dictionary WRITE path,
            // so nothing but a two-letter code (or empty) may reach Path.Combine. Invalid falls
            // back to the no-dictionary degradation (FileDictionaryProvider handles a missing file).
            Log.Warning("Ignoring malformed language pack '{Lang}'; prediction runs without dictionaries.", langCode);
            langCode = "";
        }
        var dictPath = Path.Combine(baseDir, "Resources", "Dictionaries", $"{langCode}.json");
        var provider = new FileDictionaryProvider(dictPath, appDataDir, langCode,
            SerilogLoggerProvider<FileDictionaryProvider>.Create());
        var wordPredictor = new WordPredictor(provider, settings.Keyboard.ActiveLayout);
        wordPredictor.ApplyDecay(settings.Keyboard.DecayEnabled, settings.Keyboard.DecayHalfLifeDays);

        token.ThrowIfCancellationRequested();

        SentenceCompletionIndex? trieIndex = null;
        var triePath = Path.Combine(baseDir, "Resources", "Completions", $"completions_{langCode}.bin");
        if (File.Exists(triePath))
        {
            try
            {
                using var trieStream = File.OpenRead(triePath);
                trieIndex = SentenceCompletionIndex.Load(trieStream);
                Log.Information("Loaded sentence completion trie: {Path}", triePath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load sentence completion trie from {Path}", triePath);
            }
        }

        token.ThrowIfCancellationRequested();

        ILanguageModelPredictor? lmPredictor = null;
        if (settings.Keyboard.MLSuggestionsEnabled && modelRegistry != null)
        {
            var modelsDir = Path.Combine(appDataDir, "models");
            var modelSize = settings.Keyboard.MLModelSize;

            if (!string.IsNullOrEmpty(modelSize) && modelSize != "Off")
            {
                LMTokenizer? tokenizer = null;
                var tokenizerPath = modelRegistry.GetTokenizerPath(modelSize);

                try
                {
                    if (File.Exists(tokenizerPath))
                    {
                        // Integrity at LOAD time (the deferred audit item, safe here because this
                        // runs on the background thread — hashing the model never touches the UI):
                        // the download path already verifies the same value, but a file replaced
                        // on disk directly bypasses it. An empty registry hash (custom models, by
                        // design) skips the check.
                        var expectedTokenizerHash = modelRegistry.GetTokenizerSha256(modelSize);
                        if (expectedTokenizerHash is null || VerifyFileSha256(tokenizerPath, expectedTokenizerHash))
                        {
                            tokenizer = new LMTokenizer(tokenizerPath);
                        }
                        else
                        {
                            Log.Error(
                                "Tokenizer at {Path} failed its SHA-256 integrity check; LM suggestions disabled for this build",
                                tokenizerPath);
                        }
                    }
                    else
                    {
                        Log.Warning("Tokenizer file not found at {Path}; LM suggestions disabled", tokenizerPath);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to load tokenizer from {Path}; LM suggestions disabled", tokenizerPath);
                }

                var modelPath = modelRegistry.GetModelPath(modelSize);
                var expectedModelHash = modelRegistry.GetModelSha256(modelSize);

                (bool Reports, ModelVerificationOutcome Outcome) verdict =
                    (false, ModelVerificationOutcome.NotYetVerified);

                // Same load-time check for the model itself — most valuable here: the model is
                // EXECUTED (ONNX runtime parses and runs it), so a swapped file is not just wrong
                // predictions, it is unvalidated input to a large native parse surface.
                var hashMatches = expectedModelHash is not null
                    && File.Exists(modelPath)
                    && VerifyFileSha256(modelPath, expectedModelHash);

                if (expectedModelHash is not null && File.Exists(modelPath) && !hashMatches)
                {
                    // Mismatch arm: unchanged behaviour — the predictor is NOT constructed.
                    Log.Error(
                        "Model at {Path} failed its SHA-256 integrity check; LM suggestions disabled for this build",
                        modelPath);
                }
                else
                {
                    // Unchanged arm: hash matches, OR no hash (custom), OR file absent —
                    // construct exactly as today (plan review C1).
                    token.ThrowIfCancellationRequested(); // no point building a session we are discarding

                    // (The seam takes modelSize, not modelsDir: a stub overrides per model
                    // identity, and modelPath already carries the location.)
                    lmPredictor = PredictorFactoryOverride is not null
                        ? PredictorFactoryOverride(modelSize, settings.Keyboard.PreferGPU, tokenizer, modelPath)
                        : new SmallLMPredictor(
                            modelsDir, modelSize, settings.Keyboard.PreferGPU,
                            tokenizer: tokenizer, modelPath: modelPath);
                }

                // I1: the report gate must EQUAL the snapshot's read gate —
                // KeyboardStackSnapshot.ActiveModelVerificationOf reads the ledger only when
                // IsModelDownloaded(id) (BOTH files on disk), so a model whose tokenizer alone
                // is absent must report NOTHING. Reporting Failed there fed a perpetual reload
                // loop: the swap patches Failed into the stored snapshot while every compare
                // capture reads NotYetVerified (IsModelDownloaded false), so every settings
                // apply reloaded the keyboard forever. Such a model's Models row is a Download
                // row, not a repair row. No outcome change for the corrupt-model case (both
                // files present): still Failed.
                verdict = ClassifyLmOutcome(
                    modelOnDisk: modelRegistry.IsModelDownloaded(modelSize),
                    hashGated: expectedModelHash is not null,
                    hashOk: hashMatches,
                    predictorAvailable: lmPredictor is not null && lmPredictor.IsAvailable,
                    tokenizerLoaded: tokenizer is not null);

                // N6: assigned HERE, inside the LM block, where `modelSize` is in scope.
                if (verdict.Reports)
                    verification = (ModelId: modelSize, Outcome: verdict.Outcome);
            }
        }

        try
        {
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            // Cancellation landing between the LM predictor's construction and the final check
            // would otherwise orphan its native InferenceSession — every other discard path
            // disposes (review finding 6).
            lmPredictor?.Dispose();
            throw;
        }

        return (new PredictionOrchestrator(
            wordPredictor, trieIndex, lmPredictor, settings.Keyboard.MLSuggestionsEnabled),
            verification);
    }

    /// <summary>Streaming SHA-256 of a file compared case-insensitively against a hex digest.
    /// Internal for tests.</summary>
    internal static bool VerifyFileSha256(string path, string expectedHex)
    {
        try
        {
            // FileShare.Read | FileShare.Delete, NOT File.OpenRead's plain FileShare.Read
            // (issue #166 M9): a repair re-download's final File.Move(overwrite: true) must be
            // able to replace this file WHILE a background build is mid-hash of it — under a
            // sharing mode without Delete the Move fails (IOException, sharing violation) and
            // the repair dies. The hash may then reflect the pre-replace content; the repair's
            // own reload rebuilds and re-verifies, so nothing has to coordinate the two.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            var actual = Convert.ToHexString(hash);
            return string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to hash {Path} for integrity verification", path);
            return false; // fail closed: an unreadable file must not load as if verified
        }
    }

    public void SaveAndDisposeCurrent()
    {
        if (_current != null)
        {
            // Save must not prevent disposal: a failed dictionary write should still let the old
            // orchestrator (and its LM predictor) be disposed, otherwise a throwing save leaks it.
            try
            {
                _current.WordPredictor.SaveUserDictionary();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to save user dictionary before prediction-stack rebuild");
            }
            _current.Dispose();
            _current = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _disposeCts.Cancel();
        SaveAndDisposeCurrent();
        _disposeCts.Dispose();
    }
}
