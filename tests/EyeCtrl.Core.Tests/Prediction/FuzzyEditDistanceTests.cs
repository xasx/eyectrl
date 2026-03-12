namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

public class FuzzyEditDistanceTests
{
    private static readonly Dictionary<char, HashSet<char>> QwertyMap =
        KeyboardAdjacencyMap.For("QWERTY");

    [Fact]
    public void Compute_IdenticalStrings_ReturnsZero()
    {
        Assert.Equal(0.0, FuzzyEditDistance.Compute("the", "the", QwertyMap));
    }

    [Fact]
    public void Compute_AdjacentKeySubstitution_ReturnHalfCost()
    {
        // 'r' is adjacent to 't' on QWERTY — substituting t→r costs 0.5
        var dist = FuzzyEditDistance.Compute("the", "rhe", QwertyMap);
        Assert.Equal(0.5, dist, precision: 5);
    }

    [Fact]
    public void Compute_NonAdjacentSubstitution_ReturnFullCost()
    {
        // 's' is not adjacent to 't'
        var dist = FuzzyEditDistance.Compute("the", "she", QwertyMap);
        Assert.Equal(1.0, dist, precision: 5);
    }

    [Fact]
    public void Compute_OneDeletion_ReturnsCostOne()
    {
        var dist = FuzzyEditDistance.Compute("have", "hav", QwertyMap);
        Assert.Equal(1.0, dist, precision: 5);
    }

    [Fact]
    public void Compute_OneInsertion_ReturnsCostOne()
    {
        var dist = FuzzyEditDistance.Compute("hav", "have", QwertyMap);
        Assert.Equal(1.0, dist, precision: 5);
    }

    [Fact]
    public void Compute_EmptyStrings_ReturnsZero()
    {
        Assert.Equal(0.0, FuzzyEditDistance.Compute("", "", QwertyMap));
    }

    [Fact]
    public void Compute_EmptyVsWord_ReturnsWordLength()
    {
        Assert.Equal(3.0, FuzzyEditDistance.Compute("", "the", QwertyMap), precision: 5);
    }

    [Fact]
    public void Compute_TwoAdjacentSubstitutions_ReturnsCostOne()
    {
        // t→r (0.5), e→e (0), n→m (0.5) = 1.0
        var dist = FuzzyEditDistance.Compute("ten", "rem", QwertyMap);
        Assert.Equal(1.0, dist, precision: 5);
    }
}
