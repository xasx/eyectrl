namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

public class PhraseDecayTests
{
    [Fact]
    public void ApplyDecay_WithDecayDisabled_DoesNothing()
    {
        // Arrange
        var model = new PhraseModel();
        var words = new[] { "looking", "forward", "to" };
        model.LearnPhrase(words);
        model.LearnPhrase(words);

        // Act
        model.ApplyDecay(decayEnabled: false, halfLifeDays: 30);

        // Assert
        var phrases = model.GetPhrases("looking forward", 10);
        Assert.Single(phrases);
        Assert.Equal(2, phrases[0].frequency);
    }

    [Fact]
    public void ApplyDecay_WithRecentEntry_DoesNotDecay()
    {
        // Arrange
        var model = new PhraseModel();
        var words = new[] { "thank", "you", "very", "much" };
        model.LearnPhrase(words);
        model.LearnPhrase(words);

        // Act - Apply decay immediately (0 days elapsed)
        model.ApplyDecay(decayEnabled: true, halfLifeDays: 30);

        // Assert - Frequency should remain unchanged
        var phrases = model.GetPhrases("thank you very", 10);
        Assert.Single(phrases);
        Assert.Equal(2, phrases[0].frequency);
    }

    [Fact]
    public void ApplyDecay_WithOldEntry_ReducesFrequency()
    {
        // Arrange
        var model = new PhraseModel();
        var timestamp = DateTimeOffset.UtcNow.AddDays(-60);

        // Use internal test method to set timestamp to 60 days ago
        model.LearnPhraseWithTimestamp(new[] { "how", "are", "you" }, timestamp);
        model.LearnPhraseWithTimestamp(new[] { "how", "are", "you" }, timestamp);

        // Act - Apply decay with 30-day half-life
        // After 60 days (2 half-lives), frequency should decay: 2 * 0.5^2 = 0.5
        model.ApplyDecay(decayEnabled: true, halfLifeDays: 30);

        // Assert - Entry should be removed (frequency < MinFrequency threshold of 2)
        var phrases = model.GetPhrases("how are", 10);
        Assert.Empty(phrases);
    }

    [Fact]
    public void ApplyDecay_WithMixedAges_DecaysProportionally()
    {
        // Arrange
        var model = new PhraseModel();

        // Recent entry (should not decay)
        var recentWords = new[] { "looking", "forward", "to", "it" };
        model.LearnPhraseWithTimestamp(recentWords, DateTimeOffset.UtcNow);
        model.LearnPhraseWithTimestamp(recentWords, DateTimeOffset.UtcNow);
        model.LearnPhraseWithTimestamp(recentWords, DateTimeOffset.UtcNow);
        model.LearnPhraseWithTimestamp(recentWords, DateTimeOffset.UtcNow);

        // Old entry (should decay significantly)
        var oldWords = new[] { "looking", "forward", "to", "seeing" };
        model.LearnPhraseWithTimestamp(oldWords, DateTimeOffset.UtcNow.AddDays(-90));
        model.LearnPhraseWithTimestamp(oldWords, DateTimeOffset.UtcNow.AddDays(-90));
        model.LearnPhraseWithTimestamp(oldWords, DateTimeOffset.UtcNow.AddDays(-90));
        model.LearnPhraseWithTimestamp(oldWords, DateTimeOffset.UtcNow.AddDays(-90));

        // Act - Apply decay with 30-day half-life
        model.ApplyDecay(decayEnabled: true, halfLifeDays: 30);

        // Assert
        var phrases = model.GetPhrases("looking forward to", 10);

        // "looking forward to it" should still be present with frequency 4
        var recentPhrase = phrases.FirstOrDefault(p => p.phrase == "looking forward to it");
        Assert.Equal("looking forward to it", recentPhrase.phrase);
        Assert.Equal(4, recentPhrase.frequency);

        // "looking forward to seeing" should be removed (4 * 0.5^3 = 0.5, below threshold)
        var oldExists = phrases.Any(p => p.phrase == "looking forward to seeing");
        Assert.False(oldExists);
    }

    [Fact]
    public void LearnPhraseWithTimestamp_StoresTimestamp()
    {
        // Arrange
        var model = new PhraseModel();
        var timestamp = DateTimeOffset.UtcNow.AddDays(-10);
        var words = new[] { "hello", "world" };

        // Act
        model.LearnPhraseWithTimestamp(words, timestamp);
        model.LearnPhraseWithTimestamp(words, timestamp);

        // Assert - Verify timestamp is stored (will be tested via decay behavior)
        model.ApplyDecay(decayEnabled: true, halfLifeDays: 30);
        var phrases = model.GetPhrases("hello", 10);

        // Entry should still exist (10 days is less than one half-life)
        Assert.Single(phrases);
        Assert.Equal(2, phrases[0].frequency);
    }
}
