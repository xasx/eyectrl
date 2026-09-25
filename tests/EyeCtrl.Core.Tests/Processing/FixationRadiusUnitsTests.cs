namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Tests.Flow;

/// <summary>
/// Ticket 3 row "Units": the fixation radius is in physical pixels and must never be DPI-converted. The
/// rule's whole geometry (running mean vs R) is measured in physical px, so a conversion here would
/// halve the effective radius at the user's ScaleX 2.0.
/// <para>
/// Part of the non-parallelized DpiHelper collection because it mutates global DpiHelper state.
/// Mutation: apply <c>DpiHelper.LogicalToPhysicalX</c> to R in <c>GazeEngine.EffectiveFixationRadius</c>
/// → 37.5 at ScaleX 2.0 instead of 75.
/// </para>
/// </summary>
[Collection("DpiHelper Tests")]
public class FixationRadiusUnitsTests
{
    [Fact]
    public void AtScaleX2_TheLiveDetectorRadius_EqualsTheSettingInPhysicalPixels()
    {
        DpiHelper.Initialize(2.0, 2.0);
        var settings = new EyeCtrlSettings();
        settings.Dwell.FixationRadiusPixels = 75;

        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), settings);

        Check.That(engine.FixationRadiusPixelsForTest).IsEqualTo(75.0);
    }
}
