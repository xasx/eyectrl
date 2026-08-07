namespace EyeCtrl.Controls.Theme;

using System.Windows;
using System.Windows.Media;

/// <summary>
/// The shipping palette — docs/design/overlay-unification-spec.md §4.1, measured
/// in §9. Cool charcoal surfaces, cyan accent.
///
/// Alpha appears only where the token composites over live desktop content; 0xFA
/// (98%) bounds worst-case contrast drift to -5% (§4.1.2).
/// </summary>
public sealed class MidnightTheme : ThemeBase
{
    /// <summary>The single shared instance. Themes are immutable.</summary>
    public static readonly MidnightTheme Instance = new();

    private MidnightTheme()
    {
    }

    /// <inheritdoc />
    public override string Name => "Midnight";

    /// <inheritdoc />
    public override Color SurfaceDark => Color.FromArgb(0xFA, 0x1A, 0x1A, 0x2E);
    /// <inheritdoc />
    public override Color SurfaceMedium => Color.FromArgb(0xFA, 0x2A, 0x2A, 0x44);
    /// <inheritdoc />
    public override Color SurfaceLight => Color.FromRgb(0x3A, 0x3A, 0x5E);
    /// <inheritdoc />
    public override Color SurfaceKey => Color.FromRgb(0x2A, 0x2A, 0x3E);
    /// <inheritdoc />
    public override Color SurfaceKeyModifier => Color.FromRgb(0x3E, 0x3E, 0x64);
    /// <inheritdoc />
    public override Color SurfaceKeySwitch => Color.FromRgb(0x38, 0x52, 0x7F);
    /// <inheritdoc />
    public override Color SurfaceKeyActive => Color.FromRgb(0x52, 0x78, 0xB8);
    /// <inheritdoc />
    public override Color SurfacePrediction => Color.FromRgb(0x2F, 0x53, 0x40);
    /// <inheritdoc />
    public override Color BorderSubtle => Color.FromRgb(0x61, 0x6B, 0xA0);
    /// <inheritdoc />
    public override Color BorderFocus => Color.FromRgb(0xA5, 0xF3, 0xFC);
    /// <inheritdoc />
    public override Color TextPrimary => Color.FromRgb(0xD6, 0xDE, 0xF5);
    /// <inheritdoc />
    public override Color TextSecondary => Color.FromRgb(0x9A, 0xA4, 0xC8);
    /// <inheritdoc />
    public override Color TextDim => Color.FromRgb(0x86, 0x8F, 0xB5);
    /// <inheritdoc />
    public override Color MarkerMove => Color.FromRgb(0xA8, 0xA0, 0xD0);
    /// <inheritdoc />
    public override Color Accent => Color.FromRgb(0xA5, 0xF3, 0xFC);
    /// <inheritdoc />
    public override Color StrokeHalo => Color.FromRgb(0x0A, 0x0A, 0x14);
    /// <inheritdoc />
    public override Color AccentSuccess => Color.FromRgb(0x7E, 0xE7, 0x87);
    /// <inheritdoc />
    public override Color AccentWarning => Color.FromRgb(0xE8, 0xA3, 0x3C);
    /// <inheritdoc />
    public override Color AccentDanger => Color.FromRgb(0xFF, 0x5C, 0x7A);
    /// <inheritdoc />
    public override Color ToggleOn => Color.FromRgb(0x4A, 0x8F, 0x5C);
    /// <inheritdoc />
    public override Color ToggleOff => Color.FromRgb(0x2E, 0x2E, 0x42);
    /// <inheritdoc />
    public override Color ModalBackdrop => Color.FromArgb(0xB3, 0x00, 0x00, 0x00);

    /// <inheritdoc />
    public override double BorderThicknessStandard => 1.0;

    // ── Geometry and typography: identical in all three themes (spec §5 decision 6).
    // Theme-varying by mechanism so a future revision can diverge without touching a
    // single overlay; no measurement today says any theme wants different geometry.

    /// <inheritdoc />
    public override double CornerRadiusSmall => 4.0;
    /// <inheritdoc />
    public override double CornerRadiusStandard => 8.0;
    /// <inheritdoc />
    public override double CornerRadiusLarge => 12.0;
    /// <inheritdoc />
    public override double CornerRadiusCircle => 9999.0;

    /// <inheritdoc />
    public override string FontFamilyStandard => "Segoe UI Variable Text, Segoe UI";
    /// <inheritdoc />
    public override FontWeight FontWeightMedium => FontWeights.SemiBold;
    /// <inheritdoc />
    public override FontWeight FontWeightStrong => FontWeights.Bold;

    /// <inheritdoc />
    public override double FontSizeBody => 12.0;
    /// <inheritdoc />
    public override double FontSizeBodyLarge => 14.0;
    /// <inheritdoc />
    public override double FontSizeSubtitle => 18.0;
    /// <inheritdoc />
    public override double FontSizeTitle => 24.0;
    /// <inheritdoc />
    public override double FontSizeDisplay => 34.0;
}
