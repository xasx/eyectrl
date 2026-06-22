// src/EyeCtrl.Core/Prediction/ILanguageModelPredictor.cs
namespace EyeCtrl.Core.Prediction;

/// <summary>
/// Predicts text completions using a language model (e.g., ONNX neural LM).
/// Implementations are optional — the orchestrator functions correctly when
/// the predictor is null or <see cref="IsAvailable"/> is false.
/// </summary>
public interface ILanguageModelPredictor : IDisposable
{
    /// <summary>
    /// Whether the underlying model is loaded and ready to produce predictions.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Begins an asynchronous prediction for the given context and prefix.
    /// Results are retrieved later via <see cref="TryGetResults"/>.
    /// </summary>
    /// <param name="context">The text preceding the prefix (may be null or empty).</param>
    /// <param name="prefix">The text to complete (may be null or empty).</param>
    void Predict(string context, string? prefix = null);

    /// <summary>
    /// Returns completed predictions from the most recent <see cref="Predict"/> call,
    /// or null if the inference is still in progress.
    /// </summary>
    IReadOnlyList<PredictionResult>? TryGetResults();

    /// <summary>
    /// Cancels any pending inference and releases resources.
    /// </summary>
    void Cancel();
}
