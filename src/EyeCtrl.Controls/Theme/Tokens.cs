namespace EyeCtrl.Controls.Theme;

using System.Windows.Media;

/// <summary>
/// The single source of truth for every palette colour in the EyeCtrl overlay UI.
/// See docs/design/overlay-unification-spec.md §4.1 for values and §4.1.1 for the
/// ten design rules they satisfy; tools/palette_validate.py enforces those rules
/// and must exit 0 after any edit here.
///
/// Deliberately plain C# statics rather than Application.Current.Resources
/// lookups: EyeCtrl.Controls.Tests and EyeCtrl.Core.Tests run without a WPF
/// Application, and a resource lookup would NullReferenceException there.
///
/// This class holds the Midnight palette. Ember and Contrast are specified in
/// §10 but not implemented; adding them means introducing an ITheme indirection
/// here, which will NOT require changing any {StaticResource} consumer because
/// the chosen theme-change model recreates windows rather than re-resolving live.
/// </summary>
public static class Tokens
{
    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();     // shared across overlays that redraw at gaze rate
        return b;
    }

    /// <summary>A frozen brush over <paramref name="c"/> at the given <paramref name="opacity"/> (0-1).</summary>
    private static SolidColorBrush Dimmed(Color c, double opacity)
    {
        var b = new SolidColorBrush(c) { Opacity = opacity };
        b.Freeze();
        return b;
    }

    // ── Surfaces ────────────────────────────────────────────────────────────
    // Alpha only where the token composites over live desktop content. 0xFA
    // (98%) bounds the worst-case contrast drift to -5%; §4.1.2.
    public static readonly Color SurfaceDark = Color.FromArgb(0xFA, 0x1A, 0x1A, 0x2E);
    public static readonly Color SurfaceMedium = Color.FromArgb(0xFA, 0x2A, 0x2A, 0x44);
    /// <summary>Elevated surfaces are fully opaque — §4.1.1 rule 2.</summary>
    public static readonly Color SurfaceLight = Color.FromRgb(0x3A, 0x3A, 0x5E);
    public static readonly Color SurfaceKey = Color.FromRgb(0x2A, 0x2A, 0x3E);
    public static readonly Color SurfaceKeyModifier = Color.FromRgb(0x3E, 0x3E, 0x64);
    public static readonly Color SurfaceKeySwitch = Color.FromRgb(0x38, 0x52, 0x7F);
    /// <summary>Active modifier (held Shift/Ctrl) — a 5th, brighter key-surface step (gate 8 ramp).</summary>
    public static readonly Color SurfaceKeyActive = Color.FromRgb(0x52, 0x78, 0xB8);
    public static readonly Color SurfacePrediction = Color.FromRgb(0x2F, 0x53, 0x40);

    // ── Text and borders ────────────────────────────────────────────────────
    public static readonly Color BorderSubtle = Color.FromRgb(0x61, 0x6B, 0xA0);
    /// <summary>Equals <see cref="Accent"/>: selection is carried by fill, not hue (§4.1.2).</summary>
    public static readonly Color BorderFocus = Color.FromRgb(0xA5, 0xF3, 0xFC);
    public static readonly Color TextPrimary = Color.FromRgb(0xD6, 0xDE, 0xF5);
    public static readonly Color TextSecondary = Color.FromRgb(0x9A, 0xA4, 0xC8);
    public static readonly Color TextDim = Color.FromRgb(0x86, 0x8F, 0xB5);
    /// <summary>Marker for the note Move state — distinct from the cyan Accent (Edit) and visible on surfaces (§9).</summary>
    public static readonly Color MarkerMove = Color.FromRgb(0xA8, 0xA0, 0xD0);

    // ── Accent and semantics ────────────────────────────────────────────────
    /// <summary>Reserved: means gaze dwell in progress and nothing else (§4.1.2).</summary>
    public static readonly Color Accent = Color.FromRgb(0xA5, 0xF3, 0xFC);
    /// <summary>Outline drawn under/around Accent on transparent overlays (§4.1.3, Plan B).</summary>
    public static readonly Color StrokeHalo = Color.FromRgb(0x0A, 0x0A, 0x14);
    public static readonly Color AccentSuccess = Color.FromRgb(0x7E, 0xE7, 0x87);
    public static readonly Color AccentWarning = Color.FromRgb(0xE8, 0xA3, 0x3C);
    public static readonly Color AccentDanger = Color.FromRgb(0xFF, 0x5C, 0x7A);
    public static readonly Color ToggleOn = Color.FromRgb(0x4A, 0x8F, 0x5C);
    public static readonly Color ToggleOff = Color.FromRgb(0x2E, 0x2E, 0x42);
    public static readonly Color ModalBackdrop = Color.FromArgb(0xB3, 0x00, 0x00, 0x00);

    // ── Brushes ─────────────────────────────────────────────────────────────
    public static readonly SolidColorBrush BrushSurfaceDark = Frozen(SurfaceDark);
    public static readonly SolidColorBrush BrushSurfaceMedium = Frozen(SurfaceMedium);
    public static readonly SolidColorBrush BrushSurfaceLight = Frozen(SurfaceLight);
    public static readonly SolidColorBrush BrushSurfaceKey = Frozen(SurfaceKey);
    public static readonly SolidColorBrush BrushSurfaceKeyModifier = Frozen(SurfaceKeyModifier);
    public static readonly SolidColorBrush BrushSurfaceKeySwitch = Frozen(SurfaceKeySwitch);
    public static readonly SolidColorBrush BrushSurfaceKeyActive = Frozen(SurfaceKeyActive);
    public static readonly SolidColorBrush BrushSurfacePrediction = Frozen(SurfacePrediction);
    public static readonly SolidColorBrush BrushBorderSubtle = Frozen(BorderSubtle);
    public static readonly SolidColorBrush BrushBorderFocus = Frozen(BorderFocus);
    public static readonly SolidColorBrush BrushTextPrimary = Frozen(TextPrimary);
    public static readonly SolidColorBrush BrushTextSecondary = Frozen(TextSecondary);
    public static readonly SolidColorBrush BrushTextDim = Frozen(TextDim);
    public static readonly SolidColorBrush BrushMarkerMove = Frozen(MarkerMove);
    public static readonly SolidColorBrush BrushAccent = Frozen(Accent);
    public static readonly SolidColorBrush BrushStrokeHalo = Frozen(StrokeHalo);
    public static readonly SolidColorBrush BrushAccentSuccess = Frozen(AccentSuccess);
    public static readonly SolidColorBrush BrushAccentWarning = Frozen(AccentWarning);
    public static readonly SolidColorBrush BrushAccentDanger = Frozen(AccentDanger);
    public static readonly SolidColorBrush BrushToggleOn = Frozen(ToggleOn);
    public static readonly SolidColorBrush BrushToggleOff = Frozen(ToggleOff);
    public static readonly SolidColorBrush BrushModalBackdrop = Frozen(ModalBackdrop);

    /// <summary>
    /// <see cref="Accent"/> at 10% opacity — the translucent dwell-progress fill used on
    /// <c>DwellFeedbackOverlay</c>. WPF XAML has no syntax for "this brush at N% alpha", so the
    /// derived brush lives here as a frozen token rather than a restated hex literal in XAML.
    /// Not a new palette colour (no matching <c>Color</c> field); derived from <see cref="Accent"/>.
    /// </summary>
    public static readonly SolidColorBrush BrushAccentDim10 = Dimmed(Accent, 0.10);

    /// <summary>
    /// The one non-colour theme-varying token (§4.4). Midnight and Ember use 1;
    /// Contrast uses 2, because high-contrast structure is carried by the border
    /// rather than by fill steps.
    ///
    /// Deliberately <c>static readonly</c> rather than <c>const</c>: {x:Static}
    /// cannot bind a const, and a const would force OverlayStyles.xaml to restate
    /// the literal — breaking the single-source-of-truth guarantee for the sake of
    /// one number.
    /// </summary>
    public static readonly double BorderThicknessStandard = 1.0;
}
