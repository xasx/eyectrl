namespace EyeCtrl.LM;

using EyeCtrl.Core.Prediction;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Collections.Concurrent;
using System.Text;

/// <summary>
/// ONNX runtime-based small language model predictor for next-word suggestions.
/// Uses a tokenizer to encode context and runs inference on a SmallLM ONNX model.
/// </summary>
public sealed class SmallLMPredictor : ILanguageModelPredictor
{
    private readonly string _modelsDirectory;
    private readonly string? _selectedModelSize;
    private readonly bool _preferGpu;
    private readonly ILogger<SmallLMPredictor>? _logger;
    private readonly LMTokenizer? _tokenizer;
    private readonly bool _ownsTokenizer;

    private InferenceSession? _session;
    private DecoderIoPlan? _ioPlan;
    private readonly object _lock = new();
    private readonly object _inferenceGate = new();
    private volatile bool _disposed;
    private CancellationTokenSource? _pendingCts;
    private volatile int _inferenceCount;
    private ConcurrentBag<PredictionResult>? _pendingResults;

    /// <summary>Whether the model was successfully loaded and is ready for inference.</summary>
    public bool IsAvailable { get; private set; }
    /// <summary>Full path to the loaded ONNX model file, or null if not loaded.</summary>
    public string? SelectedModelPath { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SmallLMPredictor"/> class.
    /// Loads the specified ONNX model and optional tokenizer.
    /// </summary>
    /// <param name="modelsDirectory">Directory containing model files.</param>
    /// <param name="modelSize">Model size identifier (e.g., "360M"), or "Off" to disable.</param>
    /// <param name="preferGpu">Whether to prefer GPU inference if available.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="tokenizer">Optional pre-loaded tokenizer instance.</param>
    /// <param name="modelPath">Optional explicit path to an ONNX model file instead of using the size-based lookup.</param>
    public SmallLMPredictor(string modelsDirectory, string? modelSize, bool preferGpu, ILogger<SmallLMPredictor>? logger = null, LMTokenizer? tokenizer = null, string? modelPath = null, bool ownsTokenizer = true)
    {
        _modelsDirectory = modelsDirectory;
        _selectedModelSize = modelSize;
        _preferGpu = preferGpu;
        _logger = logger;
        _tokenizer = tokenizer;
        _ownsTokenizer = tokenizer != null && ownsTokenizer;

        if (modelSize == "Off" || string.IsNullOrEmpty(modelSize))
        {
            IsAvailable = false;
            return;
        }

        if (_tokenizer == null)
        {
            _logger?.LogWarning("No tokenizer provided for {Model}; LM suggestions disabled", modelSize);
            IsAvailable = false;
            return;
        }

        Initialize(modelPath);
    }

    private void Initialize(string? explicitModelPath = null)
    {
        if (explicitModelPath != null)
        {
            SelectedModelPath = explicitModelPath;
        }
        else if (ModelConstants.ModelFileNames.TryGetValue(_selectedModelSize!, out var fileName))
        {
            SelectedModelPath = Path.Combine(_modelsDirectory, fileName);
        }
        else
        {
            _logger?.LogWarning("Unknown model size: {Size}", _selectedModelSize);
            IsAvailable = false;
            return;
        }
        if (!File.Exists(SelectedModelPath))
        {
            _logger?.LogInformation("Model file not found: {Path}", SelectedModelPath);
            IsAvailable = false;
            return;
        }

        SessionOptions? options = null;
        try
        {
            options = new SessionOptions();

            if (_preferGpu)
            {
                try
                {
                    options.AppendExecutionProvider_DML();
                    _logger?.LogInformation("Using DirectML (GPU) for {Model}", _selectedModelSize);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "DirectML not available, falling back to CPU");
                    options.Dispose();
                    options = new SessionOptions();
                }
            }

            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

            _session = new InferenceSession(SelectedModelPath, options);

            // Derive the feeding plan up front: a model this predictor cannot drive is caught at
            // load, where the user can see it, instead of throwing on every keystroke.
            _ioPlan = DecoderIoPlan.Build(Describe(_session.InputMetadata), Describe(_session.OutputMetadata));

            IsAvailable = true;
            _logger?.LogInformation("SmallLMPredictor initialized with {Model} ({CacheSlots} cache slots)",
                _selectedModelSize, _ioPlan.KvSlots.Count);
        }
        catch (NotSupportedException ex)
        {
            _logger?.LogError(ex, "Model {Model} declares a graph this predictor cannot drive", _selectedModelSize);
            _session?.Dispose();
            _session = null;
            IsAvailable = false;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize ONNX session for {Model}", _selectedModelSize);
            IsAvailable = false;
        }
        finally
        {
            // InferenceSession copies the options at construction; the SessionOptions
            // instance itself is not retained and must be disposed (native handle).
            options?.Dispose();
        }
    }

