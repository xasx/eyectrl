namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

/// <summary>
/// Pins the non-ASCII adjacency entries the letters-page edit (keyboard-one-look T6) added, plus
/// the ASCII sanity corner the map has always rested on. The maps are hand-maintained tables, so
/// a lost entry — deleting <c>ß</c> while reordering the QWERTZ map, say — is invisible to the
/// build and silently raises the typo cost back to 1.0 in <see cref="FuzzyEditDistance"/>.
/// </summary>
public class KeyboardAdjacencyMapTests
{
    [Fact]
    public void Qwertz_SharpS_IsAdjacentToAumlaut()
    {
        // ß took the third row's right end (variant A): ⌫ left, ↵ below, ä's box above-left.
        var map = KeyboardAdjacencyMap.For("QWERTZ");
        Check.That(KeyboardAdjacencyMap.AreAdjacent(map, 'ß', 'ä')).IsTrue();
        // Not adjacent to anything else on that row or the row above: ü is two keys away
        // (⌫ sits between), and the footer keys (/, ., Space, ↵) are not letters.
        Check.That(KeyboardAdjacencyMap.AreAdjacent(map, 'ß', 'ü')).IsFalse();
        Check.That(KeyboardAdjacencyMap.AreAdjacent(map, 'ß', 'p')).IsFalse();
    }

    [Fact]
    public void Azerty_Cedilla_IsAdjacentToM_BothWays()
    {
        // ç took the third row's right end: its box overlaps m's span above. Unlike ß→ä, both
        // sides are in the map (m is ASCII), so the relation must be pinned symmetric.
        var map = KeyboardAdjacencyMap.For("AZERTY");
        Check.That(KeyboardAdjacencyMap.AreAdjacent(map, 'ç', 'm')).IsTrue();
        Check.That(KeyboardAdjacencyMap.AreAdjacent(map, 'm', 'ç')).IsTrue();
        Check.That(KeyboardAdjacencyMap.AreAdjacent(map, 'ç', 'l')).IsFalse();
        Check.That(KeyboardAdjacencyMap.AreAdjacent(map, 'ç', 'p')).IsFalse();
    }

    [Fact]
    public void Qwerty_Unchanged_ByTheLettersPageEdit()
    {
        // QWERTY's letters never move (only DelWd/Win/Tab/footer keys do), so its map gains
        // nothing: the only neighbour added anywhere is on QWERTZ/AZERTY.
        var map = KeyboardAdjacencyMap.For("QWERTY");
        Check.That(map.ContainsKey('ß')).IsFalse();
        Check.That(map.ContainsKey('ç')).IsFalse();
        // The r→t corner the FuzzyEditDistance tests lean on stays intact.
        Check.That(KeyboardAdjacencyMap.AreAdjacent(map, 'r', 't')).IsTrue();
    }
}
