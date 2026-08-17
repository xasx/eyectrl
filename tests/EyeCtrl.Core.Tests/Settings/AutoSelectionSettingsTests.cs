namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class AutoSelectionSettingsTests
{
    [Fact]
    public void DefaultHighlightColor_IsValid()
    {
        var s = new AutoSelectionSettings();
        var errors = s.Validate();
        Check.That(errors).IsEmpty();
    }

    [Fact]
    public void InvalidHighlightColor_ReturnsError()
    {
        var s = new AutoSelectionSettings { HighlightColor = "not-a-color" };
        var errors = s.Validate();
        Check.That(errors).HasSize(1);
        Check.That(errors[0]).Contains("HighlightColor");
    }

    [Fact]
    public void HighlightThickness_OutOfRange_ReturnsError()
    {
        var s = new AutoSelectionSettings();
        Check.ThatCode(() => s.HighlightThickness = 0).Throws<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void HighlightThickness_MaxEdge_IsValid()
    {
        var s = new AutoSelectionSettings { HighlightThickness = 10 };
        var errors = s.Validate();
        Check.That(errors).IsEmpty();
    }

    [Fact]
    public void HighlightOpacity_OutOfRange_ReturnsError()
    {
        var s = new AutoSelectionSettings();
        Check.ThatCode(() => s.HighlightOpacity = 0).Throws<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void SnapSearchRadiusPixels_DefaultsTo48()
    {
        // 48 was ControlFinder's hard-coded SearchRadiusPixels before the value became a setting;
        // the default must preserve that so wiring the slider changed nothing out of the box.
        Check.That(new AutoSelectionSettings().SnapSearchRadiusPixels).IsEqualTo(48);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(48)]
    [InlineData(96)]
    public void SnapSearchRadiusPixels_WithinRange_IsValid(int value)
    {
        var s = new AutoSelectionSettings { SnapSearchRadiusPixels = value };
        Check.That(s.Validate()).IsEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(97)]
    public void SnapSearchRadiusPixels_OutOfRange_Throws(int value)
    {
        var s = new AutoSelectionSettings();
        Check.ThatCode(() => s.SnapSearchRadiusPixels = value).Throws<ArgumentOutOfRangeException>();
    }
}
