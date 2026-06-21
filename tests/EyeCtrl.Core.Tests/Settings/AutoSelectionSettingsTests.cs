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
}
