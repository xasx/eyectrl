namespace EyeCtrl.App.Services;

using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.LM;
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
    private PredictionOrchestrator? _current;
    private bool _disposed;
    private readonly Action<Action>? _postToUi;
    private readonly CancellationTokenSource _disposeCts = new();
    private int _buildGeneration;

    /// <summary>
    /// Raised on the UI THREAD (through the constructor's marshal) after a background build
    /// swapped a new stack in — including a swap to <c>null</c>. Subscribers re-bind their
    /// consumer (KeyboardCoordinator calls <c>SetOrchestrator</c>). Not raised in synchronous
    /// mode: the caller already holds the returned stack.
    /// </summary>
    public event Action<PredictionOrchestrator?>? StackSwapped;

    public PredictionStackFactory(Action<Action>? postToUi = null)
    {
        _postToUi = postToUi;
    }

    public PredictionOrchestrator? Current => _current;

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
            SaveAndDisposeCurrent();
            _current = null;
            if (_postToUi is not null)
                _postToUi(() => StackSwapped?.Invoke(null));
            return null;
        }

        var generation = Interlocked.Increment(ref _buildGeneration);

        if (_postToUi is null)
        {
            // Synchronous mode: save + dispose the old, build the new from the just-saved files,
            // return it — identical to the pre-background behaviour.
            SaveAndDisposeCurrent();
            _current = Build(settings, baseDir, appDataDir, modelRegistry, CancellationToken.None);
            return _current;
        }

        // Background mode. Persist the old stack's dictionary NOW, on the caller's (UI) thread:
        // the new stack LOADS from disk on the pool thread, so everything learned on the old
        // stack must be on disk before the build reads it (the synchronous flow got this for
        // free by disposing before building). Cheap: these are small JSON writes; the expensive
        // parts of a rebuild are the trie/ONNX/dictionary LOADS, which stay off the UI thread.
        try
        {
            _current?.WordPredictor.SaveUserDictionary();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save user dictionary before background rebuild");
        }

        // Snapshot AFTER the save, on the UI thread (word learning is UI-thread): the delta
        // against the swap-time state is exactly what was learned during the build window.
        var before = _current?.WordPredictor.WordFrequencies;

        _ = Task.Run(
            () => Build(settings, baseDir, appDataDir, modelRegistry, _disposeCts.Token),
            _disposeCts.Token)
            .ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        Log.Error(t.Exception, "Background prediction-stack build failed; the previous stack keeps serving");
                        return;
                    }

                    var built = t.IsCanceled ? null : t.Result;
                    if (built is null || _disposed || Volatile.Read(ref _buildGeneration) != generation)
                    {
                        // Superseded by a newer rebuild, cancelled by Dispose, or shutting down.
                        // DISCARD AND DISPOSE: a discarded stack still owns an LM predictor
                        // (an ONNX InferenceSession is native memory) — dropping the reference
                        // without Dispose leaks it.
                        built?.Dispose();
                        return;
                    }

                    _postToUi(() =>
                    {
                        // Re-check under the marshal: a newer rebuild may have arrived while this
                        // continuation waited for the UI thread.
                        if (_disposed || Volatile.Read(ref _buildGeneration) != generation)
                        {
                            built.Dispose();
                            return;
                        }
                        SwapInWithLearnedTail(built, before);
                        StackSwapped?.Invoke(_current);
                    });
                },
                TaskScheduler.Default);

        // The OLD stack (or null on first build) — it keeps serving until the swap lands.
        return _current;
    }

    /// <summary>
    /// The background-mode swap, on the UI thread (word learning cannot interleave): diff the old
    /// stack's current frequencies against the pre-build snapshot, apply the learned deltas to the
    /// new stack, then save + dispose the old (persisting the tail for the NEXT boot as well) and
    /// publish the new one.
    /// </summary>
    private void SwapInWithLearnedTail(PredictionOrchestrator built, IReadOnlyDictionary<string, int>? before)
    {
        var old = _current;
        if (old is not null && before is not null)
        {
            var deltas = new List<KeyValuePair<string, int>>();
            foreach (var (word, after) in old.WordPredictor.WordFrequencies)
            {
                var delta = after - before.GetValueOrDefault(word);
                if (delta > 0)
                    deltas.Add(new(word, delta));
            }
            if (deltas.Count > 0)
                built.WordPredictor.LearnWordDeltas(deltas);
        }

        SaveAndDisposeCurrent();
        _current = built;
    }

    private static PredictionOrchestrator? Build(
        EyeCtrlSettings settings,
        string baseDir,
        string appDataDir,
        ModelRegistryService? modelRegistry,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

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
        var provider = new FileDictionaryProvider(dictPath, appDataDir, langCode);
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

                // Same load-time check for the model itself — most valuable here: the model is
                // EXECUTED (ONNX runtime parses and runs it), so a swapped file is not just wrong
                // predictions, it is unvalidated input to a large native parse surface.
                var expectedModelHash = modelRegistry.GetModelSha256(modelSize);
                if (expectedModelHash is not null && File.Exists(modelPath)
                    && !VerifyFileSha256(modelPath, expectedModelHash))
                {
                    Log.Error(
                        "Model at {Path} failed its SHA-256 integrity check; LM suggestions disabled for this build",
                        modelPath);
                }
                else
                {
                    token.ThrowIfCancellationRequested(); // no point building a session we are discarding

                    lmPredictor = new SmallLMPredictor(
                        modelsDir,
                        modelSize,
                        settings.Keyboard.PreferGPU,
                        tokenizer: tokenizer,
                        modelPath: modelPath);
                }
            }
        }

        token.ThrowIfCancellationRequested();

        return new PredictionOrchestrator(wordPredictor, trieIndex, lmPredictor, settings.Keyboard.MLSuggestionsEnabled);
    }

    /// <summary>Streaming SHA-256 of a file compared case-insensitively against a hex digest.
    /// Internal for tests.</summary>
    internal static bool VerifyFileSha256(string path, string expectedHex)
    {
        try
        {
            using var stream = File.OpenRead(path);
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
