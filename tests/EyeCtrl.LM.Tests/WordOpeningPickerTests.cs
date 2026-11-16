namespace EyeCtrl.LM.Tests;

using EyeCtrl.LM;

/// <summary>
/// The next-word request's first-step filter. Without it the model continues the context's last
/// word ("ich" → "/", "ou"), which is exactly the class that made a boundary LM useless.
/// </summary>
public class WordOpeningPickerTests
{
    private static double[] Probs(params (int Id, double P)[] entries)
    {
        var probs = new double[200];
        foreach (var (id, p) in entries) probs[id] = p;
        return probs;
    }

    [Fact]
    public void Pick_ChoosesTheBestWordOpeningToken_EvenWhenAContinuationRanksHigher()
    {
        // A word continuation (no leading space) outscores every real word: the picker must skip it.
        var probs = Probs((5, 0.5), (7, 0.2), (9, 0.1));

        var picked = WordOpeningPicker.Pick(probs, id => id switch
        {
            5 => "ou",
            7 => " Wetter",
            9 => " und",
            _ => "",
        });

        Check.That(picked).IsEqualTo(7L);
    }

    [Fact]
    public void Pick_ReturnsMinusOne_WhenNothingInTheWindowOpensAWord()
    {
        var probs = Probs((5, 0.5), (6, 0.3));

        var picked = WordOpeningPicker.Pick(probs, _ => "ou");

        Check.That(picked).IsEqualTo(-1L);
    }

    [Fact]
    public void Pick_IgnoresAWordOpeningTokenOutsideTheScanWindow()
    {
        // One very likely continuation, then enough low-probability filler to push the single
        // word-opening token past the window: it must not be found.
        var entries = new List<(int, double)> { (5, 0.9) };
        for (var i = 0; i < WordOpeningPicker.ScanWindow + 5; i++)
            entries.Add((100 + i, 0.001));
        entries.Add((3, 1e-9));   // a word-opening token, but far below the window

        var picked = WordOpeningPicker.Pick(Probs(entries.ToArray()), id => id == 3 ? " Wort" : "ou");

        Check.That(picked).IsEqualTo(-1L);
    }

    [Fact]
    public void StartsWord_IsTheLeadingSpace()
    {
        Check.That(WordOpeningPicker.StartsWord(" Wort")).IsTrue();
        Check.That(WordOpeningPicker.StartsWord("wort")).IsFalse();
        Check.That(WordOpeningPicker.StartsWord("")).IsFalse();
    }

    [Fact]
    public void TopTokenIds_AreDescendingAndBounded()
    {
        var probs = Probs((1, 0.1), (2, 0.4), (3, 0.3), (4, 0.2));

        var top = WordOpeningPicker.TopTokenIds(probs, 3);

        Check.That(top).ContainsExactly(2L, 3L, 4L);
    }
}
