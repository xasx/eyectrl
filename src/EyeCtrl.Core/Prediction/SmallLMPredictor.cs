namespace EyeCtrl.Core.Prediction;

using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Collections.Concurrent;
using System.Text;

public class SmallLMPredictor : IDisposable
{
    private readonly string _modelsDirectory;
    private readonly string? _selectedModelSize;
    private readonly bool _preferGpu;
    private readonly ILogger<SmallLMPredictor>? _logger;

    private InferenceSession? _session;
    private readonly object _lock = new();
    private volatile bool _disposed;
    private CancellationTokenSource? _pendingCts;
    private volatile bool _isRunning;
    private ConcurrentBag<PredictionResult>? _pendingResults;

    public bool IsAvailable { get; private set; }
    public string? SelectedModelPath { get; private set; }

    private static readonly Dictionary<string, string> ModelFileNames = new()
    {
        ["135M"] = "smollm2-135m.onnx",
        ["360M"] = "smollm2-360m.onnx",
        ["1.5B"] = "qwen2.5-1.5b.onnx",
    };

    public SmallLMPredictor(string modelsDirectory, string? modelSize, bool preferGpu, ILogger<SmallLMPredictor>? logger = null)
    {
        _modelsDirectory = modelsDirectory;
        _selectedModelSize = modelSize;
        _preferGpu = preferGpu;
        _logger = logger;

        if (modelSize == "Off" || string.IsNullOrEmpty(modelSize))
        {
            IsAvailable = false;
            return;
        }

        Initialize();
    }

    private void Initialize()
    {
        if (!ModelFileNames.TryGetValue(_selectedModelSize!, out var fileName))
        {
            _logger?.LogWarning("Unknown model size: {Size}", _selectedModelSize);
            IsAvailable = false;
            return;
        }

        SelectedModelPath = Path.Combine(_modelsDirectory, fileName);
        if (!File.Exists(SelectedModelPath))
        {
            _logger?.LogInformation("Model file not found: {Path}", SelectedModelPath);
            IsAvailable = false;
            return;
        }

        try
        {
            var options = new SessionOptions();

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
                    options = new SessionOptions();
                }
            }

            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

            _session = new InferenceSession(SelectedModelPath, options);
            IsAvailable = true;
            _logger?.LogInformation("SmallLMPredictor initialized with {Model}", _selectedModelSize);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize ONNX session for {Model}", _selectedModelSize);
            IsAvailable = false;
        }
    }

    public void Predict(string context, string? prefix = null)
    {
        if (!IsAvailable || _session == null) return;

        lock (_lock)
        {
            _pendingCts?.Cancel();
            _pendingCts?.Dispose();
            _pendingCts = new CancellationTokenSource();
            _pendingResults = new ConcurrentBag<PredictionResult>();

            var token = _pendingCts.Token;
            var session = _session;
            var capturePrefix = prefix;
            var captureContext = context;

            _isRunning = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (token.IsCancellationRequested) return;

                    var results = RunInference(session, captureContext, capturePrefix);
                    if (token.IsCancellationRequested) return;

                    foreach (var result in results)
                    {
                        if (token.IsCancellationRequested) break;
                        var bag = _pendingResults;
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
                    _isRunning = false;
                }
            });
        }
    }

    private List<PredictionResult> RunInference(InferenceSession session, string context, string? prefix)
    {
        var prompt = BuildPrompt(context, prefix);
        var inputIds = Tokenize(prompt);

        if (inputIds.Length == 0) return [];

        const int maxNewTokens = 8;
        var generatedTokens = new List<long>();
        var pastInputIds = inputIds.ToList();

        for (int i = 0; i < maxNewTokens; i++)
        {
            var inputTensor = new DenseTensor<long>(pastInputIds.ToArray(), [1, pastInputIds.Count]);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputTensor)
            };

            using var outputs = session.Run(inputs);
            var logits = outputs.First().AsTensor<float>();

            var vocabSize = logits.Dimensions[^1];
            var lastTokenLogits = new float[vocabSize];
            var offset = (pastInputIds.Count - 1) * vocabSize;
            for (int j = 0; j < vocabSize; j++)
                lastTokenLogits[j] = logits.GetValue(offset + j);

            var bestTokenId = 0L;
            var bestLogit = float.MinValue;
            for (int j = 0; j < lastTokenLogits.Length; j++)
            {
                if (lastTokenLogits[j] > bestLogit)
                {
                    bestLogit = lastTokenLogits[j];
                    bestTokenId = j;
                }
            }

            if (bestTokenId == 0) break;

            generatedTokens.Add(bestTokenId);
            pastInputIds.Add(bestTokenId);
        }

        var generatedText = Detokenize(generatedTokens);
        return PostProcess(generatedText, prefix);
    }

    public IReadOnlyList<PredictionResult>? TryGetResults()
    {
        if (!IsAvailable) return null;
        if (_isRunning) return null;
        lock (_lock)
        {
            var results = _pendingResults;
            if (results == null || results.IsEmpty) return null;
            _pendingResults = null;
            return results.ToList();
        }
    }

    public void Cancel()
    {
        lock (_lock)
        {
            _pendingCts?.Cancel();
            _pendingCts?.Dispose();
            _pendingCts = null;
            _pendingResults = null;
            _isRunning = false;
        }
    }

    private static readonly Dictionary<string, long> StubVocab = new(StringComparer.OrdinalIgnoreCase)
    {
        ["the"] = 1, ["be"] = 2, ["to"] = 3, ["of"] = 4, ["and"] = 5,
        ["a"] = 6, ["in"] = 7, ["that"] = 8, ["have"] = 9, ["i"] = 10,
        ["it"] = 11, ["for"] = 12, ["not"] = 13, ["on"] = 14, ["with"] = 15,
        ["he"] = 16, ["as"] = 17, ["you"] = 18, ["do"] = 19, ["at"] = 20,
    };

    private static long[] Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var words = text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ids = new List<long>();
        foreach (var word in words)
        {
            var cleanWord = word.TrimEnd('.', ',', '!', '?', ';', ':');
            if (StubVocab.TryGetValue(cleanWord, out var id))
                ids.Add(id);
            else
                ids.Add(StubVocab.Count + 1);
        }
        return ids.ToArray();
    }

    private static string Detokenize(List<long> tokenIds)
    {
        var reverseVocab = StubVocab.ToDictionary(kv => kv.Value, kv => kv.Key);
        var words = tokenIds
            .Select(id => reverseVocab.TryGetValue(id, out var word) ? word : "<unk>")
            .ToList();
        return string.Join(" ", words);
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

    private List<PredictionResult> PostProcess(string generatedText, string? prefix)
    {
        var results = new List<PredictionResult>();
        if (string.IsNullOrWhiteSpace(generatedText)) return results;

        var words = generatedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            if (!string.IsNullOrEmpty(prefix) && !word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            // TODO: Use actual softmax-normalized logit values when a real tokenizer
            // with log-probabilities is available instead of the hardcoded score.
            results.Add(new PredictionResult(word, 10.0, PredictionType.LMSuggestion, 0.5));
        }

        return results;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();

        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (_isRunning && DateTime.UtcNow < timeout)
            Thread.Sleep(10);

        _session?.Dispose();
        _session = null;
    }
}
