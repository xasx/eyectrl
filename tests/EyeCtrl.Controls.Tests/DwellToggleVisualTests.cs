namespace EyeCtrl.Controls.Tests;

using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

public class DwellToggleVisualTests
{
    [StaFact]
    public void RefreshVisual_OnState_ShowsCheckedSymbol()
    {
        var state = true;
        var toggle = new TestDwellToggle(new FakeDwellRegistrar(), "id", "Label", () => state, v => state = v, DwellCategory.Control, 40, 800);

        var text = toggle.Label;
        Check.That(text.Text).StartsWith("\u2611 ");
    }

    [StaFact]
    public void RefreshVisual_OffState_ShowsUncheckedSymbol()
    {
        var state = false;
        var toggle = new TestDwellToggle(new FakeDwellRegistrar(), "id", "Label", () => state, v => state = v, DwellCategory.Control, 40, 800);

        var text = toggle.Label;
        Check.That(text.Text).StartsWith("\u2610 ");
    }

    [StaFact]
    public void Toggle_FlipsState()
    {
        var state = false;
        var toggle = new TestDwellToggle(new FakeDwellRegistrar(), "id", "Label", () => state, v => state = v, DwellCategory.Control, 40, 800);

        toggle.TestOnActivated();

        Check.That(state).IsTrue();
    }

    [StaFact]
    public void ApplyProgress_SweepsTheBar()
    {
        var state = true;
        var toggle = new TestDwellToggle(new FakeDwellRegistrar(), "id", "Label", () => state, v => state = v, DwellCategory.Control, 40, 800);

        toggle.TestApplyProgress(0.75);

        Check.That(((ScaleTransform)toggle.ProgressBar.RenderTransform).ScaleX).IsEqualTo(0.75);
    }

    /// <summary>
    /// The ON fill is #4A8F5C, on which the #D6DEF5 label already sits at 2.91:1 \u2014 under the
    /// 3:1 large-text bar before any dwell begins. Washing it toward the accent made that
    /// worse with every frame. The fill must now hold still; lifting the resting 2.91:1 needs
    /// the label off the green entirely, which is the switch anatomy in the next plan.
    /// </summary>
    [StaFact]
    public void ApplyProgress_NeverMovesTheStateFill()
    {
        var state = true;
        var toggle = new TestDwellToggle(new FakeDwellRegistrar(), "id", "Label", () => state, v => state = v, DwellCategory.Control, 40, 800);

        foreach (var p in new[] { 0.0, 0.5, 1.0 })
        {
            toggle.TestApplyProgress(p);
            Check.WithCustomMessage($"ON fill moved at progress {p}")
                .That(((SolidColorBrush)toggle.Root.Background).Color).IsEqualTo(Tokens.ToggleOn);
        }
    }

    [StaFact]
    public void ProgressBar_HeightMatchesTheConstant()
    {
        var state = true;
        var toggle = new TestDwellToggle(new FakeDwellRegistrar(), "id", "Label", () => state, v => state = v, DwellCategory.Control, 40, 800);

        Check.That(toggle.ProgressBar.Height).IsEqualTo(DwellButton.ProgressBarHeight);
    }

    [StaFact]
    public void ProgressBar_IsNotHitTestVisible()
    {
        var state = true;
        var toggle = new TestDwellToggle(new FakeDwellRegistrar(), "id", "Label", () => state, v => state = v, DwellCategory.Control, 40, 800);

        Check.That(toggle.ProgressBar.IsHitTestVisible).IsFalse();
    }

    [StaFact]
    public void ProgressBar_IsInRowOne()
    {
        var state = true;
        var toggle = new TestDwellToggle(new FakeDwellRegistrar(), "id", "Label", () => state, v => state = v, DwellCategory.Control, 40, 800);

        Check.That(Grid.GetRow(toggle.ProgressBar)).IsEqualTo(1);
    }

