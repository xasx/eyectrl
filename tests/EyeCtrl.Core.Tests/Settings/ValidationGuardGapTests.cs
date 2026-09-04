namespace EyeCtrl.Core.Tests.Settings;

using System.Reflection;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public class ValidationGuardGapTests
{
    // Inject an out-of-range value past the throwing setter guard, so Validate() itself can be
    // exercised (the setter would otherwise reject the value before Validate() ever sees it).
    private static void SetBackingField(object settings, string fieldName, object value)
    {
        var field = settings.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Check.That(field).IsNotNull();
        field!.SetValue(settings, value);
    }

    [Fact]
    public void VelocityThresholdPxPerSec_NotPositive_Throws()
    {
        Check.ThatCode(() => new DwellSettings().VelocityThresholdPxPerSec = 0)
            .Throws<ArgumentOutOfRangeException>();
        Check.ThatCode(() => new DwellSettings().VelocityThresholdPxPerSec = -5)
            .Throws<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_ReportsDwellCooldownMs_WhenOutOfRange()
    {
        var s = new DwellSettings();
        SetBackingField(s, "_dwellCooldownMs", 50);
        Check.That(s.Validate()).HasElementThatMatches(e => e.Contains(nameof(DwellSettings.DwellCooldownMs)));
    }

    [Fact]
    public void Validate_ReportsVelocityThreshold_WhenNotPositive()
    {
        var s = new DwellSettings();
        SetBackingField(s, "_velocityThresholdPxPerSec", 0.0);
        Check.That(s.Validate()).HasElementThatMatches(e => e.Contains(nameof(DwellSettings.VelocityThresholdPxPerSec)));
    }

    [Fact]
    public void Validate_ReportsMaxGapInterpolationMs_WhenOutOfRange()
    {
        var s = new CursorSettings();
        SetBackingField(s, "_maxGapInterpolationMs", 501);
        Check.That(s.Validate()).HasElementThatMatches(e => e.Contains(nameof(CursorSettings.MaxGapInterpolationMs)));
    }

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
    public void ManualSelectionSideButtonDwellMs_HasDefault600_AndRangeGuard()
    {
        Check.That(new DwellSettings().ManualSelectionSideButtonDwellMs).IsEqualTo(600);
        Check.ThatCode(() => new DwellSettings().ManualSelectionSideButtonDwellMs = 100)
            .Throws<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BackdropHideButtonDwellMs_HasDefault600_AndRangeGuard()
    {
        Check.That(new DwellSettings().BackdropHideButtonDwellMs).IsEqualTo(600);
        Check.ThatCode(() => new DwellSettings().BackdropHideButtonDwellMs = 100)
            .Throws<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Defaults_ValidateClean()
    {
        Check.That(new DwellSettings().Validate()).IsEmpty();
        Check.That(new CursorSettings().Validate()).IsEmpty();
    }

    [Fact]
    public void Validate_ReportsRingStyle_WhenOutOfRange()
    {
        // No OnChanging guard exists for RingStyle (unlike the other CursorSettings
        // properties above), so a plain assignment reaches the field directly — no
        // reflection needed to get the bad value in. JsonStringEnumConverter accepts
        // integers by default, so a hand-edited settings.json can produce exactly this:
        // an undefined CursorRingStyle that CursorRingStacks.For's `_ =>` arm throws on.
        var s = new CursorSettings { RingStyle = (CursorRingStyle)99 };
        Check.That(s.Validate()).HasElementThatMatches(e => e.Contains(nameof(CursorSettings.RingStyle)));
    }

    [Fact]
    public void Validate_ReportsPositionIndicatorStyle_WhenOutOfRange()
    {
        // No OnChanging guard exists for PositionIndicatorStyle either, so a plain assignment
        // reaches the field directly — no reflection needed to get the bad value in.
        // JsonStringEnumConverter accepts integers by default, so a hand-edited settings.json
        // can produce exactly this: an undefined PositionIndicatorStyle. Unlike RingStyle, no
        // switch throws on it — ActionMenuWindow.Show only tests `== Crosshair` and falls
        // through to the Pin branch for anything else — but Validate() should still catch an
        // undefined value rather than let it be silently reinterpreted as Pin.
        var s = new CursorSettings { PositionIndicatorStyle = (PositionIndicatorStyle)99 };
        Check.That(s.Validate()).HasElementThatMatches(e => e.Contains(nameof(CursorSettings.PositionIndicatorStyle)));
    }
}
