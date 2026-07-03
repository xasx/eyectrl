namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class ValidationGuardGapTests
{
    [Fact]
    public void DwellCooldownMs_BelowRange_Throws()
    {
        Check.ThatCode(() => new DwellSettings().DwellCooldownMs = 50)
            .Throws<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void DwellCooldownMs_AboveRange_Throws()
    {
        Check.ThatCode(() => new DwellSettings().DwellCooldownMs = 5001)
            .Throws<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void MaxGapInterpolationMs_OutOfRange_Throws()
    {
        Check.ThatCode(() => new CursorSettings().MaxGapInterpolationMs = -1)
            .Throws<ArgumentOutOfRangeException>();
        Check.ThatCode(() => new CursorSettings().MaxGapInterpolationMs = 501)
            .Throws<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ScrollStopButtonDwellMs_HasDefault600_AndRangeGuard()
    {
        Check.That(new DwellSettings().ScrollStopButtonDwellMs).IsEqualTo(600);
        Check.ThatCode(() => new DwellSettings().ScrollStopButtonDwellMs = 100)
            .Throws<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Defaults_ValidateClean()
    {
        Check.That(new DwellSettings().Validate()).IsEmpty();
        Check.That(new CursorSettings().Validate()).IsEmpty();
    }
}
