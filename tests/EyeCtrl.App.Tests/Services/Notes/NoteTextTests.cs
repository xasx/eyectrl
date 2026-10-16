namespace EyeCtrl.App.Tests.Services.Notes;

using EyeCtrl.App.Services.Notes;

/// <summary>
/// The pure string surgery behind the note edits (P1 D2/D4): whole text-element deletes, the
/// deleteWord rule mirroring the keyboard VM's TrimLastWord, the prediction snap, and the Done
/// trim.
/// </summary>
public class NoteTextTests
{
    // ── TrimBlankEdgeLines (P1 D4's Done trim) ──

    [Fact]
    public void Trim_RemovesLeadingAndTrailingWhitespaceOnlyLines() =>
        // Whole blank lines go; whitespace INSIDE a content line is content.
        Check.That(NoteText.TrimBlankEdgeLines("\n \n\thello\n  \n\n")).IsEqualTo("\thello");

    [Fact]
    public void Trim_KeepsInteriorBlankLines() =>
        Check.That(NoteText.TrimBlankEdgeLines("\n\na\n\nb\n\n")).IsEqualTo("a\n\nb");

    [Fact]
    public void Trim_KeepsInteriorLeadingWhitespaceOfContentLines() =>
        Check.That(NoteText.TrimBlankEdgeLines("\n  indented\n")).IsEqualTo("  indented");

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData(" \n \n ")]
    [InlineData("\t\t")]
    public void Trim_OfBlankOnlyText_IsEmpty(string text) =>
        Check.That(NoteText.TrimBlankEdgeLines(text)).IsEqualTo("");

    [Fact]
    public void Trim_LeavesPlainTextAlone() =>
        Check.That(NoteText.TrimBlankEdgeLines("hello")).IsEqualTo("hello");

    // ── DeleteLastTextElement (emoji-safe Backspace, P1 D2) ──

    [Fact]
    public void Backspace_AfterAnEmoji_RemovesTheWholeEmoji() =>
        Check.That(NoteText.DeleteLastTextElement("hi😀")).IsEqualTo("hi");

    [Fact]
    public void Backspace_AfterACombiningSequence_RemovesTheWholeElement() =>
        // "é" here is e + COMBINING ACUTE — one text element. Deleting x leaves it whole.
        Check.That(NoteText.DeleteLastTextElement("e\u0301x")).IsEqualTo("e\u0301");

    [Fact]
    public void Backspace_OnPlainText_RemovesOneChar() =>
        Check.That(NoteText.DeleteLastTextElement("abc")).IsEqualTo("ab");

    [Fact]
    public void Backspace_OnEmpty_IsEmpty() =>
        Check.That(NoteText.DeleteLastTextElement("")).IsEqualTo("");

    // ── TrimLastWord (deleteWord, the VM's rule) ──

    [Fact]
    public void DeleteWord_RemovesTheWordAndItsTrailingSeparator() =>
        Check.That(NoteText.TrimLastWord("thank you ")).IsEqualTo("thank ");

    [Fact]
    public void DeleteWord_StopsAtPunctuation_LikeMainstreamApps() =>
        Check.That(NoteText.TrimLastWord("well-known ")).IsEqualTo("well-");

    [Fact]
    public void DeleteWord_WithoutTrailingSeparator_RemovesTheWord() =>
        Check.That(NoteText.TrimLastWord("hello world")).IsEqualTo("hello ");

    [Fact]
    public void DeleteWord_OnSeparatorsOnly_IsEmpty() =>
        Check.That(NoteText.TrimLastWord("   ")).IsEqualTo("");

    [Fact]
    public void DeleteWord_OnEmpty_IsEmpty() =>
        Check.That(NoteText.TrimLastWord("")).IsEqualTo("");

    // ── DeleteUnitsSnapped (the prediction delete, P1 D2) ──

    [Fact]
    public void Snap_DeletesExactlyTheUnits_WhenTheyLandOnABoundary() =>
        Check.That(NoteText.DeleteUnitsSnapped("abcd", 2)).IsEqualTo("ab");

    [Fact]
    public void Snap_PullsBackToTheElementBoundary_WhenTheCountSplitsASurrogate() =>
        // "😀a" is 3 UTF-16 units; deleting 2 would cut inside the pair → snaps to 0 deleted.
        Check.That(NoteText.DeleteUnitsSnapped("\uD83D\uDE00a", 2)).IsEqualTo("");

    [Fact]
    public void Snap_DeletesWholePairs_WhenTheCountCoversThem() =>
        // "😀a" minus 3 units lands exactly after the 'a'.
        Check.That(NoteText.DeleteUnitsSnapped("\uD83D\uDE00a", 3)).IsEqualTo("");

    [Fact]
    public void Snap_PastTheTextLength_IsEmpty() =>
        Check.That(NoteText.DeleteUnitsSnapped("ab", 99)).IsEqualTo("");

    [Fact]
    public void Snap_SkinToneModifier_IsNeverSplit() =>
        // Fixup m3: 👋🏽 is base emoji + skin-tone modifier — FOUR UTF-16 units, ONE text element.
        // A count of 3 cuts inside the modifier pair; the boundary snap deletes the whole element
        // (the old surrogate-only step-back would have left a lone base emoji behind).
        Check.That(NoteText.DeleteUnitsSnapped("a👋🏽", 3)).IsEqualTo("a");

    [Fact]
    public void Snap_ZwjSequence_IsNeverSplit() =>
        // Fixup m3: 👩‍💻 is 👩 + ZWJ + 💻 — FIVE units, ONE element under the grapheme rules.
        // Deleting 2 counted units from a composing input must not strand 👩‍ half-built.
        Check.That(NoteText.DeleteUnitsSnapped("hi👩‍💻", 2)).IsEqualTo("hi");

    [Fact]
    public void Snap_ZeroUnits_DeletesNothing() =>
        Check.That(NoteText.DeleteUnitsSnapped("abc", 0)).IsEqualTo("abc");

    [Fact]
    public void Snap_OnEmpty_IsEmpty() =>
        Check.That(NoteText.DeleteUnitsSnapped("", 3)).IsEqualTo("");
}
