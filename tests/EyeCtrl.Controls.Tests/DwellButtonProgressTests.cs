namespace EyeCtrl.Controls.Tests;

using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

public class DwellButtonProgressTests
{
    [StaFact]
    public void ApplyProgress_AtZero_ShowsNoBar()
    {
        var btn = new TestDwellButton(new FakeDwellRegistrar(), "id", "", () => { }, Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);

        btn.TestApplyProgress(0);

        Check.That(btn.ProgressScaleX).IsEqualTo(0.0);
    }

    [StaFact]
    public void ApplyProgress_AtHalf_FillsHalfTheBar()
    {
        var btn = new TestDwellButton(new FakeDwellRegistrar(), "id", "", () => { }, Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);

        btn.TestApplyProgress(0.5);

        Check.That(btn.ProgressScaleX).IsEqualTo(0.5);
    }

    [StaFact]
    public void ApplyProgress_AtFull_FillsTheBar()
    {
        var btn = new TestDwellButton(new FakeDwellRegistrar(), "id", "", () => { }, Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);

        btn.TestApplyProgress(1.0);

        Check.That(btn.ProgressScaleX).IsEqualTo(1.0);
    }

    [StaFact]
    public void ApplyProgress_OutOfRange_IsClamped()
    {
        var btn = new TestDwellButton(new FakeDwellRegistrar(), "id", "", () => { }, Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);

        btn.TestApplyProgress(-1);
        Check.That(btn.ProgressScaleX).IsEqualTo(0.0);

        btn.TestApplyProgress(2);
        Check.That(btn.ProgressScaleX).IsEqualTo(1.0);
    }

    /// <summary>
    /// The whole point of the bar: the fill the label sits on never moves, so the label's
    /// contrast at the end of a dwell equals its contrast at rest. The wash this replaced
    /// took a #D6DEF5 label on #2A2A44 from 10.3:1 down to 2.62:1 at full dwell.
    /// </summary>
    [StaFact]
    public void ApplyProgress_NeverMovesTheFillTheLabelSitsOn()
    {
        var btn = new TestDwellButton(new FakeDwellRegistrar(), "id", "Label", () => { }, Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);

        foreach (var p in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            btn.TestApplyProgress(p);
            var brush = (SolidColorBrush)btn.Root.Background;
            Check.WithCustomMessage($"fill moved at progress {p}")
                .That(brush.Color).IsEqualTo(Tokens.SurfaceMedium);
        }
    }

    [StaFact]
    public void OnActivated_InvokesAction()
    {
        var clicked = false;
        var btn = new TestDwellButton(new FakeDwellRegistrar(), "id", "", () => clicked = true, Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);

        btn.TestOnActivated();

        Check.That(clicked).IsTrue();
    }

    /// <summary>
    /// Root.Child is the button's own layout container from here on. Callers that used to
    /// assign it (DwellComboBox, DwellColorPicker) go through Content instead — assigning
    /// Root.Child directly would evict the progress bar along with the old label.
    /// </summary>
    [StaFact]
    public void Content_IsTheLabel_AndRootChildIsTheLayoutContainer()
    {
        var btn = new DwellButton(
            new FakeDwellRegistrar(), "id", "Label", () => { },
            Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);

        Check.That(btn.Content).IsInstanceOf<TextBlock>();
        Check.That(((TextBlock)btn.Content!).Text).IsEqualTo("Label");
        Check.That(btn.Root.Child).IsInstanceOf<Grid>();
    }

    [StaFact]
    public void Content_WhenReplaced_SwapsTheOldElementOut()
    {
        var btn = new DwellButton(
            new FakeDwellRegistrar(), "id", "Label", () => { },
            Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);
        var replacement = new TextBlock { Text = "Other" };

        btn.Content = replacement;

        var grid = (Grid)btn.Root.Child;
        Check.That(grid.Children.Cast<UIElement>()).Contains(replacement);
        Check.That(grid.Children.Cast<UIElement>().OfType<TextBlock>().Count()).IsEqualTo(1);
    }

    [StaFact]
    public void Content_WhenClearedToNull_LeavesTheLayoutContainerIntact()
    {
        var btn = new DwellButton(
            new FakeDwellRegistrar(), "id", "Label", () => { },
            Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);

        btn.Content = null;

        Check.That(btn.Content).IsNull();
        Check.That(btn.Root.Child).IsInstanceOf<Grid>();
        Check.That(((Grid)btn.Root.Child).Children.Cast<UIElement>().OfType<TextBlock>()).IsEmpty();
    }

    private class TestDwellButton : DwellButton
    {
        public TestDwellButton(IDwellRegistrar registrar, string id, string label, Action action, Color baseColor, DwellCategory category, double radius, int dwellMs)
            : base(registrar, id, label, action, baseColor, category, radius, dwellMs)
        {
        }

        public double ProgressScaleX => ((ScaleTransform)ProgressBar.RenderTransform).ScaleX;

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
