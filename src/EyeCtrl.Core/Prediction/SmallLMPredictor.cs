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
    private readonly LMTokenizer? _tokenizer;

    private InferenceSession? _session;
    private readonly object _lock = new();
    private volatile bool _disposed;
    private CancellationTokenSource? _pendingCts;
    private volatile int _inferenceCount;
    private ConcurrentBag<PredictionResult>? _pendingResults;

    public bool IsAvailable { get; private set; }
    public string? SelectedModelPath { get; private set; }

    public SmallLMPredictor(string modelsDirectory, string? modelSize, bool preferGpu, ILogger<SmallLMPredictor>? logger = null, LMTokenizer? tokenizer = null)
    {
        _modelsDirectory = modelsDirectory;
        _selectedModelSize = modelSize;
        _preferGpu = preferGpu;
        _logger = logger;
        _tokenizer = tokenizer;

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

        Initialize();
    }

    private void Initialize()
    {
        if (!ModelConstants.ModelFileNames.TryGetValue(_selectedModelSize!, out var fileName))
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
        if (_disposed || !IsAvailable || _session == null) return;

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
                    Interlocked.Decrement(ref _inferenceCount);
                }
            });
        }
    }

    private List<PredictionResult> RunInference(InferenceSession session, string context, string? prefix)
    {
        var prompt = BuildPrompt(context, prefix);
        var inputIds = _tokenizer!.Encode(prompt).ToList();
        if (inputIds.Count == 0) return [];

        if (_tokenizer.BosTokenId >= 0)
            inputIds.Insert(0, _tokenizer.BosTokenId);

        const int maxNewTokens = 8;
        var generatedTokens = new List<long>();
        var tokenProbabilities = new List<double>();
        var pastInputIds = inputIds.ToList();
        var vocabSize = _tokenizer.VocabSize;

        for (int i = 0; i < maxNewTokens; i++)
        {
            var inputTensor = new DenseTensor<long>(pastInputIds.ToArray(), [1, pastInputIds.Count]);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputTensor)
            };

            try
            {
                using var outputs = session.Run(inputs);
                var logits = outputs.First().AsTensor<float>();

                var lastTokenLogits = new float[vocabSize];
                var offset = (pastInputIds.Count - 1) * vocabSize;
                for (int j = 0; j < vocabSize; j++)
                    lastTokenLogits[j] = logits.GetValue(offset + j);

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
                pastInputIds.Add(bestTokenId);
            }
            finally
            {
                foreach (var input in inputs)
                {
                    (input as IDisposable)?.Dispose();
                }
            }
        }

        if (generatedTokens.Count == 0) return [];

        var generatedText = _tokenizer.Decode(generatedTokens.ToArray());
        var avgProb = tokenProbabilities.Count > 0
            ? tokenProbabilities.Average()
            : 0.0;

        return PostProcess(generatedText, prefix, avgProb);
    }

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

    private List<PredictionResult> PostProcess(string generatedText, string? prefix, double avgProb)
    {
        var results = new List<PredictionResult>();
        if (string.IsNullOrWhiteSpace(generatedText)) return results;

        var words = generatedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            if (!string.IsNullOrEmpty(prefix) && !word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            results.Add(new PredictionResult(word, avgProb * 100.0, PredictionType.LMSuggestion, avgProb));
        }

        return results;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();

        var start = DateTime.UtcNow;
        while (_inferenceCount > 0 && (DateTime.UtcNow - start) < TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(10);
        }

        if (_inferenceCount == 0)
        {
            _session?.Dispose();
            _session = null;
            _tokenizer?.Dispose();
            return;
        }

        _logger?.LogWarning("SmallLMPredictor dispose timed out while inference was still running; deferring session disposal");
        ThreadPool.QueueUserWorkItem(_ =>
        {
            while (Interlocked.CompareExchange(ref _inferenceCount, 0, 0) > 0)
            {
                Thread.Sleep(10);
            }

            lock (_lock)
            {
                _session?.Dispose();
                _session = null;
                _tokenizer?.Dispose();
            }
        });
    }
}
