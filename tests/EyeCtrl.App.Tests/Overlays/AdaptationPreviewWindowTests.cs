namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Settings;
using Xunit;

/// <summary>
/// Constructs the real <c>AdaptationPreviewWindow</c> (WPF Window with XAML) without showing it —
/// the <c>GlassBackdropWindowTests</c> pattern. Same collection for the same reason: the
/// <c>Application</c> singleton race, plus the P9 construction counter in
/// <c>AdaptationPreviewServiceTests</c> which this class's constructions must not interleave with.
/// </summary>
[Collection("WpfApplication")]
public class AdaptationPreviewWindowTests
{
    private const string FooterKey = "overlays.adaptationPreview.footer";
    private const string AtBaselineKey = "overlays.adaptationPreview.direction.atBaseline";
    private const string LongerKey = "overlays.adaptationPreview.direction.longer";
    private const string BandKey = "overlays.adaptationPreview.value.band";
    private const string AbsoluteKey = "overlays.adaptationPreview.value.absolute";
    private const string PxKey = "overlays.adaptationPreview.unit.px";

    [StaFact]
    public void Construction_IncrementsTheCounter_SetsClickThroughAndCaptureExclusion()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var before = AdaptationPreviewWindow.ConstructionCount;

        var window = new AdaptationPreviewWindow();

        Check.That(AdaptationPreviewWindow.ConstructionCount).IsEqualTo(before + 1);
        Check.That(window.IsClickThrough).IsTrue();
        Check.That(window.ExcludeFromScreenCapture).IsTrue();
    }

    [StaFact]
    public void HidePanel_DoesNotThrow_WhenConstructedWithoutShowing()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new AdaptationPreviewWindow();

        var exception = Record.Exception(() => window.HidePanel());

        Check.That(exception).IsNull();
    }

    [StaFact]
    public void BuildContent_FlatSnapshot_BuildsRowsAndTheNothingActiveLine()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new AdaptationPreviewWindow();
        var snapshot = NewSnapshot(enabled: false);

        window.BuildContentForTest(snapshot, "Top");

        // Spec §3.4: at baseline four flat rows PLUS the muted nothing-active line — a stable panel
        // shape, no layout jump between baseline and active.
        Check.That(window.RowItemCountForTest()).IsEqualTo(4);
        Check.That(window.NothingActiveIsVisibleForTest()).IsTrue();
        Check.That(window.FooterTextForTest()).IsEqualTo(StringTable.Current.T(FooterKey));
    }

    [StaFact]
    public void BuildContent_ActiveSnapshot_HidesTheNothingActiveLine()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new AdaptationPreviewWindow();
        var snapshot = NewSnapshot(enabled: true);

        window.BuildContentForTest(snapshot, "Top");

        Check.That(window.NothingActiveIsVisibleForTest()).IsFalse();
    }

    [Fact]
    public void ValueText_AtBaseline_UsesTheAtBaselineWord()
    {
        var row = new AdaptationPreviewRow(AdaptationKnob.DwellTime, 0.0, 0.0, false, null, null, false);

        Check.That(AdaptationPreviewWindow.ValueTextFor(StringTable.Current, row))
            .IsEqualTo(StringTable.Current.T(AtBaselineKey));
    }

    [Fact]
    public void ValueText_DwellTightening_ShowsLonger()
    {
        var row = new AdaptationPreviewRow(AdaptationKnob.DwellTime, 0.4, 0.0, true, null, null, false);
        var strings = StringTable.Current;
        var expected = strings.T(BandKey, 40, strings.T(LongerKey));

        Check.That(AdaptationPreviewWindow.ValueTextFor(strings, row)).IsEqualTo(expected);
    }

    [Fact]
    public void ValueText_Tolerance_ShowsCurrentAndBaselineWithUnit()
    {
        var row = new AdaptationPreviewRow(
            AdaptationKnob.FixationRadius, 0.2, 0.0, false, 148.0, 120.0, false);
        var strings = StringTable.Current;
        var expected = strings.T(AbsoluteKey, 148, 120, strings.T(PxKey));

        Check.That(AdaptationPreviewWindow.ValueTextFor(strings, row)).IsEqualTo(expected);
    }

    private static AdaptationPreviewSnapshot NewSnapshot(bool enabled)
    {
        var adaptation = new AdaptationSettings { Enabled = enabled };
        var provider = new EffectiveSettingsProvider(adaptation);
        if (enabled)
            provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, forgivingFill: 0.3, tighteningFill: 0);
        return AdaptationPreviewSnapshot.Build(
            adaptation, provider, new DwellSettings(), new CursorSettings());
    }
}
