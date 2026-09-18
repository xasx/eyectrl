namespace EyeCtrl.Core.Prediction;

/// <summary>
/// An immutable snapshot of everything a <see cref="WordPredictor"/> has learned: word
/// frequencies, bigrams (with timestamps), phrases (with timestamps), and casing preferences.
/// <para>
/// Captured cheaply on the UI thread by the background prediction-stack rebuild; the snapshot
/// taken BEFORE a build starts is diffed against the state at swap time, and the delta is
/// applied to the new stack — so learning that happened on the old stack during the
/// multi-second build window (words, bigrams, phrases, casing) is carried over instead of
/// being silently destroyed when the new stack's next save overwrites the files.
/// </para>
/// </summary>
public sealed class PredictionLearnedState
{
    /// <summary>Word → frequency (base + learned, one map).</summary>
    public required IReadOnlyDictionary<string, int> Words { get; init; }

    /// <summary>Context word → (next word → frequency).</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Bigrams { get; init; }

    /// <summary>Context word → (next word → last-seen timestamp).</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, DateTimeOffset>> BigramTimestamps { get; init; }

    /// <summary>Phrase prefix → (full phrase → frequency).</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Phrases { get; init; }

    /// <summary>Phrase prefix → (full phrase → last-seen timestamp).</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, DateTimeOffset>> PhraseTimestamps { get; init; }

    /// <summary>Lowercased word → (casing variant → frequency).</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Casing { get; init; }
}
