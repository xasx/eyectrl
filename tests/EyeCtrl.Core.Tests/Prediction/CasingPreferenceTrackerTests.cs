namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

public class CasingPreferenceTrackerTests
{
    [Fact]
    public void RecordCasing_SingleWord_StoresPreference()
    {
        var tracker = new CasingPreferenceTracker();

        tracker.RecordCasing("Hello");

        Check.That(tracker.HasPreference("hello")).IsTrue();
        Check.That(tracker.GetPreferredCasing("hello")).IsEqualTo("Hello");
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

        Check.That(variants.Count).IsEqualTo(3);
        Check.That(variants["hello"]).IsEqualTo(1);
        Check.That(variants["Hello"]).IsEqualTo(2);
        Check.That(variants["HELLO"]).IsEqualTo(1);
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
        Check.That(tracker.GetPreferredCasing("haus")).IsEqualTo("Haus");
        Check.That(tracker.GetPreferredCasing("HAUS")).IsEqualTo("Haus");
    }

    [Fact]
    public void GetPreferredCasing_NoPreference_ReturnsOriginal()
    {
        var tracker = new CasingPreferenceTracker();

        Check.That(tracker.GetPreferredCasing("unknown")).IsEqualTo("unknown");
        Check.That(tracker.GetPreferredCasing("Unknown")).IsEqualTo("Unknown");
    }

    [Fact]
    public void HasPreference_NoRecordings_ReturnsFalse()
    {
        var tracker = new CasingPreferenceTracker();

        Check.That(tracker.HasPreference("test")).IsFalse();
    }

    [Fact]
    public void HasPreference_AfterRecording_ReturnsTrue()
    {
        var tracker = new CasingPreferenceTracker();

        tracker.RecordCasing("test");

        Check.That(tracker.HasPreference("test")).IsTrue();
        Check.That(tracker.HasPreference("Test")).IsTrue();
        Check.That(tracker.HasPreference("TEST")).IsTrue();
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

        Check.That(variants.Count).IsEqualTo(3);
    }

    [Fact]
    public void GetVariants_NoPreference_ReturnsEmptyDictionary()
    {
        var tracker = new CasingPreferenceTracker();

        var variants = tracker.GetVariants("unknown");

        Check.That(variants).IsEmpty();
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

        Check.That(tracker.GetPreferredCasing("hello")).IsEqualTo("Hello");
        Check.That(tracker.GetPreferredCasing("world")).IsEqualTo("World");
    }

    [Fact]
    public void GetAllPreferences_ReturnsAllData()
    {
        var tracker = new CasingPreferenceTracker();

        tracker.RecordCasing("Hello");
        tracker.RecordCasing("World");

        var preferences = tracker.GetAllPreferences();

        Check.That(preferences.Count).IsEqualTo(2);
        Check.That(preferences.Keys).Contains("hello");
        Check.That(preferences.Keys).Contains("world");
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
        Check.That(tracker.HasPreference("hello")).IsFalse();
        Check.That(tracker.HasPreference("world")).IsFalse();

        // New data should be present
        Check.That(tracker.HasPreference("test")).IsTrue();
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

        Check.That(tracker.GetPreferredCasing(input)).IsEqualTo(input);
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
        Check.That(tracker.HasPreference("word1")).IsTrue();
        Check.That(tracker.HasPreference("word2")).IsFalse();
        Check.That(tracker.HasPreference("word3")).IsFalse();
    }
}
