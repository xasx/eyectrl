namespace EyeCtrl.App.Tests.Diagnostics;

using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// P1 — look parity between the probe ring (<see cref="ProbeRingVisual"/>, C4) and the real
/// production ring. It constructs (never shows) <see cref="CursorCircleWindow"/>, calls
/// <see cref="CursorCircleWindow.UpdateAppearance"/> and compares every painted property — stroke
/// colour, stroke thickness, band opacity, dash array, dash offset and size — for every style and
/// look. Constructing a window needs the shared WPF <see cref="Application"/> and its token
/// dictionary, hence the <c>WpfApplication</c> collection; nothing is shown and no input is
/// simulated, so CI runs it.
///
/// <para>
/// <b>Why it exists.</b> C4 is a copy of the ring drawing (the boundary rule forbids reusing the
/// production window), and a copy is exactly where drift creeps in — review 01 V3/L5. This test is
/// the pin.
/// </para>
///
/// <para>
/// <b>Mutation proofs (applied to the real <see cref="ProbeRingVisual"/>, each confirmed red).</b>
/// Dash units: dropping the <c>/ band.Thickness</c> conversions in the probe's band application —
/// killed by the <see cref="CursorRingStyle.BiTonalSegments"/> cases at thickness 3.0, where the
/// division is not a no-op (at the owner's thickness 1.0 it would be invisible, which is why the
/// matrix spans thicknesses). Per-band opacity: applying the look to each band and leaving the
/// container opaque instead of applying it to the container — killed by the quiet-look cases
/// (production's window opacity is 0.6).
/// </para>
///
/// <para>
/// <b>Equivalent mutant (documented, not used).</b> Dropping the probe's size floor,
/// <c>Math.Max(10, CircleSize)</c>. <see cref="CursorSettings.CircleSize"/>'s own setter and
/// <c>Validate()</c> reject anything below 10, so no constructible settings object or JSON-loaded
/// profile can be under the floor and the mutation cannot change behaviour. The floor stays because
/// production has it and this class is a faithful copy.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class ProbeRingParityTests
{
    private const double SizeDip = 250.0;
    private const double CoreOpacity = 0.8;
    private const string MarkColour = "#A5F3FC";

    public static TheoryData<CursorRingStyle, double, bool> StyleThicknessAndLookCases()
    {
        var cases = new TheoryData<CursorRingStyle, double, bool>();
        foreach (var style in Enum.GetValues<CursorRingStyle>())
        {
            foreach (var thickness in new[] { 1.0, 3.0 })
            {
                cases.Add(style, thickness, false);
                cases.Add(style, thickness, true);
            }
        }
        return cases;
    }

    [StaTheory]
    [MemberData(nameof(StyleThicknessAndLookCases))]
    public void ProbeRing_MatchesTheConstructedCursorCircleWindow(CursorRingStyle style, double thickness, bool pinned)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var settings = new CursorSettings
        {
            RingStyle = style,
            CircleSize = SizeDip,
            CircleThickness = thickness,
            CircleOpacity = CoreOpacity,
            CircleColor = MarkColour,
        };

        var window = new CursorCircleWindow();
        try
        {
            window.UpdateAppearance(settings);
            window.SetPinned(pinned);

            var expectedLook = pinned ? ProbeRingVisual.PinnedLookOpacity : ProbeRingVisual.QuietLookOpacity;
            var probe = new ProbeRingVisual(scale: 2.0, settings, expectedLook);

            Check.WithCustomMessage(
                    $"The probe must apply the look at the container, like production's window opacity. "
                    + $"Style {style}, thickness {thickness}, pinned {pinned}: probe {probe.Opacity}, window {window.Opacity}.")
                .That(probe.Opacity).IsEqualTo(window.Opacity);
            Check.WithCustomMessage(
                    $"The {(!pinned ? "quiet" : "pinned")} look is {(pinned ? 1.0 : 0.6)}; the probe was built with {probe.Opacity}.")
                .That(probe.Opacity).IsEqualTo(expectedLook);

            Check.WithCustomMessage($"Probe size {probe.SizeDip} DIP must match the window's {window.Width}.")
                .That(probe.SizeDip).IsEqualTo(window.Width);
            Check.That(probe.Width).IsEqualTo(window.Width);
            Check.That(probe.Height).IsEqualTo(window.Height);
            Check.That(probe.CircleHalo.Width).IsEqualTo(window.CircleHalo.Width);
            Check.That(probe.CircleHalo.Height).IsEqualTo(window.CircleHalo.Height);
            Check.That(probe.Circle.Width).IsEqualTo(window.Circle.Width);
            Check.That(probe.Circle.Height).IsEqualTo(window.Circle.Height);

            AssertBandMatchesProduction(probe.CircleHalo, window.CircleHalo, "outer band (CircleHalo)");
            AssertBandMatchesProduction(probe.Circle, window.Circle, "core band (Circle)");
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertBandMatchesProduction(Ellipse probeBand, Ellipse windowBand, string name)
    {
        var probeColour = ((SolidColorBrush)probeBand.Stroke).Color;
        var windowColour = ((SolidColorBrush)windowBand.Stroke).Color;

        Check.WithCustomMessage($"{name}: stroke colour must match production's ({probeColour} vs {windowColour}).")
            .That(probeColour).IsEqualTo(windowColour);
        Check.WithCustomMessage(
                $"{name}: stroke thickness must match production's ({probeBand.StrokeThickness} vs {windowBand.StrokeThickness}).")
            .That(probeBand.StrokeThickness).IsEqualTo(windowBand.StrokeThickness);
        Check.WithCustomMessage(
                $"{name}: band opacity must match production's ({probeBand.Opacity} vs {windowBand.Opacity}) — "
                + "the look belongs on the container, not multiplied into each band.")
            .That(probeBand.Opacity).IsEqualTo(windowBand.Opacity);

        Check.WithCustomMessage(
                $"{name}: dash array length must match production's ({probeBand.StrokeDashArray.Count} vs "
                + $"{windowBand.StrokeDashArray.Count}) — WPF dash units are multiples of StrokeThickness.")
            .That(probeBand.StrokeDashArray.Count).IsEqualTo(windowBand.StrokeDashArray.Count);
        for (var i = 0; i < windowBand.StrokeDashArray.Count; i++)
        {
            Check.WithCustomMessage($"{name}: dash array unit {i} must match production's.")
                .That(probeBand.StrokeDashArray[i]).IsEqualTo(windowBand.StrokeDashArray[i]);
        }

        Check.WithCustomMessage(
                $"{name}: dash offset must match production's ({probeBand.StrokeDashOffset} vs {windowBand.StrokeDashOffset}).")
            .That(probeBand.StrokeDashOffset).IsEqualTo(windowBand.StrokeDashOffset);
    }
}
