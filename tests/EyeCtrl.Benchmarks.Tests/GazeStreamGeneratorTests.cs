namespace EyeCtrl.Benchmarks.Tests;

using EyeCtrl.Core.Models;

public class GazeStreamGeneratorTests
{
    [Fact]
    public void GenerateFixation_ReturnsCorrectCount()
    {
        var points = GazeStreamGenerator.GenerateFixation();
        Check.That(points).HasSize(GazeStreamGenerator.SamplesPerBatch);
    }

    [Fact]
    public void GenerateSaccade_ReturnsCorrectCount()
    {
        var points = GazeStreamGenerator.GenerateSaccade();
        Check.That(points).HasSize(GazeStreamGenerator.SamplesPerBatch);
    }

    [Fact]
    public void GenerateNoisyFixation_ReturnsCorrectCount()
    {
        var points = GazeStreamGenerator.GenerateNoisyFixation();
        Check.That(points).HasSize(GazeStreamGenerator.SamplesPerBatch);
    }

    [Fact]
    public void GenerateWithDropout_ReturnsCorrectCount()
    {
        var points = GazeStreamGenerator.GenerateWithDropout();
        Check.That(points).HasSize(GazeStreamGenerator.SamplesPerBatch);
    }

    [Theory]
    [InlineData("Fixation")]
    [InlineData("Saccade")]
    [InlineData("NoisyFixation")]
    [InlineData("WithDropout")]
    public void Timestamps_AreStrictlyIncreasingBySamplePeriod(string scenario)
    {
        var points = Generate(scenario);
        for (int i = 1; i < points.Length; i++)
        {
            long delta = points[i].TimestampMicroseconds - points[i - 1].TimestampMicroseconds;
            Check.That(delta).IsEqualTo(GazeStreamGenerator.SamplePeriodMicros);
        }
    }

    [Theory]
    [InlineData("Fixation", 99)]
    [InlineData("Saccade", 99)]
    [InlineData("NoisyFixation", 99)]
    [InlineData("WithDropout", 99)]
    public void SameSeed_ProducesIdenticalStreams(string scenario, int seed)
    {
        var first = Generate(scenario, seed);
        var second = Generate(scenario, seed);
        var defaultSeedStream = Generate(scenario, GazeStreamGenerator.DefaultSeed);
        Check.That(second.Length).IsEqualTo(first.Length);
        bool differsFromDefault = false;
        for (int i = 0; i < first.Length; i++)
        {
            Check.That(second[i].X).IsEqualTo(first[i].X);
            Check.That(second[i].Y).IsEqualTo(first[i].Y);
            Check.That(second[i].IsValid).IsEqualTo(first[i].IsValid);
            if (first[i].X != defaultSeedStream[i].X || first[i].Y != defaultSeedStream[i].Y)
                differsFromDefault = true;
        }
        Check.That(differsFromDefault).IsTrue();
    }

    [Fact]
    public void GenerateWithDropout_HasBothValidAndInvalidSamples()
    {
        var points = GazeStreamGenerator.GenerateWithDropout();
        Check.That(points).HasElementThatMatches(p => p.IsValid);
        Check.That(points).HasElementThatMatches(p => !p.IsValid);
    }

    [Fact]
    public void GenerateWithDropout_HasClusteredInvalids_WithAtLeastOneLongGap()
    {
        var points = GazeStreamGenerator.GenerateWithDropout();
        int longestRun = 0;
        int currentRun = 0;
        foreach (var p in points)
        {
            if (!p.IsValid)
            {
                currentRun++;
                longestRun = Math.Max(longestRun, currentRun);
            }
            else
            {
                currentRun = 0;
            }
        }
        Check.That(longestRun).IsGreaterOrEqualThan(6);
    }

    [Theory]
    [InlineData("Fixation")]
    [InlineData("Saccade")]
    [InlineData("NoisyFixation")]
    [InlineData("WithDropout")]
    public void Coordinates_AreWithinPlausibleScreenBox(string scenario)
    {
        var points = Generate(scenario);
        foreach (var p in points)
        {
            Check.That(p.X).IsStrictlyGreaterThan(-1000.0);
            Check.That(p.X).IsStrictlyLessThan(3000.0);
            Check.That(p.Y).IsStrictlyGreaterThan(-1000.0);
            Check.That(p.Y).IsStrictlyLessThan(3000.0);
            Check.That(double.IsFinite(p.X)).IsTrue();
            Check.That(double.IsFinite(p.Y)).IsTrue();
        }
    }

    [Theory]
    [InlineData("Fixation", 2500)]
    [InlineData("Saccade", 2500)]
    [InlineData("Saccade", 2818)]
    [InlineData("NoisyFixation", 2500)]
    public void Generate_WithExplicitLength_ReturnsExactCount(string scenario, int length)
    {
        var points = Generate(scenario, length: length);
        Check.That(points).HasSize(length);
    }

    [Theory]
    [InlineData("Fixation", 2500)]
    [InlineData("Saccade", 2500)]
    [InlineData("Saccade", 2818)]
    [InlineData("NoisyFixation", 2500)]
    public void Generate_WithExplicitLength_HasContinuousTimestamps(string scenario, int length)
    {
        var points = Generate(scenario, length: length);
        for (int i = 0; i < points.Length; i++)
        {
            Check.That(points[i].TimestampMicroseconds).IsEqualTo(i * GazeStreamGenerator.SamplePeriodMicros);
        }
    }

    private static GazePoint[] Generate(string scenario, int seed = 42, int? length = null) => scenario switch
    {
        "Fixation" => length is null
            ? GazeStreamGenerator.GenerateFixation(seed)
            : GazeStreamGenerator.GenerateFixation(seed, length.Value),
        "Saccade" => length is null
            ? GazeStreamGenerator.GenerateSaccade(seed)
            : GazeStreamGenerator.GenerateSaccade(seed, length.Value),
        "NoisyFixation" => length is null
            ? GazeStreamGenerator.GenerateNoisyFixation(seed)
            : GazeStreamGenerator.GenerateNoisyFixation(seed, length.Value),
        "WithDropout" => GazeStreamGenerator.GenerateWithDropout(seed),
        _ => throw new ArgumentException($"Unknown scenario: {scenario}", nameof(scenario)),
    };
}
