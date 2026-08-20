namespace EyeCtrl.Controls.Tests;

using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    // These four pin the static wiring ApplyProgress's tests assume but never check: change
    // any one of them (height, hit-testability, grid row, vertical alignment) and every test
    // above keeps passing while the bar breaks. Task 5 also consumes ProgressBarHeight as a
    // constant — if the element's actual Height ever drifts from it, nothing else would notice.
    [StaFact]
    public void ProgressBar_HeightMatchesTheConstant()
    {
        var btn = new TestDwellButton(new FakeDwellRegistrar(), "id", "", () => { }, Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);

        Check.That(btn.ProgressBar.Height).IsEqualTo(DwellButton.ProgressBarHeight);
    }

    [StaFact]
    public void ProgressBar_IsNotHitTestVisible()
    {
        var btn = new TestDwellButton(new FakeDwellRegistrar(), "id", "", () => { }, Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);

        Check.That(btn.ProgressBar.IsHitTestVisible).IsFalse();
    }

    [StaFact]
    public void ProgressBar_IsInRowOne()
    {
        var btn = new TestDwellButton(new FakeDwellRegistrar(), "id", "", () => { }, Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);

        Check.That(Grid.GetRow(btn.ProgressBar)).IsEqualTo(1);
    }

    [StaFact]
    public void ProgressBar_IsBottomAligned()
    {
        var btn = new TestDwellButton(new FakeDwellRegistrar(), "id", "", () => { }, Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);

        Check.That(btn.ProgressBar.VerticalAlignment).IsEqualTo(VerticalAlignment.Bottom);
    }

    /// <summary>
    /// Closes a real gap: every test above reads back the exact value ApplyProgress wrote, so
    /// flipping RenderTransformOrigin to (0.5, 0.5) (bar grows from the centre), or
    /// HorizontalAlignment to Left (bar collapses to zero width forever), or dropping
    /// Grid.SetRow(_progressBar, 1), leaves them all green. This measures the actual rendered
    /// width and the actual swept extent after a real layout pass, which none of them do.
    /// </summary>
    [StaFact]
    public void ApplyProgress_SweepsFromTheLeftEdge()
    {
        var btn = new TestDwellButton(new FakeDwellRegistrar(), "id", "", () => { }, Tokens.SurfaceMedium, DwellCategory.Control, 40, 800); // width 100
        var host = new Border { Child = btn.Root, Width = 400, Height = 200 };
        host.Measure(new Size(400, 200));
        host.Arrange(new Rect(0, 0, 400, 200));
        host.UpdateLayout();

        Check.That(btn.ProgressBar.ActualWidth).IsEqualTo(100.0); // spans the whole button

        btn.TestApplyProgress(0.5);
        var swept = btn.ProgressBar.RenderTransform.Value.Transform(
            new Point(btn.ProgressBar.ActualWidth, 0)).X;
        Check.That(swept).IsEqualTo(50.0); // 50 DIP of real width
        Check.That(btn.ProgressBar.RenderTransformOrigin.X).IsEqualTo(0.0); // grows from the left
    }

    /// <summary>
    /// Border does not clip its Child to CornerRadius. Without <c>_layout.Clip</c>, the bar's
    /// square corners paint outside the button's rounded silhouette: measured via
    /// RenderTargetBitmap, the wedge a CornerRadiusStandard corner is supposed to cut away was
    /// fully opaque at full dwell (the bar's own fill or halo stroke) and fully transparent at
    /// rest — proof that unclipped content, not the button's own Border background, was
    /// painting there. Alpha is the robust signal here: the bar's fill and its halo stroke are
    /// two different colours, so asserting "not accent-coloured" would miss the stroke case.
    /// </summary>
    [StaFact]
    public void ProgressBar_AtFullDwell_StaysInsideTheRoundedSilhouette()
    {
        var btn = new TestDwellButton(new FakeDwellRegistrar(), "id", "", () => { }, Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);
        btn.Root.Margin = new Thickness(0);
        btn.TestApplyProgress(1.0);

        const int size = 100;
        btn.Root.Measure(new Size(size, size));
        btn.Root.Arrange(new Rect(0, 0, size, size));
        btn.Root.UpdateLayout();

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(btn.Root);

        var stride = size * 4;
        var pixels = new byte[stride * size];
        rtb.CopyPixels(pixels, stride, 0);

        // Bottom corner wedges an 8 DIP (Tokens.CornerRadiusStandard) corner radius cuts away.
        // Both bottom corners, since the 6 DIP bar sits entirely inside that band.
        foreach (var (x, y) in new[] { (0, 99), (99, 99), (0, 96), (99, 96) })
        {
            var alpha = pixels[y * stride + x * 4 + 3];
            Check.WithCustomMessage(
                    $"pixel at ({x},{y}) has alpha {alpha}, outside the rounded corner — the "
                    + "progress bar (or its halo) is painting past the button's silhouette.")
                .That(alpha).IsEqualTo((byte)0);
        }
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
    /// The tightest real case, read from the button's ACTUAL brushes rather than assumed —
    /// DwellColorPicker's first swatch passes <see cref="Tokens.Accent"/> as baseColor, so its
    /// DwellButton's own background brush is exactly the bar's Fill colour. Without the halo
    /// stroke this is a literal 1.00:1 (fill on fill); this test reads
    /// <see cref="DwellButton.ProgressBar"/>'s real Fill and Stroke brushes, so removing the
    /// halo — or wiring the wrong brush — fails here, not only in the cross-theme design check
    /// in DwellButtonProgressBarContrastTests.
    /// </summary>
    [StaFact]
    public void ProgressBar_OnAnAccentColouredButton_ClearsThreeToOneAgainstItsOwnFill()
    {
        var btn = new TestDwellButton(new FakeDwellRegistrar(), "id", "", () => { }, Tokens.Accent, DwellCategory.Control, 40, 800);

        var barFill = ((SolidColorBrush)btn.ProgressBar.Fill).Color;
        var buttonFill = ((SolidColorBrush)btn.Root.Background).Color;
        var fillContrast = Contrast(barFill, buttonFill);
        var strokeContrast = btn.ProgressBar.Stroke is SolidColorBrush strokeBrush
            ? Contrast(strokeBrush.Color, buttonFill)
            : 1.0; // no stroke at all -> no rescue, matches the pre-halo defect exactly

        var best = Math.Max(fillContrast, strokeContrast);
        Check.WithCustomMessage(
                $"bar vs its own accent-coloured button reaches only {best:F2}:1 "
                + $"(fill {fillContrast:F2}:1, stroke {strokeContrast:F2}:1)")
            .That(best).IsStrictlyGreaterThan(3.0);
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
