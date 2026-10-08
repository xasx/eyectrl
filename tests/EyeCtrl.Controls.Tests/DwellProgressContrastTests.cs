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
/// The rule is stated for every <see cref="DwellControl"/>, and exactly ONE concrete subclass
/// exists in <c>EyeCtrl.Controls</c> today (confirmed below): <see cref="DwellButton"/>.
/// <see cref="DwellToggle"/> used to be the second, and is not one any more — it became an
/// <see cref="IDwellComposite"/> whose single target IS a <see cref="DwellButton"/>. It keeps a
/// case here regardless, because the rule is about a LABEL and the surface under it, and the
/// toggle still has a label: what changed is that its label now sits on a row card the switch's
/// dwell bar has no access to, so the invariant went from observed to structural. Both cases are
/// written out explicitly rather than generated — their constructors take genuinely different
/// arguments (an <c>Action</c> plus a resting <see cref="Color"/>; a pair of getter/setter
/// delegates) and a single generic factory over them would trade a real assertion for reflection
/// ceremony. <see cref="KnownDwellControlSubclasses_MatchTheTestedSet"/> is the tripwire that
/// catches a NEW subclass arriving without its own case: it enumerates
/// <c>EyeCtrl.Controls</c>'s own assembly (not this test assembly, so a test-only fake
/// subclassing <see cref="DwellControl"/> is never counted) and fails loudly, by name, the moment
/// the concrete subclass set changes.
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
        var registrar = new FakeDwellRegistrar();
        var btn = new DwellButton(
            registrar, "id", "Label", () => { },
            Tokens.SurfaceMedium, DwellCategory.Control, 50, 800);

        var atRest = Contrast(Tokens.TextPrimary, ((SolidColorBrush)btn.Root.Background).Color);
        registrar.Progress = 1.0;
        btn.UpdateVisual();
        var atFull = Contrast(Tokens.TextPrimary, ((SolidColorBrush)btn.Root.Background).Color);

        Check.WithCustomMessage(
                $"label contrast fell from {atRest:F2}:1 to {atFull:F2}:1 over the dwell")
            .That(atFull).IsEqualTo(atRest);
    }

    /// <summary>
    /// The toggle's label sits on its row card, and the toggle's dwell feedback is the SWITCH's
    /// progress bar — a Rectangle in the switch's own layout, in a different subtree entirely. So
    /// the collapse this file exists to prevent is now STRUCTURALLY impossible here rather than
    /// merely absent, and both halves are asserted: the measured relationship (unchanged over a
    /// full dwell, exercised through the real <see cref="DwellControl.UpdateVisual"/> path), and
    /// the reason it cannot change — the row card paints a FROZEN theme brush, which no dwell code
    /// can mutate even by accident. The relationship check alone would still pass if the card were
    /// handed a control-owned mutable brush that nothing happened to write yet.
    /// </summary>
    [StaFact]
    public void DwellToggle_LabelContrast_IsTheSameAtFullDwellAsAtRest()
    {
        var state = true;
        var registrar = new FakeDwellRegistrar();
        var toggle = new DwellToggle(
            registrar, "id", "Label", "On", "Off", () => state, v => state = v,
            DwellCategory.Control, 50, 800);

        var surface = (SolidColorBrush)toggle.RowSurface.Background;
        var label = (SolidColorBrush)toggle.Label.Foreground;

        var atRest = Contrast(label.Color, surface.Color);
        registrar.Progress = 1.0;
        toggle.Switch.UpdateVisual();
        var atFull = Contrast(
            ((SolidColorBrush)toggle.Label.Foreground).Color,
            ((SolidColorBrush)toggle.RowSurface.Background).Color);

        Check.WithCustomMessage(
                $"label contrast fell from {atRest:F2}:1 to {atFull:F2}:1 over the dwell")
            .That(atFull).IsEqualTo(atRest);

        Check.WithCustomMessage(
                "the row card's background is a mutable brush, so dwell code CAN move the surface "
                + "the label is read against — the guarantee above drops back to a coincidence. "
                + "Paint the card from a frozen Tokens brush.")
            .That(surface.IsFrozen).IsTrue();
    }

    /// <summary>
    /// The tripwire the type's own doc comment promises: if a second <see cref="DwellControl"/>
    /// subclass is ever added to <c>EyeCtrl.Controls</c>, this fails by name instead of the file
    /// silently continuing to cover only <see cref="DwellButton"/>. It shrank from two names to
    /// one when <see cref="DwellToggle"/> became an <see cref="IDwellComposite"/> — the toggle's
    /// own case above stayed, since it still has a label on a surface. Scoped to
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
                + "to the expected set in this test. Found: "
                + string.Join(", ", subclasses))
            .That(subclasses)
            .ContainsExactly("DwellButton");
    }

}
