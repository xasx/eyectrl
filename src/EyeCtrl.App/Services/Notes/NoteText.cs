namespace EyeCtrl.App.Services.Notes;

using System.Globalization;

/// <summary>
/// The pure string surgery the note editor applies (P1 D2/D4): whole text-element deletes so an
/// emoji never leaves a lone surrogate, word deletion mirroring the keyboard's own
/// <c>KeyboardViewModel.TrimLastWord</c>, and the Done trim. No WPF, no state.
/// </summary>
public static class NoteText
{
    /// <summary>
    /// Removes leading and trailing whitespace-only lines (P1 D4). Interior blank lines and all
    /// interior content are kept byte-for-byte; a text of nothing but blank lines is empty.
    /// </summary>
    public static string TrimBlankEdgeLines(string text)
    {
        var lines = text.Split('\n');
        int first = 0;
        while (first < lines.Length && string.IsNullOrWhiteSpace(lines[first])) first++;
        if (first == lines.Length) return string.Empty;
        int last = lines.Length - 1;
        while (last > first && string.IsNullOrWhiteSpace(lines[last])) last--;
        return string.Join('\n', lines[first..(last + 1)]);
    }

    /// <summary>
    /// Removes the final text element (a surrogate pair included), so Backspace after an emoji
    /// removes the whole emoji (P1 D2). An empty text stays empty.
    /// </summary>
    public static string DeleteLastTextElement(string text)
    {
        var info = new StringInfo(text);
        if (info.LengthInTextElements <= 0) return string.Empty;
        return info.SubstringByTextElements(0, info.LengthInTextElements - 1);
    }

    /// <summary>
    /// Deletes the final word and the separators after it ("thank you " → "thank "), a word being
    /// a run of letters or digits. Byte-for-byte the same rule as the keyboard VM's private
    /// <c>TrimLastWord</c> — the bookkeeping must agree with the deleteWord keystroke it mirrors
    /// (mainstream apps' Ctrl+Backspace stop at hyphens and other punctuation).
    /// </summary>
    public static string TrimLastWord(string text)
    {
        var end = text.Length;
        while (end > 0 && !char.IsLetterOrDigit(text[end - 1])) end--;
        var start = end;
        while (start > 0 && char.IsLetterOrDigit(text[start - 1])) start--;
        return text[..start];
    }

    /// <summary>
    /// Deletes up to <paramref name="units"/> UTF-16 units, then snaps the cut BACK to a
    /// text-element boundary (P1 D2): the VM counted <c>CurrentInput.Length</c>, which can split
    /// a surrogate pair the note holds. Snapping back never deletes more than the count — the
    /// extra half of a pair is already inside the counted units, so the boundary sits at or before
    /// the raw cut.
    /// </summary>
    public static string DeleteUnitsSnapped(string text, int units)
    {
        if (units <= 0 || text.Length == 0) return text;
        var cut = Math.Max(0, text.Length - units);
        while (cut > 0 && char.IsLowSurrogate(text[cut])) cut--;
        return text[..cut];
    }
}