    /// <summary>Starts an asynchronous inference for next-word prediction based on context and optional prefix.</summary>
    /// <param name="context">The preceding text for context.</param>
    /// <param name="prefix">Optional prefix to filter predictions to.</param>
    public void Predict(string context, string? prefix = null)
    {
        if (_disposed || !IsAvailable || _session == null) return;

        lock (_lock)
        {
            _pendingCts?.Cancel();
            _pendingCts?.Dispose();
            _pendingCts = new CancellationTokenSource();
            _pendingResults = new ConcurrentBag<PredictionResult>();
            var bag = _pendingResults;

            var token = _pendingCts.Token;
            var session = _session;
            var capturePrefix = prefix;
            var captureContext = context;

            Interlocked.Increment(ref _inferenceCount);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (token.IsCancellationRequested) return;
                    if (_disposed) return;

                    var results = RunInference(session, captureContext, capturePrefix);
                    if (token.IsCancellationRequested) return;

                    foreach (var result in results)
                    {
                        if (token.IsCancellationRequested) break;
                        if (bag == null) break;
                        bag.Add(result);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "LM inference failed");
                }
                finally
                {
                    Interlocked.Decrement(ref _inferenceCount);
                    lock (_inferenceGate) Monitor.PulseAll(_inferenceGate);
                }
            });
        }
    }

    private List<PredictionResult> RunInference(InferenceSession session, string context, string? prefix)
    {
        var plan = _ioPlan!;
        var prompt = BuildPrompt(context, prefix);
        var inputIds = _tokenizer!.Encode(prompt).ToList();
        if (inputIds.Count == 0) return [];

        if (_tokenizer.BosTokenId >= 0)
            inputIds.Insert(0, _tokenizer.BosTokenId);

        const int maxNewTokens = 8;
        var generatedTokens = new List<long>();
        var tokenProbabilities = new List<double>();

        // The cache carries every token already scored, so each step after the first feeds
        // exactly one new token instead of re-running the whole sequence.
        var cache = plan.KvSlots.ToDictionary(slot => slot.PastName, EmptyCacheEntry, StringComparer.Ordinal);
        var step = inputIds.ToArray();
        var pastLength = 0;

        for (int i = 0; i < maxNewTokens; i++)
        {
            var inputs = new List<NamedOnnxValue>(3 + plan.KvSlots.Count)
            {
                NamedOnnxValue.CreateFromTensor(DecoderIoPlan.InputIdsInput,
                    new DenseTensor<long>(step, [1, step.Length]))
            };

            if (plan.NeedsAttentionMask)
            {
                var mask = DecoderIoPlan.AttentionMask(pastLength, step.Length);
                inputs.Add(NamedOnnxValue.CreateFromTensor(DecoderIoPlan.AttentionMaskInput,
                    new DenseTensor<long>(mask, [1, mask.Length])));
            }

            if (plan.NeedsPositionIds)
            {
                var positions = DecoderIoPlan.PositionIds(pastLength, step.Length);
                inputs.Add(NamedOnnxValue.CreateFromTensor(DecoderIoPlan.PositionIdsInput,
                    new DenseTensor<long>(positions, [1, positions.Length])));
            }

            foreach (var slot in plan.KvSlots)
                inputs.Add(cache[slot.PastName]);

            using var outputs = session.Run(inputs);
            var produced = outputs.ToDictionary(o => o.Name, StringComparer.Ordinal);

            var logits = produced[DecoderIoPlan.LogitsOutput].AsTensor<float>();
            var sequenceLength = logits.Dimensions[1];
            var vocabSize = logits.Dimensions[2];
            var lastTokenLogits = LastTokenLogits(
                logits is DenseTensor<float> dense ? dense.Buffer.Span : logits.ToArray(),
                sequenceLength, vocabSize);

            var softmaxProbs = Softmax(lastTokenLogits);

            var bestTokenId = 0L;
            var bestProb = 0.0;
            for (int j = 0; j < softmaxProbs.Length; j++)
            {
                if (softmaxProbs[j] > bestProb)
                {
                    bestProb = softmaxProbs[j];
                    bestTokenId = j;
                }
            }

            if (_tokenizer.EosTokenId >= 0 && bestTokenId == _tokenizer.EosTokenId) break;

            generatedTokens.Add(bestTokenId);
            tokenProbabilities.Add(bestProb);

            // present.N.* becomes the next step's past_key_values.N.*. The values are copied out
            // because `outputs` owns native memory that is released at the end of this iteration.
            pastLength += step.Length;
            cache = plan.KvSlots.ToDictionary(
                slot => slot.PastName,
                slot => CarryForward(slot, produced[slot.PresentName]),
                StringComparer.Ordinal);
            step = [bestTokenId];
        }

        if (generatedTokens.Count == 0) return [];

        var generatedText = _tokenizer.Decode(generatedTokens.ToArray());
        var avgProb = tokenProbabilities.Count > 0
            ? tokenProbabilities.Average()
            : 0.0;

        return PostProcess(generatedText, prefix, avgProb);
    }

    private static List<GraphTensor> Describe(IReadOnlyDictionary<string, NodeMetadata> metadata)
        => metadata.Select(entry => new GraphTensor(entry.Key, entry.Value.ElementDataType, entry.Value.Dimensions))
                   .ToList();

    // An empty cache: rank and static dimensions as the graph declares them, zero tokens deep.
    private static NamedOnnxValue EmptyCacheEntry(KvSlot slot) => slot.ElementType switch
    {
        TensorElementType.Float16 => NamedOnnxValue.CreateFromTensor(
            slot.PastName, new DenseTensor<Float16>(Array.Empty<Float16>(), slot.EmptyShape)),
        _ => NamedOnnxValue.CreateFromTensor(
            slot.PastName, new DenseTensor<float>(Array.Empty<float>(), slot.EmptyShape)),
    };

    private static NamedOnnxValue CarryForward(KvSlot slot, DisposableNamedOnnxValue present) => slot.ElementType switch
    {
        TensorElementType.Float16 => NamedOnnxValue.CreateFromTensor(slot.PastName, Copy<Float16>(present)),
        _ => NamedOnnxValue.CreateFromTensor(slot.PastName, Copy<float>(present)),
    };

    private static DenseTensor<T> Copy<T>(DisposableNamedOnnxValue value)
    {
        var tensor = value.AsTensor<T>();
        return new DenseTensor<T>(tensor.ToArray(), tensor.Dimensions.ToArray());
    }

    // logits arrive as [batch=1, sequence, vocab]; only the final position scores the next token.
    // vocabSize must come from the logits tensor itself — the model's output width is not the
    // tokenizer's vocab count (Qwen2.5 emits 151936 scores against a 151665-entry tokenizer).
    internal static float[] LastTokenLogits(ReadOnlySpan<float> logits, int sequenceLength, int vocabSize)
        => logits.Slice((sequenceLength - 1) * vocabSize, vocabSize).ToArray();

    private static double[] Softmax(float[] logits)
    {
        var result = new double[logits.Length];
        var maxLogit = logits.Max();
        double sum = 0.0;
        for (int i = 0; i < logits.Length; i++)
        {
            result[i] = Math.Exp(logits[i] - maxLogit);
            sum += result[i];
        }
        for (int i = 0; i < result.Length; i++)
            result[i] /= sum;
        return result;
    }

    /// <summary>Attempts to retrieve pending inference results, returning null if none are available.</summary>
    /// <returns>A list of <see cref="PredictionResult"/> or null.</returns>
    public IReadOnlyList<PredictionResult>? TryGetResults()
    {
        if (!IsAvailable) return null;
        lock (_lock)
        {
            if (_inferenceCount > 0) return null;
            var results = _pendingResults;
            if (results == null || results.IsEmpty) return null;
            _pendingResults = null;
            return results.ToList();
        }
    }

    /// <summary>Cancels any pending inference operation.</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _pendingCts?.Cancel();
            _pendingCts?.Dispose();
            _pendingCts = null;
            _pendingResults = null;
        }
    }

    private static string BuildPrompt(string context, string? prefix)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(context))
            sb.Append(context).Append(' ');
        if (!string.IsNullOrWhiteSpace(prefix))
            sb.Append(prefix);
        return sb.ToString().Trim();
    }

    internal static List<PredictionResult> PostProcess(string generatedText, string? prefix, double avgProb)
    {
        var results = new List<PredictionResult>();
        if (string.IsNullOrWhiteSpace(generatedText)) return results;

        var words = generatedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return results;

        var word = words[0];
        if (!string.IsNullOrEmpty(prefix) && !word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return results;
        results.Add(new PredictionResult(word, avgProb * 100.0, PredictionType.LMSuggestion, avgProb));

        return results;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();

        lock (_inferenceGate)
        {
            var deadline = Environment.TickCount64 + 5000;
            while (_inferenceCount > 0)
            {
                var remainingMs = deadline - Environment.TickCount64;
                if (remainingMs <= 0 || !Monitor.Wait(_inferenceGate, (int)remainingMs)) break;
            }
        }

        if (_inferenceCount == 0)
        {
            _session?.Dispose();
            _session = null;
            if (_ownsTokenizer) _tokenizer?.Dispose();
            return;
        }

        _logger?.LogWarning("SmallLMPredictor dispose timed out; deferring session disposal");
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_inferenceGate)
            {
                while (_inferenceCount > 0) Monitor.Wait(_inferenceGate);
            }
            lock (_lock)
            {
                _session?.Dispose();
                _session = null;
                if (_ownsTokenizer) _tokenizer?.Dispose();
            }
        });
    }
}
