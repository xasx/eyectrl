namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

public class CasingPreferenceTrackerTests
{
    [Fact]
    public void RecordCasing_SingleWord_StoresPreference()
    {
        var tracker = new CasingPreferenceTracker();

        tracker.RecordCasing("Hello");

        Assert.True(tracker.HasPreference("hello"));
        Assert.Equal("Hello", tracker.GetPreferredCasing("hello"));
    }

    [Fact]
    public void RecordCasing_MultipleVariants_TracksFrequencies()
    {
        var tracker = new CasingPreferenceTracker();

        // Record different casings
        tracker.RecordCasing("hello");
        tracker.RecordCasing("Hello");
        tracker.RecordCasing("Hello");
        tracker.RecordCasing("HELLO");

        var variants = tracker.GetVariants("hello");

        Assert.Equal(3, variants.Count);
        Assert.Equal(1, variants["hello"]);
        Assert.Equal(2, variants["Hello"]);
        Assert.Equal(1, variants["HELLO"]);
    }

    [Fact]
    public void GetPreferredCasing_ReturnsHighestFrequency()
    {
        var tracker = new CasingPreferenceTracker();

        // Record "Haus" (German noun) 10 times
        for (int i = 0; i < 10; i++)
            tracker.RecordCasing("Haus");

        // Record "haus" 3 times
        for (int i = 0; i < 3; i++)
            tracker.RecordCasing("haus");

        // Preferred should be "Haus"
        Assert.Equal("Haus", tracker.GetPreferredCasing("haus"));
        Assert.Equal("Haus", tracker.GetPreferredCasing("HAUS"));
    }

    [Fact]
    public void GetPreferredCasing_NoPreference_ReturnsOriginal()
    {
        var tracker = new CasingPreferenceTracker();

        Assert.Equal("unknown", tracker.GetPreferredCasing("unknown"));
        Assert.Equal("Unknown", tracker.GetPreferredCasing("Unknown"));
    }

    [Fact]
    public void HasPreference_NoRecordings_ReturnsFalse()
    {
        var tracker = new CasingPreferenceTracker();

        Assert.False(tracker.HasPreference("test"));
    }

    [Fact]
    public void HasPreference_AfterRecording_ReturnsTrue()
    {
        var tracker = new CasingPreferenceTracker();

        tracker.RecordCasing("test");

        Assert.True(tracker.HasPreference("test"));
        Assert.True(tracker.HasPreference("Test"));
        Assert.True(tracker.HasPreference("TEST"));
    }

    [Fact]
    public void RecordCasing_CaseInsensitiveKey_AllVariantsMappedTogether()
    {
        var tracker = new CasingPreferenceTracker();

        tracker.RecordCasing("hello");
        tracker.RecordCasing("Hello");
        tracker.RecordCasing("HELLO");

        // All variants should be tracked under the same key
        var variants = tracker.GetVariants("HeLLo"); // Any casing should work

        Assert.Equal(3, variants.Count);
    }

    [Fact]
    public void GetVariants_NoPreference_ReturnsEmptyDictionary()
    {
        var tracker = new CasingPreferenceTracker();

        var variants = tracker.GetVariants("unknown");

        Assert.Empty(variants);
    }

    [Fact]
    public void LoadFromDictionary_LoadsPreferences()
    {
        var tracker = new CasingPreferenceTracker();

        var data = new Dictionary<string, Dictionary<string, int>>
        {
            ["hello"] = new Dictionary<string, int>
            {
                ["Hello"] = 10,
                ["hello"] = 5
            },
            ["world"] = new Dictionary<string, int>
            {
                ["World"] = 3
            }
        };

        tracker.LoadFromDictionary(data);

        Assert.Equal("Hello", tracker.GetPreferredCasing("hello"));
        Assert.Equal("World", tracker.GetPreferredCasing("world"));
    }

    [Fact]
    public void GetAllPreferences_ReturnsAllData()
    {
        var tracker = new CasingPreferenceTracker();

        tracker.RecordCasing("Hello");
        tracker.RecordCasing("World");

        var preferences = tracker.GetAllPreferences();

        Assert.Equal(2, preferences.Count);
        Assert.Contains("hello", preferences.Keys);
        Assert.Contains("world", preferences.Keys);
    }

    [Fact]
    public void LoadFromDictionary_ClearsExistingData()
    {
        var tracker = new CasingPreferenceTracker();

        // Add some initial data
        tracker.RecordCasing("Hello");
        tracker.RecordCasing("World");

        // Load new data
        var newData = new Dictionary<string, Dictionary<string, int>>
        {
            ["test"] = new Dictionary<string, int> { ["Test"] = 1 }
        };

        tracker.LoadFromDictionary(newData);

        // Old data should be gone
        Assert.False(tracker.HasPreference("hello"));
        Assert.False(tracker.HasPreference("world"));

        // New data should be present
        Assert.True(tracker.HasPreference("test"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void RecordCasing_NullOrEmpty_DoesNotThrow(string? input)
    {
        var tracker = new CasingPreferenceTracker();

        // Should not throw
        tracker.RecordCasing(input);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void GetPreferredCasing_NullOrEmpty_ReturnsInput(string? input)
    {
        var tracker = new CasingPreferenceTracker();

        Assert.Equal(input, tracker.GetPreferredCasing(input));
    }

    [Fact]
    public void LoadFromDictionary_HandlesNullVariants_Gracefully()
    {
        var tracker = new CasingPreferenceTracker();

        // Simulate deserialized JSON with null variant entry
        var dataWithNull = new Dictionary<string, Dictionary<string, int>>
        {
            ["word1"] = new Dictionary<string, int> { ["Word1"] = 5 },
            ["word2"] = null!, // Null variant entry
            ["word3"] = new Dictionary<string, int>() // Empty variant entry
        };

        // Should not throw
        tracker.LoadFromDictionary(dataWithNull);

        // Should only load the valid entry
        Assert.True(tracker.HasPreference("word1"));
        Assert.False(tracker.HasPreference("word2"));
        Assert.False(tracker.HasPreference("word3"));
    }
}
