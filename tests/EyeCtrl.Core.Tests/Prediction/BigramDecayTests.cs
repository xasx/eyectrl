namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

public class BigramDecayTests
{
    [Fact]
    public void ApplyDecay_WithDecayDisabled_DoesNothing()
    {
        // Arrange
        var model = new BigramModel();
        model.Learn("hello", "world");
        model.Learn("hello", "world");

        // Act
        model.ApplyDecay(decayEnabled: false, halfLifeDays: 30);

        // Assert
        var nextWords = model.GetNextWords("hello", 10);
        Check.That(nextWords).HasSize(1);
        Check.That(nextWords[0].frequency).IsEqualTo(2);
    }

    [Fact]
    public void ApplyDecay_WithRecentEntry_DoesNotDecay()
    {
        // Arrange
        var model = new BigramModel();
        model.Learn("hello", "world");
        model.Learn("hello", "world");

        // Act - Apply decay immediately (0 days elapsed)
        model.ApplyDecay(decayEnabled: true, halfLifeDays: 30);

        // Assert - Frequency should remain unchanged
        var nextWords = model.GetNextWords("hello", 10);
        Check.That(nextWords).HasSize(1);
        Check.That(nextWords[0].frequency).IsEqualTo(2);
    }

    [Fact]
    public void ApplyDecay_WithOldEntry_ReducesFrequency()
    {
        // Arrange
        var model = new BigramModel();
        // Use internal test method to set timestamp to 60 days ago
        model.LearnWithTimestamp("hello", "world", DateTimeOffset.UtcNow.AddDays(-60));
        model.LearnWithTimestamp("hello", "world", DateTimeOffset.UtcNow.AddDays(-60));

        // Act - Apply decay with 30-day half-life
        // After 60 days (2 half-lives), frequency should decay: 2 * 0.5^2 = 0.5
        model.ApplyDecay(decayEnabled: true, halfLifeDays: 30);

        // Assert - Entry should be removed (frequency < MinFrequency threshold of 2)
        var nextWords = model.GetNextWords("hello", 10);
        Check.That(nextWords).IsEmpty();
    }

    [Fact]
    public void ApplyDecay_WithMixedAges_DecaysProportionally()
    {
        // Arrange
        var model = new BigramModel();

        // Recent entry (should not decay)
        model.LearnWithTimestamp("hello", "world", DateTimeOffset.UtcNow);
        model.LearnWithTimestamp("hello", "world", DateTimeOffset.UtcNow);
        model.LearnWithTimestamp("hello", "world", DateTimeOffset.UtcNow);
        model.LearnWithTimestamp("hello", "world", DateTimeOffset.UtcNow);

        // Old entry (should decay significantly)
        model.LearnWithTimestamp("hello", "there", DateTimeOffset.UtcNow.AddDays(-90));
        model.LearnWithTimestamp("hello", "there", DateTimeOffset.UtcNow.AddDays(-90));
        model.LearnWithTimestamp("hello", "there", DateTimeOffset.UtcNow.AddDays(-90));
        model.LearnWithTimestamp("hello", "there", DateTimeOffset.UtcNow.AddDays(-90));

        // Act - Apply decay with 30-day half-life
        model.ApplyDecay(decayEnabled: true, halfLifeDays: 30);

        // Assert
        var nextWords = model.GetNextWords("hello", 10);

        // "world" should still be present with frequency 4
        var worldEntry = nextWords.FirstOrDefault(w => w.word == "world");
        Check.That(worldEntry.word).IsNotNull(); // Ensure we found the entry
        Check.That(worldEntry.word).IsEqualTo("world");
        Check.That(worldEntry.frequency).IsEqualTo(4);

        // "there" should be removed (4 * 0.5^3 = 0.5, below threshold)
        var thereExists = nextWords.Any(w => w.word == "there");
        Check.That(thereExists).IsFalse();
    }

    [Fact]
    public void LearnWithTimestamp_StoresTimestamp()
    {
        // Arrange
        var model = new BigramModel();
        var timestamp = DateTimeOffset.UtcNow.AddDays(-10);

        // Act
        model.LearnWithTimestamp("hello", "world", timestamp);
        model.LearnWithTimestamp("hello", "world", timestamp);

        // Assert - Verify timestamp is stored (will be tested via decay behavior)
        model.ApplyDecay(decayEnabled: true, halfLifeDays: 30);
        var nextWords = model.GetNextWords("hello", 10);

        // Entry should still exist (10 days is less than one half-life)
        Check.That(nextWords).HasSize(1);
        Check.That(nextWords[0].frequency).IsEqualTo(2);
    }

    [Fact]
    public void Learn_DefaultsToCurrentTimestamp()
    {
        // Arrange
        var model = new BigramModel();

        // Act - Use regular Learn without timestamp
        model.Learn("hello", "world");
        model.Learn("hello", "world");

        // Assert - Should behave as if timestamp is now
        model.ApplyDecay(decayEnabled: true, halfLifeDays: 30);
        var nextWords = model.GetNextWords("hello", 10);
        Check.That(nextWords).HasSize(1);
        Check.That(nextWords[0].frequency).IsEqualTo(2);
    }
}
