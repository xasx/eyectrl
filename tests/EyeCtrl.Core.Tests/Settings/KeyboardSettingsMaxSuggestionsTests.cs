namespace EyeCtrl.Core.Tests.Settings;

using System.Text.Json;
using EyeCtrl.Core.Settings;

/// <summary>
/// MaxSuggestions (the configurable suggestion count — first slice of prediction controls,
/// competitive gap #22): default, JSON round-trip, and range validation.
/// </summary>
public class KeyboardSettingsMaxSuggestionsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public void MaxSuggestions_DefaultsToFive()
    {
        Check.That(new KeyboardSettings().MaxSuggestions).IsEqualTo(5);
    }

    [Fact]
    public void MaxSuggestions_RoundTripsThroughJson()
    {
        var original = new EyeCtrlSettings();
        original.Keyboard.MaxSuggestions = 2;

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<EyeCtrlSettings>(json, JsonOptions)!;

        Check.That(deserialized.Keyboard.MaxSuggestions).IsEqualTo(2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void Validate_RejectsCountsOutsideOneToFive(int value)
    {
        var settings = new KeyboardSettings { MaxSuggestions = value };
        Check.That(settings.Validate()).HasElementThatMatches(e => e.Contains("MaxSuggestions"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void Validate_AcceptsOneToFive(int value)
    {
        var settings = new KeyboardSettings { MaxSuggestions = value };
        Check.That(settings.Validate().Where(e => e.Contains("MaxSuggestions"))).IsEmpty();
    }
}
