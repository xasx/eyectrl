namespace EyeCtrl.Core.Prediction;

/// <summary>
/// Represents a word prediction with metadata for ranking and UI display.
/// Immutable record type following functional programming best practices.
/// </summary>
public record PredictionResult
{
    /// <summary>
    /// The predicted word or phrase.
    /// </summary>
    public string Word { get; init; } = string.Empty;

    /// <summary>
    /// The computed score used for ranking predictions.
    /// Higher scores indicate higher relevance.
    /// </summary>
    public double Score { get; init; }

    /// <summary>
    /// The type of prediction algorithm that generated this result.
    /// </summary>
    public PredictionType Type { get; init; }

    /// <summary>
    /// A confidence-like score used for UI feedback (e.g., background shading).
    /// Higher values indicate greater confidence or relevance.
    /// This value is not guaranteed to be normalized to the range 0.0 to 1.0;
    /// some scoring algorithms may produce values greater than 1.0.
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>Initializes a new instance of the <see cref="PredictionResult"/> record.</summary>
    /// <param name="word">The predicted word or phrase.</param>
    /// <param name="score">Relevance score used for ranking.</param>
    /// <param name="type">The prediction algorithm that produced this result.</param>
    /// <param name="confidence">Confidence indicator for UI feedback.</param>
    public PredictionResult(string word, double score, PredictionType type, double confidence)
    {
        Word = word;
        Score = score;
        Type = type;
        Confidence = confidence;
    }
}

/// <summary>
/// Categorizes the type of prediction algorithm used.
/// </summary>
public enum PredictionType
{
    /// <summary>
    /// Direct prefix match (e.g., "hel" → "hello").
    /// </summary>
    ExactPrefix,

    /// <summary>
    /// Keyboard-aware fuzzy match tolerating typos.
    /// </summary>
    FuzzyMatch,

    /// <summary>
    /// Bigram-based next word prediction (e.g., after "thank" → "you").
    /// </summary>
    NextWord,

    /// <summary>
    /// Multi-word phrase suggestion (e.g., "thank you").
    /// </summary>
    Phrase,

    /// <summary>
    /// Sentence completion prediction from the trie index.
    /// </summary>
    SentenceCompletion,

    /// <summary>
    /// Suggestion from the ONNX language model.
    /// </summary>
    LMSuggestion
}
