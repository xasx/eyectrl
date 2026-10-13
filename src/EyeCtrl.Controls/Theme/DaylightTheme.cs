namespace EyeCtrl.Controls.Theme;

using System.Windows;
using System.Windows.Media;

/// <summary>
/// The light, bright-room alternative — spec §10.8.
///
/// The axis the three dark themes leave open: Midnight, Ember and Contrast all
/// assume a dim room, and over a sunlit desktop their dark overlays read as
/// heavy voids. Daylight inverts the premise — near-white surfaces, near-black
/// text — so every contrast relation flips rather than shifts: the accent is
/// the DARKEST saturated thing on the screen instead of the brightest, and the
/// halo that makes transparent-overlay marks backdrop-independent is white
/// instead of near-black (gate 7 holds either way; the guarantee is about the
/// accent/halo EDGE, not its polarity).
///
/// The inversion is not free. Two tier orderings the dark themes take for
/// granted are forced to change shape, and both are documented at the tokens
/// that carry them: the semantic-triad lightness ladder inverts (§10.8.1) and
/// the modal decline button takes its label from a measured pairing rather
/// than the fixed dark-surface one (ModalButtonPalette). The opaque surface
/// core #E7E8F0 is Midnight's #1A1A2E on the same hue axis, at L* 92.1.
/// </summary>
public sealed class DaylightTheme : ThemeBase
{
    /// <summary>The single shared instance. Themes are immutable.</summary>
    public static readonly DaylightTheme Instance = new();

    private DaylightTheme()
    {
    }

    /// <inheritdoc />
    public override string Name => "Daylight";

    // Elevation lightens: the translucent base is the darkest layer and fully
    // opaque white is the top of the stack — the dark themes' ramp, mirrored.
    /// <inheritdoc />
    public override Color SurfaceDark => Color.FromArgb(0xFA, 0xE7, 0xE8, 0xF0);
    /// <inheritdoc />
    public override Color SurfaceMedium => Color.FromArgb(0xFA, 0xF3, 0xF4, 0xF9);
    /// <inheritdoc />
    public override Color SurfaceLight => Color.FromRgb(0xFF, 0xFF, 0xFF);
    /// <inheritdoc />
    public override Color SurfaceKey => Color.FromRgb(0xF0, 0xF1, 0xF7);
    /// <inheritdoc />
    public override Color SurfaceKeyModifier => Color.FromRgb(0xCF, 0xDE, 0xF8);
    /// <inheritdoc />
    public override Color SurfaceKeySwitch => Color.FromRgb(0xE9, 0xD5, 0xEF);
    /// <summary>Held modifier — the strongest of the five light ramp steps, kept steel-blue and far lighter than the deep-teal accent so gate 4's reservation holds.</summary>
    public override Color SurfaceKeyActive => Color.FromRgb(0x9F, 0xBD, 0xE6);
    /// <inheritdoc />
    public override Color SurfacePrediction => Color.FromRgb(0xD3, 0xED, 0xD8);
    /// <inheritdoc />
    public override Color BorderSubtle => Color.FromRgb(0x66, 0x7A, 0x9F);
    /// <inheritdoc />
    public override Color BorderFocus => Color.FromRgb(0x07, 0x34, 0x3E);
    /// <summary>
    /// Darker than the near-black the dark themes need on their side: the accent is
    /// near-black in lightness here, and only a very deep ink keeps dE 15 from it (gate 4).
    /// </summary>
    public override Color TextPrimary => Color.FromRgb(0x0C, 0x0D, 0x12);
    /// <summary>
    /// Darkened in review round 1 (was #5A6076, dE 1.9 from TextDim): the near-twin
    /// pair was a choice, not a constraint — here the accent reservation still holds
    /// (dE 17.2 of the 15 floor) and the two text tiers separate by dE 8.2.
    /// </summary>
    public override Color TextSecondary => Color.FromRgb(0x48, 0x4D, 0x5E);
    /// <inheritdoc />
    public override Color TextDim => Color.FromRgb(0x5E, 0x64, 0x77);

    /// <summary>
    /// Muted violet, same band every theme lands in: cyan, green, amber and red are
    /// spent, and against this theme's low-chroma accent a richer violet tests CLOSER
    /// (CIEDE2000's cross-term cancels chroma difference), not further.
    /// </summary>
    public override Color MarkerMove => Color.FromRgb(0x63, 0x4C, 0x7D);

    /// <summary>
    /// Deep teal — Midnight's cyan inverted. Darker than instinct suggests because
    /// protanopia is the binding vision type on a near-white core (gate 3: 10.49 of 9.5;
    /// every step lighter loses it).
    /// </summary>
    public override Color Accent => Color.FromRgb(0x07, 0x34, 0x3E);
    /// <summary>White, not near-black: dark marks get a light halo (§10.8).</summary>
    public override Color StrokeHalo => Color.FromRgb(0xFF, 0xFF, 0xFF);

    // The triad ladder INVERTS relative to the dark themes — see §10.8.1. Success and
    // warning are both capped at L* ~38 by the >7:1-against-white-halo bar (colour-picker
    // presets, cue badge), and a green below L* ~31.8 cannot hold chroma C* 55, so the
    // warning — a dark BRICK RED, which keeps its chroma in the dark — takes the bottom
    // rung. Not ochre despite the first draft's wording: hue 41°, thirteen degrees from
    // the danger red.
    /// <inheritdoc />
    public override Color AccentSuccess => Color.FromRgb(0x00, 0x62, 0x0C);
    /// <inheritdoc />
    public override Color AccentWarning => Color.FromRgb(0x7A, 0x10, 0x00);
    /// <inheritdoc />
    public override Color AccentDanger => Color.FromRgb(0xC5, 0x31, 0x3B);

    /// <inheritdoc />
    public override Color ToggleOn => Color.FromRgb(0x1F, 0x6B, 0x3E);
    /// <summary>Recessed, not raised: darker than the base it sits on, as on every theme.</summary>
    public override Color ToggleOff => Color.FromRgb(0xD8, 0xDA, 0xE4);
    /// <inheritdoc />
    public override Color ModalBackdrop => Color.FromArgb(0xB3, 0x00, 0x00, 0x00);

    /// <inheritdoc />
    public override double BorderThicknessStandard => 1.0;

    // ── Geometry and typography: identical in all four themes (spec §5 decision 6).

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
