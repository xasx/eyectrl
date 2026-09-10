namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class RepeatSettingsTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        var s = new RepeatSettings();
        Check.That(s.DwellMs).IsEqualTo(600);
        Check.That(s.StopButtonDwellMs).IsEqualTo(600);
        Check.That(s.Validate()).IsEmpty();
    }

    [Theory]
    [InlineData(199)]
    [InlineData(2001)]
    public void DwellMs_OutOfRange_Throws(int value)
    {
        ArgumentOutOfRangeException? ex = null;
        try { new RepeatSettings().DwellMs = value; }
        catch (ArgumentOutOfRangeException e) { ex = e; }
        Check.That(ex).IsNotNull();
    }

    [Theory]
    [InlineData(199)]
    [InlineData(2001)]
    public void StopButtonDwellMs_OutOfRange_Throws(int value)
    {
        ArgumentOutOfRangeException? ex = null;
        try { new RepeatSettings().StopButtonDwellMs = value; }
        catch (ArgumentOutOfRangeException e) { ex = e; }
        Check.That(ex).IsNotNull();
    }

    [Fact]
    public void BoundaryValues_AreAccepted()
    {
        var s = new RepeatSettings();
        s.DwellMs = 200;
        s.StopButtonDwellMs = 2000;
        Check.That(s.Validate()).IsEmpty();

        s.DwellMs = 2000;
        s.StopButtonDwellMs = 200;
        Check.That(s.Validate()).IsEmpty();
    }
}
