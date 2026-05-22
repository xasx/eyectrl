namespace EyeCtrl.Core.Tests.Settings;

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
    public void MLModelSize_ValidValues_Accepted(string value)
    {
        var settings = new KeyboardSettings { MLModelSize = value };
        Assert.Equal(value, settings.MLModelSize);
    }
}
