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
    public void SettleDwellMs_ClampsToRange()
    {
        var s = new DragSettings();
        s.SettleDwellMs = 50;
        Check.That(s.SettleDwellMs).IsEqualTo(200);
        s.SettleDwellMs = 5000;
        Check.That(s.SettleDwellMs).IsEqualTo(2000);
    }

    [Fact]
    public void SettleRadiusPixels_ClampsToRange()
    {
        var s = new DragSettings();
        s.SettleRadiusPixels = 2;
        Check.That(s.SettleRadiusPixels).IsEqualTo(10);
        s.SettleRadiusPixels = 500;
        Check.That(s.SettleRadiusPixels).IsEqualTo(200);
    }

    [Fact]
    public void TrailOpacity_ClampsToRange()
    {
        var s = new DragSettings();
        s.TrailOpacity = 0.0;
        Check.That(s.TrailOpacity).IsEqualTo(0.1);
        s.TrailOpacity = 2.0;
        Check.That(s.TrailOpacity).IsEqualTo(1.0);
    }

    [Fact]
    public void Validate_ReturnsNoErrors_ForDefaults()
    {
        var s = new DragSettings();
        Check.That(s.Validate()).IsEmpty();
    }
}
