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
    /// Every haloed pair: (overlay XAML repo-relative, halo x:Name, accent x:Name).
    ///
    /// A flat list, NOT a dictionary keyed on the file. It was a
    /// <c>Dictionary&lt;string, (Halo, Accent)&gt;</c>, which allows exactly one pair per
    /// file — so when <c>ManualSelectionGazeIndicatorWindow</c> gained a second halo
    /// (<c>CenterDotHalo</c>/<c>CenterDot</c>) the slot was already taken by
    /// <c>OuterCircleHalo</c> and the new pair could not be expressed at all. Two pairs
    /// added by later fixes were therefore unguarded, on the one check whose failure mode
    /// (a dark disc painted over the mark) no other test can see.
    /// </summary>
    public static readonly (string File, string Halo, string Accent)[] HaloedPairs =
    [
        ("src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml", "CircleHalo", "Circle"),
        ("src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml", "ProgressHalo", "ProgressRing"),
        ("src/EyeCtrl.App/Overlays/ManualSelectionGazeIndicatorWindow.xaml", "OuterCircleHalo", "OuterCircle"),
        ("src/EyeCtrl.App/Overlays/ManualSelectionGazeIndicatorWindow.xaml", "CenterDotHalo", "CenterDot"),
        ("src/EyeCtrl.App/Overlays/ManualSelectionGazeIndicatorWindow.xaml", "AnchorMarkHalo", "AnchorMark"),
        ("src/EyeCtrl.App/Overlays/ScrollOverlayWindow.xaml", "VelocityRingHalo", "VelocityRing"),
        ("src/EyeCtrl.App/Overlays/CueOverlayWindow.xaml", "BadgeHalo", "Badge"),
        ("src/EyeCtrl.App/Overlays/ManualSelectionOverlayWindow.xaml", "RimHalo", "Rim"),
        ("src/EyeCtrl.App/Overlays/CalibrationWindow.xaml", "ToleranceHalo", "ToleranceRing"),
        ("src/EyeCtrl.App/Overlays/CalibrationWindow.xaml", "ProgressHalo", "ProgressRing"),
    ];

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

        foreach (var (relative, halo, accent) in HaloedPairs)
        {
            var xaml = File.ReadAllText(Path.Combine(root, relative));

            // The closing quote is part of the search string, so "CenterDot" does not
            // match the element named "CenterDotHalo".
            var haloAt = xaml.IndexOf($"x:Name=\"{halo}\"", System.StringComparison.Ordinal);
            var accentAt = xaml.IndexOf($"x:Name=\"{accent}\"", System.StringComparison.Ordinal);

            Check.WithCustomMessage($"{relative} has no element named '{halo}'")
                .That(haloAt).IsStrictlyGreaterThan(-1);
            Check.WithCustomMessage($"{relative} has no element named '{accent}'")
                .That(accentAt).IsStrictlyGreaterThan(-1);

            Check.WithCustomMessage(
                    $"{relative}: '{halo}' must be declared BEFORE "
                    + $"'{accent}' or it paints over the accent and renders "
                    + "as a dark ring on every backdrop (spec §4.1.3).")
                .That(haloAt).IsStrictlyLessThan(accentAt);
        }
    }

    [Fact]
    public void EveryHaloElement_UsesTheStrokeHaloBrush()
    {
        // A correctly ordered element painted in the wrong colour is not a halo. Pairs the
        // ordering check with a colour check so neither half can pass alone.
        var root = RepoRoot().FullName;

        foreach (var (relative, halo, _) in HaloedPairs)
        {
            var xaml = File.ReadAllText(Path.Combine(root, relative));
            var at = xaml.IndexOf($"x:Name=\"{halo}\"", System.StringComparison.Ordinal);
            Check.That(at).IsStrictlyGreaterThan(-1);

            // Scan from the element's x:Name to the end of that element's tag.
            var close = xaml.IndexOf('>', at);
            var element = close > at ? xaml[at..close] : xaml[at..];

            Check.WithCustomMessage(
                    $"{relative}: '{halo}' must paint with BrushStrokeHalo — a halo in any "
                    + "other colour gives no backdrop-independent edge (spec §4.1.3).")
                .That(element).Contains("BrushStrokeHalo");
        }
    }

    [Fact]
    public void FilledCueBadge_KeepsItsHaloRingOutsideTheFadingFill()
    {
        // This test used to assert only that the file contained "BrushStrokeHalo" — true
        // both before and after the badge was split into BadgeHalo (opaque ring) + Badge
        // (fading fill), so it kept passing while no longer checking its own premise.
        //
        // What actually matters is the split: the ring must NOT carry the animated Opacity,
        // because dimming halo and fill together puts the backdrop back into both terms and
        // voids the §4.1.3 edge. Assert the structure, not the substring: the halo is a
        // stroke-only element, and the fill element is the one that starts at Opacity 0.
        var xaml = File.ReadAllText(Path.Combine(
            RepoRoot().FullName, "src/EyeCtrl.App/Overlays/CueOverlayWindow.xaml"));

        var haloAt = xaml.IndexOf("x:Name=\"BadgeHalo\"", System.StringComparison.Ordinal);
        var badgeAt = xaml.IndexOf("x:Name=\"Badge\"", System.StringComparison.Ordinal);
        Check.WithCustomMessage("CueOverlayWindow must declare BadgeHalo and Badge separately")
            .That(haloAt).IsStrictlyGreaterThan(-1);
        Check.That(badgeAt).IsStrictlyGreaterThan(-1);

        var haloElement = xaml[haloAt..xaml.IndexOf('>', haloAt)];
        var badgeElement = xaml[badgeAt..xaml.IndexOf('>', badgeAt)];

        Check.WithCustomMessage(
                "BadgeHalo must be a stroke-only ring: giving it a Fill would put the fading "
                + "fill and the ring on one element again.")
            .That(haloElement).Not.Contains("Fill=");
        Check.WithCustomMessage(
                "BadgeHalo must not carry Opacity — the animated fade belongs to Badge alone, "
                + "or the §4.1.3 edge stops being backdrop-independent (1.48:1 over white).")
            .That(haloElement).Not.Contains("Opacity");
        Check.WithCustomMessage("Badge carries the fill and the animated opacity")
            .That(badgeElement).Contains("Fill=");
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

    [Fact]   // enabled by Task 7
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
