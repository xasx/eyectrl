namespace EyeCtrl.App.Tests;

using System.Linq;
using System.Windows;
using System.Windows.Media;
using EyeCtrl.App.Overlays;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Guards the one assignment that gives the whole application its font (design spec §8.8).
/// Every window class in EyeCtrl.App derives from OverlayWindow, so the base constructor is
/// the single point of control — no OverrideMetadata, which is process-wide and single-shot.
///
/// Not in the "WpfApplication" collection and needing no <c>Application</c>: OverlayWindow has
/// no XAML, so nothing here resolves a resource key, and the constructor reads the static
/// <c>Tokens</c> rather than <c>Application.Current.Resources</c> — which is precisely the
/// property spec §8.8 chose the mechanism for.
///
/// <c>ThemeStartupTests</c> mutates the process-global <c>Tokens.Current</c> concurrently, so a
/// theme swap could in principle land between constructing the window and reading the token.
/// It cannot make this flake, because <c>ThemeShapeTests</c> pins <c>FontFamilyStandard</c> to
/// the same chain for every registered theme — do not remove that pin thinking it redundant.
/// </summary>
public class OverlayFontTests
{
    [StaFact]
    public void OverlayWindow_TakesItsFontFamilyFromTheToken()
    {
        // Constructed, never shown: OverlayWindow's Win32 interop runs in
        // OnSourceInitialized, which needs an HWND and therefore a Show().
        var window = new OverlayWindow();
        Check.That(window.FontFamily.Source).IsEqualTo(Tokens.FontFamilyStandard);
    }

    [StaFact]
    public void TheAssignedFont_DiffersFromWPFsDefault()
    {
        // The failure mode of a font token is being a silent no-op. If the token happened to
        // equal the inherited default, this suite would pass while proving nothing.
        var window = new OverlayWindow();
        Check.WithCustomMessage(
                "The font token matches WPF's inherited default, so the assignment in "
                + "OverlayWindow's constructor is unobservable and this guard is vacuous.")
            .That(window.FontFamily.Source)
            .IsNotEqualTo(SystemFonts.MessageFontFamily.Source);
    }

    [StaFact]
    public void TheFontChain_ResolvesToARealGlyphTypeface()
    {
        // Closes the gap spec §4.2 measured but nothing guarded: a typo in the chain string
        // does not throw. WPF composites a fallback matching no single installed face, so the
        // app would render the *undefined* degradation path while the token looked deliberate.
        // A resolvable chain is the assertion; WHICH member resolves is deliberately not,
        // because falling back from Segoe UI Variable Text to Segoe UI is the chain working.
        var typeface = new Typeface(Tokens.FontStandard, FontStyles.Normal,
            FontWeights.Normal, FontStretches.Normal);

        Check.WithCustomMessage(
                $"The font chain '{Tokens.FontFamilyStandard}' resolves to no glyph typeface. "
                + "Check every family name in it for typos — an unresolvable chain silently "
                + "renders WPF's composite fallback (design spec §4.2).")
            .That(typeface.TryGetGlyphTypeface(out _)).IsTrue();
    }

    [Fact]
    public void NoWindowClass_BypassesOverlayWindow()
    {
        // §8.8's mechanism assumes universal inheritance. A Window subclass that skips
        // OverlayWindow would silently keep the system font.
        var strays = typeof(OverlayWindow).Assembly.GetTypes()
            .Where(t => typeof(Window).IsAssignableFrom(t)
                     && t != typeof(Window)
                     && !typeof(OverlayWindow).IsAssignableFrom(t))
            .Select(t => t.FullName!)
            .ToList();

        Check.WithCustomMessage(
                "These Window subclasses do not derive from OverlayWindow, so they miss the "
                + "app-wide font: " + string.Join(", ", strays))
            .That(strays).IsEmpty();
    }
}
