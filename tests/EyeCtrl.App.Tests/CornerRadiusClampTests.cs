namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
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
///
/// <see cref="TheOneCircleConsumer_IsSquare"/> constructs a window and therefore shares the WPF
/// <see cref="Application"/> singleton (and the collection serialising access to it) with
/// <see cref="OverlayResourceResolutionTests"/> — see <see cref="WpfApplicationGate"/>.
/// </summary>
[Collection("WpfApplication")]
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

    /// <summary>
    /// The squareness rule, enforced over EVERY consumer rather than the one this branch knew
    /// about. <see cref="TheOneCircleConsumer_IsSquare"/> below names <c>QuickActionsGridWindow</c>
    /// and <c>CloseButton</c> in its own source, so a SECOND consumer added tomorrow — on a
    /// 120x40 Border, say — inherits no guard at all and silently renders the ellipse the theory
    /// above proves it becomes.
    ///
    /// Source-scanned rather than constructed, deliberately. Constructing gives effective values
    /// and is the better instrument, which is why the named test keeps doing it; but it can only
    /// reach windows a test can instantiate, and the whole point here is to cover the consumer
    /// nobody has thought about yet. The two are complementary: this one is broad and shallow,
    /// that one narrow and exact.
    /// </summary>
    [StaFact]
    public void EveryCircleTokenConsumer_DeclaresASquare()
    {
        var wpf = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        var root = SourceScanner.RepoRoot().FullName;
        var offenders = new List<string>();
        var consumers = 0;

        foreach (var project in SourceScanner.ScannedProjects)
        {
            var dir = Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.xaml", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;

                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                var doc = XDocument.Parse(File.ReadAllText(file));

                // Matches both reference forms — {StaticResource CircleCornerRadius} and an
                // {x:Static} path ending in it — because either resolves to the same 9999.
                foreach (var element in doc.Descendants()
                             .Where(e => (string?)e.Attribute("CornerRadius") is { } v
                                         && v.Contains("CircleCornerRadius", StringComparison.Ordinal)))
                {
                    consumers++;
                    var w = (string?)element.Attribute("Width");
                    var h = (string?)element.Attribute("Height");
                    var name = (string?)element.Attribute(
                                   XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name")
                               ?? element.Name.LocalName;

                    if (w is null || h is null)
                        offenders.Add(
                            $"{relative}: <{element.Name.LocalName} {name}> takes CircleCornerRadius "
                            + $"but declares Width={w ?? "(none)"} Height={h ?? "(none)"} — an "
                            + "auto-sized element is almost never square");
                    else if (w != h)
                        offenders.Add(
                            $"{relative}: <{element.Name.LocalName} {name}> is {w}x{h}, not square");
                }
            }
        }

        // A scan that finds nothing proves nothing. If the last consumer really did go away, this
        // test has no subject left and should be deleted along with the token's square-only
        // restriction — not left passing vacuously.
        Check.WithCustomMessage(
                "No element in the scanned projects consumes CircleCornerRadius, so this guard is "
                + "checking an empty set. Either the reference form changed (it looks for "
                + "CornerRadius=\"...CircleCornerRadius...\") or the token lost its last consumer.")
            .That(consumers).IsStrictlyGreaterThan(0);

        Check.WithCustomMessage(
                "CircleCornerRadius is a SQUARE-ONLY token: WPF clamps an over-large radius to an "
                + "inscribed ELLIPSE, not a stadium (§4.1, and the theory above measures it). On a "
                + "non-square element it therefore renders an ellipse, which is never what the "
                + "author meant. Make the element square, or use LargeCornerRadius.\n  "
                + string.Join("\n  ", offenders))
            .That(offenders).IsEmpty();

        // Code-behind cannot be checked this way — a Border sized at runtime has no attribute to
        // read — so rather than let such a site pass unexamined, refuse it outright and say why.
        var codeSites = SourceScanner.ScannedFiles()
            .Where(f => f.Relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.Text.Contains("CircleCornerRadius", StringComparison.Ordinal))
            .Select(f => f.Relative)
            .Where(r => !r.Contains("/Theme/", StringComparison.Ordinal))
            .ToList();

        Check.WithCustomMessage(
                "CircleCornerRadius is now assigned from code-behind, where this guard cannot read "
                + "a declared size and so cannot tell whether the element is square. Either size "
                + "the element in XAML so the scan above covers it, or add a constructed test like "
                + "TheOneCircleConsumer_IsSquare that reads its effective Width and Height.\n  "
                + string.Join("\n  ", codeSites))
            .That(codeSites).IsEmpty();
    }

    [StaFact]
    public void TheOneCircleConsumer_IsSquare()
    {
        // QuickActionsGridWindow's CloseButton is the one consumer of the square-only circle
        // token. Task 8 replaced its literal CornerRadius="22" with the CircleCornerRadius
        // resource, so the coupling this test's name claims is now ASSERTED here rather than
        // deferred: the button must stay square AND must keep reading the token. Either half
        // alone guards nothing — a square button off the token gains nothing from the clamp
        // pinned above, and a non-square button on the token silently becomes an ellipse.
        //
        // Constructs the window instead of regex-parsing the XAML: that reads the EFFECTIVE
        // values, so it is immune to attribute reordering and also sees a radius or size
        // arriving from a Style. The Application-with-tokens setup is shared with
        // OverlayResourceResolutionTests, which constructs this same window — see
        // WpfApplicationGate.
        //
        // Nothing calls Show(). Constructing a Window parses its XAML, which is all this needs,
        // and this suite must never take over the screen of a user whose only input is gaze.
        WpfApplicationGate.EnsureApplicationWithTokens();

        var window = new QuickActionsGridWindow();
        try
        {
            var button = window.FindName("CloseButton") as Border;
            Check.WithCustomMessage(
                    "QuickActionsGridWindow no longer has a Border named CloseButton.")
                .That(button).IsNotNull();

            // Border.Width/Height default to NaN when unset, and NFluent's structural equality
            // routes through Equals, where double.NaN.Equals(double.NaN) is true — so the
            // IsEqualTo(button.Height) check two lines down would stay green even if
            // Width="44" Height="44" were deleted from the XAML entirely, leaving an
            // auto-sized (and therefore almost never square) Border. On such a Border,
            // CornerRadiusCircle still renders — just as a pill, not a circle, which is the
            // exact defect this test exists to catch. This assertion closes that gap.
            Check.WithCustomMessage(
                    "CloseButton has no explicit size, so it auto-sizes — and CircleCornerRadius on a "
                    + "non-square element renders a pill, not a circle.")
                .That(double.IsNaN(button!.Width)).IsFalse();

            Check.WithCustomMessage(
                    "CloseButton must stay square, or CornerRadiusCircle renders an ellipse on "
                    + "it rather than a circle — see §4.1 and the theory above. Measured "
                    + $"{button.Width}x{button.Height}.")
                .That(button.Width).IsEqualTo(button.Height);

            // Compared against Tokens rather than a concrete theme because
            // OverlayStyles.xaml declares the key as {x:Static theme:Tokens.CircleCornerRadius}
            // — this is the very member the resource is pulled from. Reading the mutable
            // Tokens.Current cannot destabilise it the way it could in the theory above: all
            // three themes declare CornerRadiusCircle = 9999, so whichever theme a concurrent
            // ThemeStartupTests has activated, the expected value is the same.
            Check.WithCustomMessage(
                    "CloseButton must read its radius from the CircleCornerRadius token, not a "
                    + "literal. Expected " + Tokens.CircleCornerRadius
                    + $" but found {button.CornerRadius}.")
                .That(button.CornerRadius).IsEqualTo(Tokens.CircleCornerRadius);
        }
        finally
        {
            window.Close();
        }
    }
}
