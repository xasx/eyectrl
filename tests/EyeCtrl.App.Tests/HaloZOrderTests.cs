namespace EyeCtrl.App.Tests;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using NFluent;
using Xunit;

/// <summary>
/// In WPF, later siblings paint on top. A StrokeHalo element declared AFTER its
/// accent element hides the accent and renders as a dark ring on every backdrop —
/// visually obvious, invisible to every other test. This guard reads declaration
/// order straight out of the XAML.
/// </summary>
public class HaloZOrderTests
{
    /// <summary>
    /// overlay XAML (repo-relative) -> (halo element x:Name, accent element x:Name).
    ///
    /// Starts EMPTY and grows one entry per task, so the branch is green at every
    /// commit rather than red from here until the last overlay lands. Add each entry
    /// in the same commit that adds its halo.
    /// </summary>
    public static readonly Dictionary<string, (string Halo, string Accent)> HaloedOverlays = new()
    {
        ["src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml"] = ("CircleHalo", "Circle"),
        ["src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml"] = ("ProgressArcHalo", "ProgressArc"),
        ["src/EyeCtrl.App/Overlays/ManualSelectionGazeIndicatorWindow.xaml"] = ("OuterCircleHalo", "OuterCircle"),
        ["src/EyeCtrl.App/Overlays/ScrollOverlayWindow.xaml"] = ("VelocityRingHalo", "VelocityRing"),
    };

    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(
            typeof(HaloZOrderTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent;
        Check.That(dir).IsNotNull();
        return dir!;
    }

    [Fact]
    public void HaloElement_IsDeclaredBeforeItsAccentElement()
    {
        var root = RepoRoot().FullName;

        foreach (var (relative, names) in HaloedOverlays)
        {
            var xaml = File.ReadAllText(Path.Combine(root, relative));

            var haloAt = xaml.IndexOf($"x:Name=\"{names.Halo}\"", System.StringComparison.Ordinal);
            var accentAt = xaml.IndexOf($"x:Name=\"{names.Accent}\"", System.StringComparison.Ordinal);

            Check.WithCustomMessage($"{relative} has no element named '{names.Halo}'")
                .That(haloAt).IsStrictlyGreaterThan(-1);
            Check.WithCustomMessage($"{relative} has no element named '{names.Accent}'")
                .That(accentAt).IsStrictlyGreaterThan(-1);

            Check.WithCustomMessage(
                    $"{relative}: '{names.Halo}' must be declared BEFORE "
                    + $"'{names.Accent}' or it paints over the accent and renders "
                    + "as a dark ring on every backdrop (spec §4.1.3).")
                .That(haloAt).IsStrictlyLessThan(accentAt);
        }
    }

    [Fact]   // enabled by Task 6
    public void FilledCueBadge_HasAHaloOutlineStroke()
    {
        // The filled-shape case: a fill has no stroke to sit under, so it takes an
        // outline ring instead (spec §4.1.3).
        var xaml = File.ReadAllText(Path.Combine(
            RepoRoot().FullName, "src/EyeCtrl.App/Overlays/CueOverlayWindow.xaml"));

        Check.WithCustomMessage(
                "CueOverlayWindow's badge is a filled Ellipse and must carry "
                + "Stroke=\"{StaticResource BrushStrokeHalo}\" — at 1.06:1 over "
                + "amber content the unringed badge is invisible.")
            .That(xaml).Contains("BrushStrokeHalo");
    }

    [Fact]
    public void ActivationButton_NeverAllocatesABrush()
    {
        // ActivationButtonWindow redraws at gaze rate and has no user-configurable
        // colour, so it should reach for frozen Tokens brushes only.
        //
        // CursorCircleWindow is deliberately NOT checked: its ring colour comes
        // from CursorSettings.CircleColor, so it must parse a string into a brush.
        // That happens once per settings change, not per frame, and the brush is
        // frozen immediately (see Task 3). Scoping this test to ActivationButton is
        // the decision, not an oversight - do not widen it to CursorCircle without
        // first giving that file a cache keyed on the settings string.
        var path = Path.Combine(RepoRoot().FullName,
            "src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml.cs");

        Check.WithCustomMessage(
                "ActivationButtonWindow must use frozen Tokens brushes, not "
                + "allocate per update - it redraws at gaze rate.")
            .That(File.ReadAllText(path).Contains("new SolidColorBrush", System.StringComparison.Ordinal))
            .IsFalse();
    }

    //[Fact]   // enabled by Task 7
    public void DwellFeedbackOverlay_UsesTheAccentBrush()
    {
        // Not a haloed overlay (it is a filled Border, not a stroke on transparency)
        // but it is migrated in Task 7 and would otherwise have no guard at all.
        var path = Path.Combine(RepoRoot().FullName,
            "src/EyeCtrl.App/Overlays/DwellFeedbackOverlay.xaml");

        Check.WithCustomMessage(
                "DwellFeedbackOverlay must reference BrushAccent; if this fails the "
                + "migration in Task 7 was reverted.")
            .That(File.ReadAllText(path)).Contains("BrushAccent");
    }
}
