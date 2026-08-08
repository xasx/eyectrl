namespace EyeCtrl.App.Tests;

using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Pins the WPF behaviour <see cref="ITheme.CornerRadiusCircle"/> rests on, measured in
/// docs/superpowers/specs/2026-08-06-overlay-geometry-typography-tokens-design.md §4.1: an
/// over-large CornerRadius clamps to an inscribed ellipse.
///
/// On a SQUARE element that is a circle, rendering pixel-identically to size / 2 — which is
/// what makes the token size-independent, and what lets QuickActionsGridWindow's 44x44 close
/// button drop its literal 22. On a non-square element it is an ellipse, NOT a stadium, which
/// is why the token is named Circle rather than Pill.
///
/// Renders to a RenderTargetBitmap on an STA thread. No window is shown and no input is
/// simulated — safe to run headless, and it carries no UI category so CI runs it.
/// </summary>
public class CornerRadiusClampTests
{
    private static byte[] Render(double w, double h, double radius, double dpi)
    {
        var border = new Border
        {
            Width = w,
            Height = h,
            Background = Brushes.Black,
            CornerRadius = new CornerRadius(radius),
        };
        border.Measure(new Size(w, h));
        border.Arrange(new Rect(0, 0, w, h));
        border.UpdateLayout();

        var scale = dpi / 96.0;
        var pw = (int)Math.Round(w * scale);
        var ph = (int)Math.Round(h * scale);
        var rtb = new RenderTargetBitmap(pw, ph, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(border);

        var stride = pw * 4;
        var pixels = new byte[stride * ph];
        rtb.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static int DifferingPixels(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
            throw new InvalidOperationException($"Bitmaps differ in size: {a.Length} vs {b.Length} bytes.");
        var n = 0;
        for (var i = 0; i < a.Length; i += 4)
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2] || a[i + 3] != b[i + 3])
                n++;
        return n;
    }

    /// <summary>
    /// Fraction of pixels at least half covered. The 50% cutoff is the correct way to count a
    /// rasterised area and is the only one that reproduces the analytic value —
    /// docs/superpowers/specs/2026-08-06-overlay-geometry-typography-tokens-design.md §4.1
    /// records the same shape measuring 0.8094 / 0.7854 / 0.7671 / 0.7527 at cutoffs of
    /// 0 / 128 / 200 / 254, so a bare fraction without its threshold is meaningless.
    /// </summary>
    private static double CoveredFraction(byte[] pixels)
    {
        var covered = 0;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] > 128) covered++;
        return (double)covered / (pixels.Length / 4);
    }

    public static TheoryData<double, double> SquareCases() => new()
    {
        { 30, 96 }, { 44, 96 }, { 45, 96 }, { 66, 96 }, { 100, 96 }, { 44, 144 }, { 44, 192 },
    };

    [StaTheory]
    [MemberData(nameof(SquareCases))]
    public void OnASquare_TheCircleToken_RendersIdenticallyToHalfTheSize(double size, double dpi)
    {
        var half = Render(size, size, size / 2, dpi);
        // Reads a concrete theme instance rather than the ambient Tokens.Current: this test
        // shares the EyeCtrl.App.Tests assembly with ThemeStartupTests, which mutates
        // Tokens.Current via Tokens.Activate(...) and relies on its [Collection("ActiveTheme")]
        // to serialise that mutation against every other theme test. This test isn't in that
        // collection and could run concurrently with it, so it must not read the mutable
        // static — the WPF clamp being pinned here doesn't depend on which theme is active.
        var token = Render(size, size, MidnightTheme.Instance.CornerRadiusCircle, dpi);

        var differing = DifferingPixels(half, token);
        Check.WithCustomMessage(
                $"A {size}x{size} Border at CornerRadiusCircle must render exactly as one at "
                + $"{size / 2} ({dpi} dpi). If this fails, WPF no longer clamps an over-large "
                + "CornerRadius and the token is not size-independent — see "
                + "docs/superpowers/specs/2026-08-06-overlay-geometry-typography-tokens-design.md "
                + $"§4.1. ({differing} of {half.Length / 4} pixels differ)")
            .That(differing).IsEqualTo(0);
    }

    [StaFact]
    public void OnANonSquare_TheCircleToken_IsAnEllipseNotAStadium()
    {
        // Pins the token's documented RESTRICTION, so misuse is caught rather than merely
        // discouraged in a doc comment.
        var stadium = Render(120, 40, 20, 96);
        var token = Render(120, 40, MidnightTheme.Instance.CornerRadiusCircle, 96);

        Check.WithCustomMessage("A 120x40 at the Circle token must NOT match its stadium form.")
            .That(DifferingPixels(stadium, token)).IsStrictlyGreaterThan(0);

        Check.WithCustomMessage(
                "Coverage should sit on the inscribed ellipse's pi/4, confirming an ellipse "
                + "rather than a stadium. Counted at the 50% alpha cutoff.")
            .That(CoveredFraction(token)).IsCloseTo(Math.PI / 4, 0.01);
    }

    [Fact]
    public void CloseButton_IsSquare()
    {
        // QuickActionsGridWindow's CloseButton is the intended consumer of the square-only
        // circle token — it's 44x44 today. The token coupling itself isn't asserted here
        // because the site still holds a literal CornerRadius="22" rather than the
        // CircleCornerRadius resource; that assertion arrives with the migration (Task 8).
        // For now this only pins the precondition that assertion will depend on: the button
        // stays square.
        var path = Path.Combine(SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App",
            "Overlays", "QuickActionsGridWindow.xaml");
        Check.WithCustomMessage($"QuickActionsGridWindow.xaml not found at {path}.")
            .That(File.Exists(path)).IsTrue();
        var xaml = File.ReadAllText(path);

        var match = Regex.Match(
            xaml, @"x:Name=""CloseButton""[\s\S]{0,400}?Width=""(?<w>[\d.]+)""\s+Height=""(?<h>[\d.]+)""");

        Check.WithCustomMessage("Could not find CloseButton's Width and Height in the XAML.")
            .That(match.Success).IsTrue();
        Check.WithCustomMessage(
                "CloseButton must stay square, or CornerRadiusCircle renders an ellipse on it.")
            .That(match.Groups["w"].Value).IsEqualTo(match.Groups["h"].Value);
    }
}
