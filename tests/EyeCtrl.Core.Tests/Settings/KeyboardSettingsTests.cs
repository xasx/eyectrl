namespace EyeCtrl.Core.Tests.Settings;

using System.Linq;
using EyeCtrl.Core.Settings;

public class KeyboardSettingsTests
{
    [Fact]
    public void Defaults_MLSettings_HaveExpectedValues()
    {
        var settings = new KeyboardSettings();
        Assert.True(settings.MLSuggestionsEnabled);
        Assert.Equal("360M", settings.MLModelSize);
        Assert.True(settings.ShowLMLoadingIndicator);
        Assert.True(settings.PreferGPU);
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
        Assert.Equal(value, settings.MLModelSize);
        // Validation no longer restricts MLModelSize — custom model IDs must be accepted.
        Assert.DoesNotContain(settings.Validate(), e => e.Contains("MLModelSize"));
    }

    [Fact]
    public void CustomModels_DefaultsToEmptyList()
    {
        var settings = new KeyboardSettings();
        Assert.NotNull(settings.CustomModels);
        Assert.Empty(settings.CustomModels);
    }
}
