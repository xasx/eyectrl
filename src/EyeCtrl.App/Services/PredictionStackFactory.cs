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

    // The build-in-flight state (loading hint). Holds the GENERATION of the newest started
    // background build, 0 = none: a generation, not a bool, because superseded builds end
    // while a newer one is still running, and only the newest build's end may clear the
    // flag — a plain bool cleared by the older build would hide the hint mid-build.
    // _buildGeneration is never reused, so a live generation uniquely identifies one build.
    private int _inFlightGeneration;

    /// <summary>Whether a background build is in flight — the state behind the prediction
    /// loading hint. Thread-safe read; transitions notify <see cref="BuildInFlightChanged"/>
    /// on the UI thread.</summary>
    public bool BuildInFlight => Volatile.Read(ref _inFlightGeneration) != 0;

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

    /// <summary>
    /// Raised on the UI thread whenever a background build starts or stops being
    /// <see cref="BuildInFlight"/>. KeyboardCoordinator forwards it to the keyboard window,
    /// which shows the prediction loading hint while the FIRST build is still running (the
    /// keyboard opens with an empty bar until the swap). The START is raised directly on
    /// Rebuild's calling (UI) thread; the ENDS raised inside the swap marshal run on that
    /// marshal's thread, and pool-side ends (fault, discard) post through the same marshal as
    /// <see cref="StackSwapped"/>. Cleared when a build ends in ANY way — swap, superseded,
    /// failed, or cancelled — so a dead build can never leave the hint stuck. Not raised in
    /// synchronous mode: the build completes inline, before any subscriber could observe the
    /// state.
    /// </summary>
    public event Action<bool>? BuildInFlightChanged;

    public PredictionStackFactory(Action<Action>? postToUi = null)
    {
        _postToUi = postToUi;
    }

    public PredictionOrchestrator? Current => _current;

    /// <summary>
    /// Marks <paramref name="generation"/>'s build as the one in flight (a newer build
    /// overwrites an older one's marker). The notification is raised DIRECTLY, not posted:
    /// <see cref="Rebuild"/>'s caller is the UI thread — the same thread the marshal would
    /// land on — so a post would only delay the hint behind LoadKeyboard's remaining work
    /// while adding queue traffic between the caller and the build's own marshal.
    /// </summary>
    private void MarkBuildStarted(int generation)
    {
        Interlocked.Exchange(ref _inFlightGeneration, generation);
        BuildInFlightChanged?.Invoke(true);
    }

    /// <summary>
    /// Ends <paramref name="generation"/>'s tenure as the in-flight build — but ONLY if it is
    /// still the newest: an older, superseded build ending here must not clear the state while
    /// a newer build is still running (the CAS fails silently and the newer build's own end
    /// clears it). Called from every end path: swap, superseded, failed, cancelled.
    /// <paramref name="alreadyOnUiThread"/> is true for the ends raised inside the swap
    /// marshal, whose body runs on the UI thread: raising directly keeps the notification
    /// ordered AFTER StackSwapped's handlers without a second dispatcher hop; the pool-side
    /// ends (fault, discard) must post through the marshal like everything else that leaves
    /// the thread pool.
    /// <para>
    /// The posted notification is LEVEL-TRIGGERED — it reads <see cref="BuildInFlight"/> at
    /// DELIVERY time, not the false the ending build knows (review 01 F1): a newer build can
    /// start between this CAS on the pool thread and the delivery, and a snapshot false would
    /// arrive after the newer build's start and tell the window no build is in flight while
    /// one is — the newer FIRST build would get no hint. The delivered value can only be a
    /// redundant true (the window de-duplicates), never a lie.
    /// </para>
    /// </summary>
    private void MarkBuildEnded(int generation, bool alreadyOnUiThread)
    {
        if (Interlocked.CompareExchange(ref _inFlightGeneration, 0, generation) != generation)
            return;
        if (alreadyOnUiThread)
            BuildInFlightChanged?.Invoke(BuildInFlight);
        else if (_postToUi is not null)
            _postToUi(() => BuildInFlightChanged?.Invoke(BuildInFlight));
    }

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

        MarkBuildStarted(generation);

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
                        MarkBuildEnded(generation, alreadyOnUiThread: false);
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
                        MarkBuildEnded(generation, alreadyOnUiThread: false);
                        return;
                    }

                    _postToUi(() =>
                    {
                        // Re-check under the marshal: a newer rebuild may have arrived while this
                        // continuation waited for the UI thread.
                        if (_disposed || Volatile.Read(ref _buildGeneration) != generation)
                        {
                            orchestrator.Dispose();
                            MarkBuildEnded(generation, alreadyOnUiThread: true);
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
                        // AFTER StackSwapped: the re-bind lands first, so the window's swap
                        // refresh renders suggestions; the not-in-flight notification that
                        // follows only re-renders the (now identical) bar without the hint.
                        MarkBuildEnded(generation, alreadyOnUiThread: true);
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
                    // The predictor gets a REAL logger (the LM release's gate 4): without one,
                    // every "Using DirectML (GPU) for {Model}", the silent CPU fallback and the
                    // load failures were dropped, so the day log could not say which provider
                    // ran. SerilogStaticLogger forwards to the CURRENT static Serilog pipeline
                    // on every call (a SerilogLoggerFactory captures the instance at
                    // CreateLogger, so a logger created before a swap keeps the old sink) and
                    // keeps the rendered text prefix-free — the bridge maps every level 1:1;
                    // only the app's configured minimum (Information in the day log) filters.
                    lmPredictor = PredictorFactoryOverride is not null
                        ? PredictorFactoryOverride(modelSize, settings.Keyboard.PreferGPU, tokenizer, modelPath)
                        : new SmallLMPredictor(
                            modelsDir, modelSize, settings.Keyboard.PreferGPU,
                            logger: new SerilogStaticLogger<SmallLMPredictor>(),
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

    /// <summary>
    /// Opens a file for concurrent hash verification with
    /// <see cref="ModelDownloadService.ConcurrentHashReaderShare"/> — the sharing mode the
    /// install's File.Replace requires of open readers (issue #166 round-2 M-1: one constant
    /// binds both sides of the contract). Internal so tests can probe the actual share-mode
    /// behaviourally. VerifyFileSha256 must open through THIS helper, never File.OpenRead.
    /// </summary>
    internal static FileStream OpenForHash(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read,
            ModelDownloadService.ConcurrentHashReaderShare);

    /// <summary>Streaming SHA-256 of a file compared case-insensitively against a hex digest.
    /// Internal for tests.</summary>
    internal static bool VerifyFileSha256(string path, string expectedHex)
    {
        try
        {
            // Opened through OpenForHash — FileShare.Read|Delete via the shared constant
            // (issue #166 round-1 I1 / round-2 M-1): a repair re-download that races an open
            // hash stream installs with File.Replace, which works against open readers only
            // when they hold Delete share. The hash may reflect pre-replace bytes. The
            // download flow's own ApplyAndSave runs after the install returns and triggers a
            // rebuild when the post-install capture differs from the stored snapshot — every
            // build hashes unconditionally, so a rebuild re-verifies the final files.
            // UNMEASURED residual (round-2 N-6, recorded on issue #166): a stale build's
            // Failed verdict landing between the repair's Verified ledger write and that
            // ApplyAndSave can suppress the rebuild (snapshot and fresh capture both read
            // Failed) — ordering not measured either way.
            using var stream = OpenForHash(path);
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
        // The in-flight marker dies here without a notification: at teardown the dispatcher
        // this would post to may already be going away, and a stale build's own end path
        // (which re-checks _disposed) would only post the same clear again.
        Interlocked.Exchange(ref _inFlightGeneration, 0);
        SaveAndDisposeCurrent();
        _disposeCts.Dispose();
    }
}
