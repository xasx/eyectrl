namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

public class FuzzyEditDistanceTests
{
    private static readonly Dictionary<char, HashSet<char>> QwertyMap =
        KeyboardAdjacencyMap.For("QWERTY");

    [Fact]
    public void Compute_IdenticalStrings_ReturnsZero()
    {
        Check.That(FuzzyEditDistance.Compute("the", "the", QwertyMap)).IsEqualTo(0.0);
    }

    [Fact]
    public void Compute_AdjacentKeySubstitution_ReturnHalfCost()
    {
        // 'r' is adjacent to 't' on QWERTY — substituting t→r costs 0.5
        var dist = FuzzyEditDistance.Compute("the", "rhe", QwertyMap);
        Check.That(dist).IsCloseTo(0.5, 1e-5);
    }

    [Fact]
    public void Compute_NonAdjacentSubstitution_ReturnFullCost()
    {
        // 's' is not adjacent to 't'
        var dist = FuzzyEditDistance.Compute("the", "she", QwertyMap);
        Check.That(dist).IsCloseTo(1.0, 1e-5);
    }

    [Fact]
    public void Compute_OneDeletion_ReturnsCostOne()
    {
        var dist = FuzzyEditDistance.Compute("have", "hav", QwertyMap);
        Check.That(dist).IsCloseTo(1.0, 1e-5);
    }

    [Fact]
    public void Compute_OneInsertion_ReturnsCostOne()
    {
        var dist = FuzzyEditDistance.Compute("hav", "have", QwertyMap);
        Check.That(dist).IsCloseTo(1.0, 1e-5);
    }

    [Fact]
    public void Compute_EmptyStrings_ReturnsZero()
    {
        Check.That(FuzzyEditDistance.Compute("", "", QwertyMap)).IsEqualTo(0.0);
    }

    [Fact]
    public void Compute_EmptyVsWord_ReturnsWordLength()
    {
        Check.That(FuzzyEditDistance.Compute("", "the", QwertyMap)).IsCloseTo(3.0, 1E-05);
    }

    [Fact]
    public void Compute_TwoAdjacentSubstitutions_ReturnsCostOne()
    {
        // t→r (0.5), e→e (0), n→m (0.5) = 1.0
        var dist = FuzzyEditDistance.Compute("ten", "rem", QwertyMap);
        Check.That(dist).IsCloseTo(1.0, 1e-5);
    }
}
