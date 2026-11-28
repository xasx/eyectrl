namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

/// <summary>
/// The Lupe's onset window must stay at least as large as the sample count it scans: K &gt; M
/// makes stability onset impossible and manual selection silently stops activating. The settings
/// file is clamped at load; this pins the live setter, because the new Stability-samples row can
/// raise K above the (row-less) window during a session.
/// </summary>
public class ManualSelectionStabilityTests
{
    [Fact]
    public void RaisingStabilitySamples_LiftsTheWindow_AndValidateStaysGreen()
    {
        var settings = new ManualSelectionSettings();
        Check.That(settings.StabilityWindowSize).IsEqualTo(7);

        settings.StabilitySampleCount = 12;

        Check.That(settings.StabilityWindowSize).IsEqualTo(12);
        Check.That(settings.Validate()).IsEmpty();
    }

    [Fact]
    public void LoweringStabilitySamples_LeavesAWiderWindowAlone()
    {
        var settings = new ManualSelectionSettings { StabilityWindowSize = 20, StabilitySampleCount = 10 };

        settings.StabilitySampleCount = 4;

        Check.That(settings.StabilityWindowSize).IsEqualTo(20);
        Check.That(settings.Validate()).IsEmpty();
    }
}
