namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class DragSettingsTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var s = new DragSettings();
        Check.That(s.SettleDwellMs).IsEqualTo(500);
        Check.That(s.SettleRadiusPixels).IsEqualTo(40);
        Check.That(s.TrailColor).IsEqualTo("#ff9800");
        Check.That(s.TrailOpacity).IsEqualTo(0.7);
    }

    [Fact]
    public void SettleDwellMs_ThrowsOutOfRange_BelowMin()
    {
        var s = new DragSettings();
        ArgumentException? ex = null;
        try { s.SettleDwellMs = 50; }
        catch (ArgumentException e) { ex = e; }
        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("value");
    }

    [Fact]
    public void SettleDwellMs_ThrowsOutOfRange_AboveMax()
    {
        var s = new DragSettings();
        ArgumentException? ex = null;
        try { s.SettleDwellMs = 5000; }
        catch (ArgumentException e) { ex = e; }
        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("value");
    }

    [Fact]
    public void SettleRadiusPixels_ThrowsOutOfRange_BelowMin()
    {
        var s = new DragSettings();
        ArgumentException? ex = null;
        try { s.SettleRadiusPixels = 2; }
        catch (ArgumentException e) { ex = e; }
        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("value");
    }

    [Fact]
    public void SettleRadiusPixels_ThrowsOutOfRange_AboveMax()
    {
        var s = new DragSettings();
        ArgumentException? ex = null;
        try { s.SettleRadiusPixels = 500; }
        catch (ArgumentException e) { ex = e; }
        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("value");
    }

    [Fact]
    public void TrailOpacity_ThrowsOutOfRange_BelowMin()
    {
        var s = new DragSettings();
        ArgumentException? ex = null;
        try { s.TrailOpacity = 0.0; }
        catch (ArgumentException e) { ex = e; }
        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("value");
    }

    [Fact]
    public void TrailOpacity_ThrowsOutOfRange_AboveMax()
    {
        var s = new DragSettings();
        ArgumentException? ex = null;
        try { s.TrailOpacity = 2.0; }
        catch (ArgumentException e) { ex = e; }
        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("value");
    }

    [Fact]
    public void Validate_ReturnsNoErrors_ForDefaults()
    {
        var s = new DragSettings();
        Check.That(s.Validate()).IsEmpty();
    }
}
