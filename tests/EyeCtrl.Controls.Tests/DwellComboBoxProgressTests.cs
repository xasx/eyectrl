namespace EyeCtrl.Controls.Tests;

using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

/// <summary>
/// <see cref="DwellComboBox"/>'s option buttons had no bar-specific coverage at all: the
/// composite migrated from assigning <c>Root.Child</c> to setting <c>Content</c>, and nothing
/// then checked that its options still sweep a bar. That is the same silent shape as the colour
/// swatches, which showed no dwell feedback for as long as the defect existed because nothing
/// throws when feedback goes missing.
///
/// Options are the one <see cref="DwellButton"/> case with a padded, auto-width root
/// (<c>MinWidth 150</c>, <c>Padding (20,0,20,0)</c>, <c>Width = NaN</c>), so they also exercise
/// the bar at a width the fixed-size tests never reach.
/// </summary>
public class DwellComboBoxProgressTests
{
    private static DwellComboBox NewCombo(FakeDwellRegistrar registrar, out string selected)
    {
        selected = "a";
        var captured = selected;
        return new DwellComboBox(
            registrar, "cb", "Mode",
            ["Alpha", "Beta"], ["a", "b"],
            () => captured, v => captured = v,
            DwellCategory.Control, 50, 800, "⚠ {0} (not available)");
    }

    [StaFact]
    public void AnOption_SweepsItsBar()
    {
        var registrar = new FakeDwellRegistrar();
        var combo = NewCombo(registrar, out _);
        var option = (DwellButton)combo.Children.First();

        registrar.Progress = 0.5;
        option.UpdateVisual();

        Check.That(((ScaleTransform)option.ProgressBar.RenderTransform).ScaleX).IsEqualTo(0.5);
    }

    /// <summary>
    /// The selection indicator lives on the wrapper border precisely so it cannot be clobbered
    /// by dwell feedback. Now that feedback is a separate element rather than a fill wash, that
    /// separation should hold trivially — but it is the invariant the composite is built
    /// around, so assert it rather than assume it.
    /// </summary>
    [StaFact]
    public void DwellingAnOption_LeavesItsFillAndTheSelectionBorderAlone()
    {
        var registrar = new FakeDwellRegistrar();
        var combo = NewCombo(registrar, out _);
        var option = (DwellButton)combo.Children.First();

        var fillBefore = ((SolidColorBrush)option.Root.Background).Color;
        var borderBefore = option.Root.BorderBrush;

        foreach (var p in new[] { 0.0, 0.5, 1.0 })
        {
            registrar.Progress = p;
            option.UpdateVisual();

            Check.WithCustomMessage($"option fill moved at progress {p}")
                .That(((SolidColorBrush)option.Root.Background).Color).IsEqualTo(fillBefore);
            Check.WithCustomMessage($"selection border changed at progress {p}")
                .That(option.Root.BorderBrush).IsSameReferenceAs(borderBefore);
        }
    }

    /// <summary>
    /// Options are padded and auto-width, so their bar's real width is the option's width less
    /// the padding — the case no fixed-size test covers. Without this, flipping the bar's
    /// HorizontalAlignment to Left collapses every option's bar to a 1 DIP dot with the rest of
    /// the suite green.
    /// </summary>
    [StaFact]
    public void AnOptionsBar_SpansThePaddedBox_AndSweepsFromTheLeft()
    {
        var registrar = new FakeDwellRegistrar();
        var combo = NewCombo(registrar, out _);
        var option = (DwellButton)combo.Children.First();

        // Lay the composite out through its own Attach path — the option's Root is already the
        // child of a wrapper border the composite owns, so re-parenting it here would throw.
        var panel = new StackPanel();
        combo.Attach(panel);
        var host = new Border { Child = panel, Width = 400, Height = 400 };
        host.Measure(new Size(400, 400));
        host.Arrange(new Rect(0, 0, 400, 400));
        host.UpdateLayout();

        var expected = option.Root.ActualWidth
            - option.Root.Padding.Left - option.Root.Padding.Right;
        Check.WithCustomMessage(
                $"bar is {option.ProgressBar.ActualWidth} DIP wide against a padded box of "
                + $"{expected} — it is not spanning the option.")
            .That(option.ProgressBar.ActualWidth).IsEqualTo(expected);

        registrar.Progress = 0.5;
        option.UpdateVisual();
        var swept = option.ProgressBar.RenderTransform.Value.Transform(
            new Point(option.ProgressBar.ActualWidth, 0)).X;
        Check.That(swept).IsEqualTo(expected / 2);
        Check.That(option.ProgressBar.RenderTransformOrigin.X).IsEqualTo(0.0);
    }

}
