namespace EyeCtrl.Controls.Theme;

using System.Windows.Media;

/// <summary>
/// The situational high-contrast theme — spec §10.7. For glare, a bright room or
/// tired eyes; not an all-day palette, since pure white at 21:1 for hours is what
/// Midnight's TextPrimary deliberately avoids.
///
/// Structurally distinct rather than a re-hue: gate 1 is carried entirely by a 21:1
/// border instead of fill steps, gate 5 passes on lightness alone (#00FF00 and
/// #FFFF00 collapse to ΔE 2.7 under protanopia, so §9.4's glyph rule is *more*
/// load-bearing here), and gate 9 caps danger at #FF7777. Surfaces are necessarily
/// opaque: translucency and a contrast guarantee are incompatible.
///
/// The triad is NOT shared with Midnight and Ember — no high-contrast palette can
/// use #FF5C7A and still be high-contrast.
/// </summary>
public sealed class ContrastTheme : ThemeBase
{
    /// <summary>The single shared instance. Themes are immutable.</summary>
    public static readonly ContrastTheme Instance = new();

    private ContrastTheme()
    {
    }

    /// <inheritdoc />
    public override string Name => "Contrast";

    /// <inheritdoc />
    public override Color SurfaceDark => Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
    /// <inheritdoc />
    public override Color SurfaceMedium => Color.FromArgb(0xFF, 0x14, 0x14, 0x14);
    /// <inheritdoc />
    public override Color SurfaceLight => Color.FromRgb(0x26, 0x26, 0x26);
    /// <inheritdoc />
    public override Color SurfaceKey => Color.FromRgb(0x0F, 0x0F, 0x0F);

    /// <summary>
    /// Tinted, not outlined: an achromatic key ramp failed gate 8 at ΔE 6.1 because
    /// near-blacks compress badly, so the modifier carries a low-luminance blue.
    /// </summary>
    public override Color SurfaceKeyModifier => Color.FromRgb(0x12, 0x3A, 0x63);

    /// <inheritdoc />
    public override Color SurfaceKeySwitch => Color.FromRgb(0x4A, 0x4A, 0x4A);
    /// <inheritdoc />
    public override Color SurfaceKeyActive => Color.FromRgb(0xFF, 0x88, 0x00);
    /// <summary>Low-luminance green tint, for the same gate-8 reason as the modifier.</summary>
    public override Color SurfacePrediction => Color.FromRgb(0x00, 0x4D, 0x22);

    /// <summary>Pure white at 21:1 — in this theme structure is carried by the border.</summary>
    public override Color BorderSubtle => Color.FromRgb(0xFF, 0xFF, 0xFF);
    /// <inheritdoc />
    public override Color BorderFocus => Color.FromRgb(0x00, 0xFF, 0xFF);
    /// <inheritdoc />
    public override Color TextPrimary => Color.FromRgb(0xFF, 0xFF, 0xFF);
    /// <inheritdoc />
    public override Color TextSecondary => Color.FromRgb(0xE8, 0xE8, 0xE8);
    /// <inheritdoc />
    public override Color TextDim => Color.FromRgb(0xC8, 0xC8, 0xC8);

    /// <summary>
    /// Violet, not white. #FFFFFF was the original value, but BorderSubtle and
    /// TextPrimary are both already #FFFFFF here, so it collapsed three meanings
    /// into one colour and gate 10 rejected it.
    /// </summary>
    public override Color MarkerMove => Color.FromRgb(0xAA, 0x80, 0xFF);

    /// <summary>
    /// Fully saturated, not a brighter #66FFFF: white is a structural colour in this
    /// theme, and the paler cyan sits only ΔE 24.6 from BorderSubtle, failing gate 4.
    /// </summary>
    public override Color Accent => Color.FromRgb(0x00, 0xFF, 0xFF);

    /// <inheritdoc />
    public override Color StrokeHalo => Color.FromRgb(0x00, 0x00, 0x00);
    /// <inheritdoc />
    public override Color AccentSuccess => Color.FromRgb(0x00, 0xFF, 0x00);
    /// <inheritdoc />
    public override Color AccentWarning => Color.FromRgb(0xFF, 0xFF, 0x00);

    /// <summary>
    /// Red is inherently dark: #FF0000 is only 5.25:1 on black, and pushing past
    /// 10:1 drops chroma under the gate-9 floor (#FF9999 is C* 41.5). This is the
    /// ceiling for "high contrast and still reads red" — 8.16:1 at C* 57.6, still
    /// the strongest AccentDanger of the three themes.
    /// </summary>
    public override Color AccentDanger => Color.FromRgb(0xFF, 0x77, 0x77);

    /// <inheritdoc />
    public override Color ToggleOn => Color.FromRgb(0x00, 0xFF, 0x00);
    /// <inheritdoc />
    public override Color ToggleOff => Color.FromRgb(0x00, 0x00, 0x00);
    /// <inheritdoc />
    public override Color ModalBackdrop => Color.FromArgb(0xE6, 0x00, 0x00, 0x00);

    /// <summary>
    /// 2, not 1 — the one non-colour token a theme overrides (§4.4). High-contrast
    /// structure is carried by the border rather than by fill steps.
    /// </summary>
    public override double BorderThicknessStandard => 2.0;
}
