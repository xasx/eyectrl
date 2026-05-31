namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class AutoSelectionSettingsTests
{
    [Fact]
    public void DefaultHighlightColor_IsValid()
    {
        var s = new AutoSelectionSettings();
        var errors = s.Validate();
        Assert.Empty(errors);
    }

    [Fact]
    public void InvalidHighlightColor_ReturnsError()
    {
        var s = new AutoSelectionSettings { HighlightColor = "not-a-color" };
        var errors = s.Validate();
        Assert.Single(errors);
        Assert.Contains("HighlightColor", errors[0]);
    }

    [Fact]
    public void HighlightThickness_OutOfRange_ReturnsError()
    {
        var s = new AutoSelectionSettings { HighlightThickness = 0 };
        var errors = s.Validate();
        Assert.Single(errors);
    }

    [Fact]
    public void HighlightThickness_MaxEdge_IsValid()
    {
        var s = new AutoSelectionSettings { HighlightThickness = 10 };
        var errors = s.Validate();
        Assert.Empty(errors);
    }

    [Fact]
    public void HighlightOpacity_OutOfRange_ReturnsError()
    {
        var s = new AutoSelectionSettings { HighlightOpacity = 0 };
        var errors = s.Validate();
        Assert.Single(errors);
    }
}
