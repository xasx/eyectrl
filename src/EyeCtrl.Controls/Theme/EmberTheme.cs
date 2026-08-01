namespace EyeCtrl.Controls.Theme;

using System.Windows.Media;

/// <summary>
/// The warm, low-blue-light alternative — spec §10.2, measured in §10.3.
///
/// Surfaces and text move onto the warm axis; the accent deliberately does not
/// (§10.5). The accent covers a few percent of lit pixels, so keeping it cool
/// costs almost nothing in short-wavelength exposure while freeing the warm axis
/// for the semantic triad — which is why that triad is shared with Midnight.
///
/// The opaque surface core #211B14 is matched to Midnight's #1A1A2E at L* 10.2,
/// so the two themes sit at the same perceived depth and only temperature changes.
/// </summary>
public sealed class EmberTheme : ThemeBase
{
    /// <summary>The single shared instance. Themes are immutable.</summary>
    public static readonly EmberTheme Instance = new();

    private EmberTheme()
    {
    }

    /// <inheritdoc />
    public override string Name => "Ember";

    /// <inheritdoc />
    public override Color SurfaceDark => Color.FromArgb(0xFA, 0x21, 0x1B, 0x14);
    /// <inheritdoc />
    public override Color SurfaceMedium => Color.FromArgb(0xFA, 0x34, 0x2B, 0x20);
    /// <inheritdoc />
    public override Color SurfaceLight => Color.FromRgb(0x47, 0x3A, 0x2C);
    /// <inheritdoc />
    public override Color SurfaceKey => Color.FromRgb(0x2E, 0x26, 0x19);
    /// <inheritdoc />
    public override Color SurfaceKeyModifier => Color.FromRgb(0x4E, 0x40, 0x29);
    /// <inheritdoc />
    public override Color SurfaceKeySwitch => Color.FromRgb(0x63, 0x38, 0x2A);
    /// <inheritdoc />
    public override Color SurfaceKeyActive => Color.FromRgb(0x9A, 0x68, 0x40);
    /// <inheritdoc />
    public override Color SurfacePrediction => Color.FromRgb(0x33, 0x42, 0x2A);
    /// <inheritdoc />
    public override Color BorderSubtle => Color.FromRgb(0x7A, 0x6C, 0x51);
    /// <inheritdoc />
    public override Color BorderFocus => Color.FromRgb(0x9F, 0xE8, 0xFF);
    /// <inheritdoc />
    public override Color TextPrimary => Color.FromRgb(0xF2, 0xEE, 0xE8);
    /// <inheritdoc />
    public override Color TextSecondary => Color.FromRgb(0xBF, 0xB4, 0xA2);
    /// <inheritdoc />
    public override Color TextDim => Color.FromRgb(0xA0, 0x91, 0x7C);

    /// <summary>
    /// Violet, not a warm tone: the warm axis is already spent on surfaces and text,
    /// so a warm marker sat only ΔE 11.4 from TextPrimary and failed gate 10.
    /// </summary>
    public override Color MarkerMove => Color.FromRgb(0xB6, 0xA3, 0xDC);

    /// <summary>Cool on warm — the deliberate exception to the theme's axis (§10.5).</summary>
    public override Color Accent => Color.FromRgb(0x9F, 0xE8, 0xFF);
    /// <inheritdoc />
    public override Color StrokeHalo => Color.FromRgb(0x0E, 0x0A, 0x06);

    /// <summary>Shared with Midnight: semantics encode outcome, not brand (§10.5).</summary>
    public override Color AccentSuccess => Color.FromRgb(0x7E, 0xE7, 0x87);
    /// <summary>Shared with Midnight (§10.5).</summary>
    public override Color AccentWarning => Color.FromRgb(0xE8, 0xA3, 0x3C);
    /// <summary>Shared with Midnight (§10.5).</summary>
    public override Color AccentDanger => Color.FromRgb(0xFF, 0x5C, 0x7A);

    /// <inheritdoc />
    public override Color ToggleOn => Color.FromRgb(0x67, 0x87, 0x4C);
    /// <inheritdoc />
    public override Color ToggleOff => Color.FromRgb(0x34, 0x2C, 0x21);
    /// <inheritdoc />
    public override Color ModalBackdrop => Color.FromArgb(0xB3, 0x00, 0x00, 0x00);

    /// <inheritdoc />
    public override double BorderThicknessStandard => 1.0;
}
