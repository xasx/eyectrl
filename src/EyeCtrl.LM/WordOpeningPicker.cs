namespace EyeCtrl.LM;

/// <summary>
/// The next-word request's first-step filter: the token that starts the answer must OPEN a word,
/// not continue the context's last one. With a bare context ("ich", no trailing space) the model's
/// most likely token is usually a word continuation ("/", "ou"), which is why an unfiltered request
/// produces no next word at all.
///
/// <para>
/// The picker scans a bounded window of the most likely tokens (the tail of a 150k-entry softmax is
/// irrelevant and decoding it would cost more than the answer is worth), and takes the best one
/// whose piece starts with whitespace. No such token in the window means "no next word", never a
/// continuation.
/// </para>
/// </summary>
internal static class WordOpeningPicker
{
    /// <summary>How many of the most likely tokens are checked for a word-opening piece.</summary>
    internal const int ScanWindow = 64;

    /// <summary>True when a decoded token piece starts a new word (a leading space).</summary>
    internal static bool StartsWord(string piece) => piece.Length > 0 && char.IsWhiteSpace(piece[0]);

    /// <summary>
    /// The most likely word-opening token id, or -1 when the scan window holds none. Ties keep the
    /// higher-probability token because the window is walked in descending probability.
    /// </summary>
    internal static long Pick(double[] probabilities, Func<long, string> pieceOf)
    {
        var window = TopTokenIds(probabilities, ScanWindow);
        foreach (var id in window)
        {
            if (StartsWord(pieceOf(id))) return id;
        }

        return -1;
    }

    /// <summary>The <paramref name="count"/> highest-probability token ids, descending.</summary>
    internal static List<long> TopTokenIds(double[] probabilities, int count)
    {
        var ids = new int[count];
        var values = new double[count];
        var filled = 0;
        for (int i = 0; i < probabilities.Length; i++)
        {
            var v = probabilities[i];
            if (filled == count && v <= values[count - 1]) continue;

            var pos = filled < count ? filled : count - 1;
            while (pos > 0 && values[pos - 1] < v)
            {
                values[pos] = values[pos - 1];
                ids[pos] = ids[pos - 1];
                pos--;
            }

            values[pos] = v;
            ids[pos] = i;
            if (filled < count) filled++;
        }

        var result = new List<long>(filled);
        for (int i = 0; i < filled; i++) result.Add(ids[i]);
        return result;
    }
}