    /// <summary>
    /// Structural half of a two-part guard; <c>ToggleContrastTests
    /// .ProgressBar_IsPerceptibleAgainstEitherToggleFill</c> is the other. That theory proves
    /// the halo is NECESSARY — in the Contrast theme, bar-vs-ToggleOn collapses to ~1.09:1 by
    /// fill alone, rescued to ~15:1 by the halo — but it reads theme constants directly and
    /// never inspects a constructed toggle, so it stays green even if this control's Rectangle
    /// stopped drawing a halo altogether. This test proves the code HAS one: it reads the
    /// actual constructed <see cref="DwellToggle.ProgressBar"/>'s Stroke, under the ambient
    /// theme, independent of which theme that happens to be. Neither test alone closes the
    /// gap; together they do — the same pairing <c>DwellButtonProgressTests
    /// .ProgressBar_OnAnAccentColouredButton_ClearsThreeToOneAgainstItsOwnFill</c> gets for
    /// free from a single test, because that button's fill IS the accent colour (a
    /// theme-independent 1.00:1), which no <c>DwellToggle</c> fill is.
    /// </summary>
    [StaFact]
    public void ProgressBar_HasTheHaloStroke()
    {
        var state = true;
        var toggle = new TestDwellToggle(new FakeDwellRegistrar(), "id", "Label", () => state, v => state = v, DwellCategory.Control, 40, 800);

        Check.That(toggle.ProgressBar.Stroke).IsEqualTo(Tokens.BrushStrokeHalo);
        Check.That(toggle.ProgressBar.StrokeThickness).IsEqualTo(1.0);
    }

    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// Reads the toggle's REAL brushes rather than assumed theme constants (that cross-theme
    /// sweep lives in <c>ToggleContrastTests.ProgressBar_IsPerceptibleAgainstEitherToggleFill</c>).
    /// Removing the halo Stroke from the constructed <see cref="DwellToggle.ProgressBar"/> — or
    /// wiring the wrong Fill — fails HERE, on the actual instance, mirroring
    /// <c>DwellButtonProgressTests.ProgressBar_OnAnAccentColouredButton_ClearsThreeToOneAgainstItsOwnFill</c>.
    /// </summary>
    [StaFact]
    public void ProgressBar_ClearsThreeToOneAgainstTheStateFillItSitsOn()
    {
        foreach (var initialState in new[] { true, false })
        {
            var state = initialState;
            var toggle = new TestDwellToggle(new FakeDwellRegistrar(), "id", "Label", () => state, v => state = v, DwellCategory.Control, 40, 800);

            var barFill = ((SolidColorBrush)toggle.ProgressBar.Fill).Color;
            var toggleFill = ((SolidColorBrush)toggle.Root.Background).Color;
            var fillContrast = Contrast(barFill, toggleFill);
            var strokeContrast = toggle.ProgressBar.Stroke is SolidColorBrush strokeBrush
                ? Contrast(strokeBrush.Color, toggleFill)
                : 1.0; // no stroke at all -> no rescue, matches the pre-halo defect exactly

            var best = Math.Max(fillContrast, strokeContrast);
            Check.WithCustomMessage(
                    $"state={initialState}: bar vs its own toggle fill reaches only {best:F2}:1 "
                    + $"(fill {fillContrast:F2}:1, stroke {strokeContrast:F2}:1)")
                .That(best).IsStrictlyGreaterThan(3.0);
        }
    }

    private class TestDwellToggle : DwellToggle
    {
        public TestDwellToggle(IDwellRegistrar registrar, string id, string label, Func<bool> get, Action<bool> set, DwellCategory category, double radius, int dwellMs)
            : base(registrar, id, label, get, set, category, radius, dwellMs)
        {
        }

        public void TestApplyProgress(double progress) => ApplyProgress(progress);
        public void TestOnActivated() => OnActivated();
    }

    private class FakeDwellRegistrar : IDwellRegistrar
    {
        public void Register(string id, DwellCategory category, double radius, int dwellMs, Action onActivated) { }
        public void Unregister(string id, DwellCategory category) { }
        public void UpdatePosition(string id, DwellCategory category, PhysicalPoint point) { }
        public double GetProgress(string id, DwellCategory category) => 0;
    }
}
