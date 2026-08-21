namespace EyeCtrl.Controls.Tests;

using System;
using System.Linq;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

/// <summary>
/// One rule, stated once: dwell feedback may not move the surface a label sits on.
///
/// The fill wash this replaced was a real, measured defect — a #D6DEF5 label on #2A2A44 went
/// from 10.3:1 at rest to 2.62:1 at full dwell, under the 3:1 large-text bar exactly when the
/// user was staring hardest at it. ModalButtonPalette already carried a per-kind blend factor
/// to work around the same collapse; content controls did not.
///
/// This is a relationship test (contrast at full dwell equals contrast at rest), not a pixel
/// aesthetic — the exception the repo's "layout literals are not unit-tested" convention allows.
///
/// The rule is stated for every <see cref="DwellControl"/>, but only two concrete subclasses
/// exist in <c>EyeCtrl.Controls</c> today (confirmed below): <see cref="DwellButton"/> and
/// <see cref="DwellToggle"/>. Each gets its own explicit case rather than a generically
/// constructed one — their constructors take genuinely different arguments (an <c>Action</c>
/// plus a resting <see cref="Color"/> for the button; a pair of getter/setter delegates for the
/// toggle) and forcing a single generic factory over them would trade a real assertion for
/// reflection ceremony. <see cref="KnownDwellControlSubclasses_MatchTheTestedSet"/> is the
/// tripwire that catches a third subclass arriving without its own case here: it enumerates
/// <c>EyeCtrl.Controls</c>'s own assembly (not this test assembly, so a test-only fake
/// subclassing <see cref="DwellControl"/> is never counted) and fails loudly, by name, if the
/// concrete subclass set ever grows past these two.
/// </summary>
public class DwellProgressContrastTests
{
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

    [StaFact]
    public void DwellButton_LabelContrast_IsTheSameAtFullDwellAsAtRest()
    {
        var btn = new DwellButton(
            new FakeRegistrar(), "id", "Label", () => { },
            Tokens.SurfaceMedium, DwellCategory.Control, 50, 800);

        var atRest = Contrast(Tokens.TextPrimary, ((SolidColorBrush)btn.Root.Background).Color);
        btn.UpdateVisualForTest(1.0);
        var atFull = Contrast(Tokens.TextPrimary, ((SolidColorBrush)btn.Root.Background).Color);

        Check.WithCustomMessage(
                $"label contrast fell from {atRest:F2}:1 to {atFull:F2}:1 over the dwell")
            .That(atFull).IsEqualTo(atRest);
    }

    [StaFact]
    public void DwellToggle_LabelContrast_IsTheSameAtFullDwellAsAtRest()
    {
        var state = true;
        var toggle = new DwellToggle(
            new FakeRegistrar(), "id", "Label", () => state, v => state = v,
            DwellCategory.Control, 50, 800);

        var atRest = Contrast(Tokens.TextPrimary, ((SolidColorBrush)toggle.Root.Background).Color);
        toggle.UpdateVisualForTest(1.0);
        var atFull = Contrast(Tokens.TextPrimary, ((SolidColorBrush)toggle.Root.Background).Color);

        Check.WithCustomMessage(
                $"label contrast fell from {atRest:F2}:1 to {atFull:F2}:1 over the dwell")
            .That(atFull).IsEqualTo(atRest);
    }

    /// <summary>
    /// The tripwire the type's own doc comment promises: if a third <see cref="DwellControl"/>
    /// subclass is ever added to <c>EyeCtrl.Controls</c>, this fails by name instead of the file
    /// silently continuing to cover only the original two. Scoped to
    /// <c>typeof(DwellControl).Assembly</c> (production code only) so a test-only fake subclass
    /// declared elsewhere in this test project is never picked up and cannot cause a false
    /// failure here.
    /// </summary>
    [Fact]
    public void KnownDwellControlSubclasses_MatchTheTestedSet()
    {
        var subclasses = typeof(DwellControl).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(DwellControl).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Check.WithCustomMessage(
                "A new DwellControl subclass was added to EyeCtrl.Controls but has no dwell-"
                + "contrast case in this file. Add a [StaFact] here — modelled on "
                + "DwellButton_LabelContrast_IsTheSameAtFullDwellAsAtRest above — asserting its "
                + "label contrast at full dwell equals its contrast at rest, then add its name "
                + "to the expected set in this test.")
            .That(subclasses)
            .ContainsExactly("DwellButton", "DwellToggle");
    }

    private class FakeRegistrar : IDwellRegistrar
    {
        public void Register(string id, DwellCategory category, double radius, int dwellMs, Action onActivated) { }
        public void Unregister(string id, DwellCategory category) { }
        public void UpdatePosition(string id, DwellCategory category, PhysicalPoint point) { }
        public double GetProgress(string id, DwellCategory category) => 0;
    }
}
