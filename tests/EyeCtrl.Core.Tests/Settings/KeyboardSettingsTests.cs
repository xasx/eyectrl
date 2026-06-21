namespace EyeCtrl.Core.Tests.Settings;

using System.Linq;
using EyeCtrl.Core.Settings;

public class KeyboardSettingsTests
{
    [Fact]
    public void Defaults_MLSettings_HaveExpectedValues()
    {
        var settings = new KeyboardSettings();
        Check.That(settings.MLSuggestionsEnabled).IsTrue();
        Check.That(settings.MLModelSize).IsEqualTo("360M");
        Check.That(settings.ShowLMLoadingIndicator).IsTrue();
        Check.That(settings.PreferGPU).IsTrue();
    }

    [Theory]
    [InlineData("135M")]
    [InlineData("360M")]
    [InlineData("1.5B")]
    [InlineData("Off")]
    [InlineData("My_Custom_Model")]
    public void MLModelSize_AnyValue_Accepted(string value)
    {
        var settings = new KeyboardSettings { MLModelSize = value };
        Check.That(settings.MLModelSize).IsEqualTo(value);
        // Validation no longer restricts MLModelSize — custom model IDs must be accepted.
        Check.That(settings.Validate().Any(e => e.Contains("MLModelSize"))).IsFalse();
    }

    [Fact]
    public void CustomModels_DefaultsToEmptyList()
    {
        var settings = new KeyboardSettings();
        Check.That(settings.CustomModels).IsNotNull();
        Check.That(settings.CustomModels).IsEmpty();
    }
}
