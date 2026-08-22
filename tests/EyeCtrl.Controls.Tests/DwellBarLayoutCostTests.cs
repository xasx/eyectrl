namespace EyeCtrl.Controls.Tests;

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

/// <summary>
/// What the reserved bar row costs, recorded rather than left to be rediscovered.
///
/// <see cref="DwellButton"/> gives the bar its own layout row, so the content row is
/// <c>Height - ProgressBarHeight</c> and a label centred in it sits 3 DIP higher than before.
/// (<see cref="DwellToggle"/> used to be named here too. It has no bar of its own any more — it
/// is a composite whose single target is a <see cref="DwellButton"/>, so it inherits this cost
/// through that button rather than paying it separately.) The control's own <c>DesiredSize</c> is unchanged — the outer box does
/// not move — which is why a size-equality check passes while the label has in fact shifted.
/// Both facts are true and the second is the one a reader is likely to miss, so both are
/// pinned here.
///
/// This is a deliberate trade, not a defect: at 100 DIP there is room, and reserving makes it
/// impossible for the bar and the label to overlap. A 35 DIP keyboard key makes the opposite
/// call and overlays instead.
/// </summary>
public class DwellBarLayoutCostTests
{
    [StaFact]
    public void AReservedBarRow_ShiftsTheLabelUpByExactlyTheBarHeight()
    {
        var btn = new DwellButton(
            new FakeRegistrar(), "id", "A", () => { },
            Tokens.SurfaceMedium, DwellCategory.Control, 50, 800);
        btn.Root.Margin = new Thickness(0);

        btn.Root.Measure(new Size(100, 100));
        btn.Root.Arrange(new Rect(0, 0, 100, 100));
        btn.Root.UpdateLayout();

        var label = (TextBlock)btn.Content!;
        var topLeft = label.TransformToAncestor(btn.Root).Transform(new Point(0, 0));
        var centreY = topLeft.Y + label.ActualHeight / 2;

        // LITERALS on purpose. Writing this as (100 - ProgressBarHeight) / 2 would make the
        // assertion adapt to a change in the constant and pin nothing — verified: raising the
        // constant to 10 left the whole suite green. The point of this test is that changing
        // the bar's height MOVES EVERY LABEL, so the number has to be spelled out and the
        // change acknowledged here and in the constant's doc.
        Check.WithCustomMessage(
                $"label centre is at y={centreY}, expected 47 on a 100 DIP button with a 6 DIP "
                + "bar row. Every dwell control's label just moved — update this literal AND "
                + "DwellButton.ProgressBarHeight's doc, which records the 3 DIP shift.")
            .That(centreY).IsEqualTo(47.0);

        Check.WithCustomMessage(
                "ProgressBarHeight changed, so the 47 above and the '3 DIP shift' recorded in "
                + "its doc comment are both stale. Re-measure and update all three together.")
            .That(DwellButton.ProgressBarHeight).IsEqualTo(6.0);
    }

    [StaFact]
    public void TheOuterBox_DoesNotMove()
    {
        var btn = new DwellButton(
            new FakeRegistrar(), "id", "A", () => { },
            Tokens.SurfaceMedium, DwellCategory.Control, 50, 800);
        btn.Root.Margin = new Thickness(0);

        btn.Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        Check.That(btn.Root.DesiredSize).IsEqualTo(new Size(100, 100));
    }

    /// <summary>
    /// <c>DwellButtonProgressBarContrastTests.FillsFor</c> hardcodes the Supervisor's dialog
    /// fills, because those are literals in code rather than palette tokens. Nothing otherwise
    /// notices if a dialog is restyled, which would leave the contrast gate asserting a pairing
    /// that no longer occurs — passing while the real fill goes unchecked.
    ///
    /// Asserts SET EQUALITY against the Supervisor's own source, not mere presence: presence
    /// would miss a NEW dialog button whose fill nothing gates. The Supervisor writes these in
    /// decimal (<c>Color.FromRgb(30, 100, 30)</c>), so the gate's hex is converted here rather
    /// than matched textually.
    /// </summary>
    [Fact]
    public void TheSupervisorFills_AreExactlyTheOnesTheContrastGateChecks()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(
            typeof(DwellBarLayoutCostTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent;
        Check.WithCustomMessage("Could not locate the repo root (EyeCtrl.slnx)")
            .That(dir).IsNotNull();

        var ui = Path.Combine(dir!.FullName, "src", "EyeCtrl.Supervisor", "UI");
        Check.WithCustomMessage($"{ui} not found — has the Supervisor moved?")
            .That(Directory.Exists(ui)).IsTrue();

        var found = Directory.EnumerateFiles(ui, "*.xaml.cs")
            .SelectMany(p => Regex.Matches(
                File.ReadAllText(p),
                @"baseColor:\s*(?:Media\.)?Color\.FromRgb\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)")
                .Select(m => string.Format(
                    "#{0:X2}{1:X2}{2:X2}",
                    int.Parse(m.Groups[1].Value),
                    int.Parse(m.Groups[2].Value),
                    int.Parse(m.Groups[3].Value))))
            .Distinct()
            .OrderBy(h => h, StringComparer.Ordinal)
            .ToArray();

        // What DwellButtonProgressBarContrastTests.FillsFor gates, as hex.
        string[] gated = ["#1E641E", "#2D5A89", "#502828"];

        Check.WithCustomMessage(
                "The Supervisor's DwellButton fills and the set DwellButtonProgressBarContrastTests"
                + ".FillsFor gates have diverged."
                + " in source: " + string.Join(", ", found)
                + " | gated: " + string.Join(", ", gated)
                + ". A fill in source but not gated is a dwell bar nobody checks for contrast; "
                + "a gated fill absent from source is a pairing that cannot occur.")
            .That(found).ContainsExactly(gated);
    }

    private class FakeRegistrar : IDwellRegistrar
    {
        public void Register(string id, DwellCategory category, double radius, int dwellMs, Action onActivated) { }
        public void Unregister(string id, DwellCategory category) { }
        public void UpdatePosition(string id, DwellCategory category, PhysicalPoint point) { }
        public double GetProgress(string id, DwellCategory category) => 0;
    }
}
