namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// §13.3 smoothing routing: the provider→filter VALUE path, the active-filter range clamp, and (Task 3)
/// the knob-aware request, the §9.1 rebuild budget, and R1 continuity on the adaptive path. Drives a real
/// <see cref="GazeEngine"/> with a synchronous fake provider — each SimulateGaze runs one OnGazePoint
/// inline, so a deferred reconfiguration is applied and observable with no threading.
/// </summary>
public class GazeEngineSmoothingRoutingTests
{
    // Default EyeCtrlSettings.Cursor: OneEuro, OneEuroMinCutoff 5.0, OneEuroBeta 0.5, SmoothingFactor 0.5.
    // Beta is pinned to 0 so the One-Euro cutoff == minCutoff at every speed — isolates the parameter under
    // test (same trick as GazeEngineFilterRecreationTests); with the default beta 0.5 a fast sample spikes
    // the speed-adaptive cutoff and washes the effect out.
    private static (GazeEngine Engine, EffectiveSettingsProvider Provider, TestGazeProvider TestGaze, EyeCtrlSettings Settings) Make(
        GazeFilterMode mode = GazeFilterMode.OneEuro, bool adaptEnabled = true)
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = mode;
        settings.Cursor.OneEuroBeta = 0.0;
        settings.Adaptation.Enabled = adaptEnabled;
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var testGaze = new TestGazeProvider();
        var engine = new GazeEngine(testGaze, new TestInputSimulator(), settings, effectiveSettings: provider);
        return (engine, provider, testGaze, settings);
    }

    // --- Task 2: the value path (method-level, no pumping) ---

    [Fact]
    public void EffectiveParameter_NoProvider_ReturnsNull()
    {
        var settings = new EyeCtrlSettings();
        var engine = new GazeEngine(new TestGazeProvider(), new TestInputSimulator(), settings);
        Check.That(engine.EffectiveSmoothingParameter()).IsNull();
    }

    [Theory]
    [InlineData(GazeFilterMode.OneEuro, 5.0)]
    [InlineData(GazeFilterMode.Smoothed, 0.5)]
    public void EffectiveParameter_NoFill_EqualsTheActiveModesBaseline(GazeFilterMode mode, double baseline)
    {
        var (engine, _, _, _) = Make(mode);
        Check.That(engine.EffectiveSmoothingParameter()).IsEqualTo(baseline);
    }

    [Fact]
    public void EffectiveParameter_Disabled_EqualsBaseline_EvenWithFillSet()
    {
        var (engine, provider, _, _) = Make(GazeFilterMode.OneEuro, adaptEnabled: false);
        provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        Check.That(engine.EffectiveSmoothingParameter()).IsEqualTo(5.0);
    }

    [Fact]
    public void EffectiveParameter_Direct_ReturnsNull_EvenWithFillSet()
    {
        // §13.3: DirectFilter takes no smoothing parameter and mode is never auto-switched (§5), so the
        // knob must no-op — the smoothing fill is simply not observable in Direct mode.
        var (engine, provider, _, _) = Make(GazeFilterMode.Direct);
        provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        Check.That(engine.EffectiveSmoothingParameter()).IsNull();
    }

    [Theory]
    // mode, baseline, band fraction, expected — the fill is always the full band (1.0)
    [InlineData(GazeFilterMode.OneEuro, 5.0, 0.50, 2.5)]    // plain composition, no clamp
    [InlineData(GazeFilterMode.OneEuro, 0.2, 0.50, 0.1)]    // composes exactly onto the floor
    [InlineData(GazeFilterMode.OneEuro, 0.1, 0.50, 0.1)]    // 0.05 < floor 0.1 → clamped (§13.3 safety)
    [InlineData(GazeFilterMode.OneEuro, 0.1, 1.00, 0.1)]    // 0.0: OneEuroFilter's ctor would throw → clamped
    [InlineData(GazeFilterMode.OneEuro, 20.0, 0.50, 10.0)]  // top baseline; the upper bound never engages
    [InlineData(GazeFilterMode.Smoothed, 0.5, 0.50, 0.25)]  // EMA composes the same way
    [InlineData(GazeFilterMode.Smoothed, 1.0, 1.00, 0.0)]   // EMA floor is 0, and 0.0 is a legal factor
    public void EffectiveParameter_ClampsToTheActiveFiltersValidatedRange(
        GazeFilterMode mode, double baseline, double fraction, double expected)
    {
        // The provider's Smoothing policy carries the union superset [0, 20]; the ACTIVE filter's own
        // validated range ([0.1, 20] One-Euro, [0, 1] EMA) is re-applied here (§13.3 safety requirement).
        var (engine, provider, _, settings) = Make(mode);
        if (mode == GazeFilterMode.OneEuro) settings.Cursor.OneEuroMinCutoff = baseline;
        else settings.Cursor.SmoothingFactor = baseline;
        settings.Adaptation.SmoothingMoreMaxFraction = fraction;
        provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);

        Check.That(engine.EffectiveSmoothingParameter()).IsEqualTo(expected);
    }

    [Theory]
    [InlineData(GazeFilterMode.OneEuro, 0.1, 1.0)]   // the composed-0.0 trap: must assign 0.1, not 0.0
    [InlineData(GazeFilterMode.Smoothed, 0.5, 1.0)]
    public void EffectiveParameter_IsAlwaysAssignableToTheSettingsGuard(GazeFilterMode mode, double baseline, double fill)
    {
        // Setter-as-oracle drift guard (the FixationRadius precedent): the CursorSettings setters ARE the
        // validated range — they throw outside it. If the clamp ever drifts from the guard, this throws.
        var (engine, provider, _, settings) = Make(mode);
        if (mode == GazeFilterMode.OneEuro) settings.Cursor.OneEuroMinCutoff = baseline;
        else settings.Cursor.SmoothingFactor = baseline;
        provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: fill, tighteningFill: 0.0);

        double effective = engine.EffectiveSmoothingParameter()!.Value;
        if (mode == GazeFilterMode.OneEuro) settings.Cursor.OneEuroMinCutoff = effective;
        else settings.Cursor.SmoothingFactor = effective;
    }

    [Fact]
    public void EffectiveParameter_ComposesAgainstTheActiveModesBaseline_AcrossAModeSwitch()
    {
        // The fill is a fraction OF the baseline, and the baseline is resolved PER MODE at read time:
        // OneEuro 5.0 → 2.5, then switching the mode to Smoothed (baseline 0.5) composes the SAME fill
        // against 0.5 → 0.25. The apply-time re-read (Task 3) is what makes a mode switch converge.
        var (engine, provider, _, settings) = Make(GazeFilterMode.OneEuro);
        provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        Check.That(engine.EffectiveSmoothingParameter()).IsEqualTo(2.5);

        settings.Cursor.FilterMode = GazeFilterMode.Smoothed;
        Check.That(engine.EffectiveSmoothingParameter()).IsEqualTo(0.25);
    }

    [Fact]
    public void AppliedParameter_AfterConstruction_IsTheInitialBaselineParameter()
    {
        var (engine, _, _, _) = Make(GazeFilterMode.OneEuro);
        Check.That(engine.AppliedSmoothingParameter).IsEqualTo(5.0);
    }

    [Fact]
    public void AppliedParameter_DirectModeAtConstruction_IsNull()
    {
        var (engine, _, _, _) = Make(GazeFilterMode.Direct);
        Check.That(engine.AppliedSmoothingParameter).IsNull();
    }

    // --- Fakes (own copies — no shared base class; mirrors GazeEngineFilterRecreationTests) ---

    private sealed class TestGazeProvider : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected => true;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void SimulateGaze(double x, double y, long ts) =>
            GazePointReceived?.Invoke(new GazePoint(x, y, ts, true));
    }

    private sealed class TestInputSimulator : IInputSimulator
    {
        public void MoveCursor(PhysicalPoint point) { }
        public void LeftClick(PhysicalPoint point) { }
        public void RightClick(PhysicalPoint point) { }
        public void DoubleClick(PhysicalPoint point) { }
        public void MouseDown(PhysicalPoint point) { }
        public void MouseUp(PhysicalPoint point) { }
        public void Scroll(PhysicalPoint point, int deltaX, int deltaY) { }
        public void SendKeyPress(ushort virtualKeyCode) { }
        public void SendKeyDown(ushort virtualKeyCode) { }
        public void SendKeyUp(ushort virtualKeyCode) { }
        public void SendText(string text) { }
        public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode) { }
    }
}
