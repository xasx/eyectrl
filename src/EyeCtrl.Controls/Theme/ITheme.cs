namespace EyeCtrl.Controls.Theme;

using System.Windows;
using System.Windows.Media;

/// <summary>
/// One complete palette. See docs/design/overlay-unification-spec.md §4.1
/// (Midnight), §10.2 (Ember) and §10.7 (Contrast); tools/palette_validate.py
/// runs ten gates against all three and must exit 0 after any edit.
///
/// Carries more than colour on purpose: <see cref="BorderThicknessStandard"/> is
/// theme-varying (§4.4), so the interface is non-colour-capable from the start
/// rather than being widened later.
///
/// Implementations must be immutable and their brushes frozen — overlays redraw
/// at gaze rate and share them across windows and threads.
/// </summary>
public interface ITheme
{
    /// <summary>Stable identifier, persisted in settings and shown in the picker.</summary>
    string Name { get; }

    // ── Surfaces ────────────────────────────────────────────────────────────
    /// <summary>Base overlay surface. Alpha where it composites over the desktop (§4.1.2).</summary>
    Color SurfaceDark { get; }
    /// <summary>Second layer: cards, button discs.</summary>
    Color SurfaceMedium { get; }
    /// <summary>Elevated surfaces — fully opaque (§4.1.1 rule 2).</summary>
    Color SurfaceLight { get; }
    /// <summary>Normal keyboard key.</summary>
    Color SurfaceKey { get; }
    /// <summary>Modifier key at rest.</summary>
    Color SurfaceKeyModifier { get; }
    /// <summary>Layout-switch key.</summary>
    Color SurfaceKeySwitch { get; }
    /// <summary>Held modifier — the 5th, brightest key-surface step (gate 8 ramp).</summary>
    Color SurfaceKeyActive { get; }
    /// <summary>Word-prediction bar.</summary>
    Color SurfacePrediction { get; }

    // ── Text and borders ────────────────────────────────────────────────────
    /// <summary>Container borders. Carries the layer boundary (gate 1).</summary>
    Color BorderSubtle { get; }
    /// <summary>Focus ring. Equals <see cref="Accent"/> in every theme (§4.1.2).</summary>
    Color BorderFocus { get; }
    /// <summary>Body text.</summary>
    Color TextPrimary { get; }
    /// <summary>Secondary text.</summary>
    Color TextSecondary { get; }
    /// <summary>De-emphasised text.</summary>
    Color TextDim { get; }
    /// <summary>Note Move-state marker — distinct from the accent and from body text (gate 10).</summary>
    Color MarkerMove { get; }

    // ── Accent and semantics ────────────────────────────────────────────────
    /// <summary>Reserved: means gaze dwell in progress and nothing else (§4.1.2).</summary>
    Color Accent { get; }
    /// <summary>Outline drawn under/around marks on transparent overlays (§4.1.3).</summary>
    Color StrokeHalo { get; }
    /// <summary>Success. Shared between Midnight and Ember; Contrast differs (§10.7).</summary>
    Color AccentSuccess { get; }
    /// <summary>Warning.</summary>
    Color AccentWarning { get; }
    /// <summary>Danger.</summary>
    Color AccentDanger { get; }
    /// <summary>Toggle in the on state.</summary>
    Color ToggleOn { get; }
    /// <summary>Toggle in the off state.</summary>
    Color ToggleOff { get; }
    /// <summary>Scrim behind modal dialogs.</summary>
    Color ModalBackdrop { get; }

    // ── Brushes ─────────────────────────────────────────────────────────────
    /// <summary>Frozen brush over <see cref="SurfaceDark"/>.</summary>
    SolidColorBrush BrushSurfaceDark { get; }
    /// <summary>Frozen brush over <see cref="SurfaceMedium"/>.</summary>
    SolidColorBrush BrushSurfaceMedium { get; }
    /// <summary>Frozen brush over <see cref="SurfaceLight"/>.</summary>
    SolidColorBrush BrushSurfaceLight { get; }
    /// <summary>Frozen brush over <see cref="SurfaceKey"/>.</summary>
    SolidColorBrush BrushSurfaceKey { get; }
    /// <summary>Frozen brush over <see cref="SurfaceKeyModifier"/>.</summary>
    SolidColorBrush BrushSurfaceKeyModifier { get; }
    /// <summary>Frozen brush over <see cref="SurfaceKeySwitch"/>.</summary>
    SolidColorBrush BrushSurfaceKeySwitch { get; }
    /// <summary>Frozen brush over <see cref="SurfaceKeyActive"/>.</summary>
    SolidColorBrush BrushSurfaceKeyActive { get; }
    /// <summary>Frozen brush over <see cref="SurfacePrediction"/>.</summary>
    SolidColorBrush BrushSurfacePrediction { get; }
    /// <summary>Frozen brush over <see cref="BorderSubtle"/>.</summary>
    SolidColorBrush BrushBorderSubtle { get; }
    /// <summary>Frozen brush over <see cref="BorderFocus"/>.</summary>
    SolidColorBrush BrushBorderFocus { get; }
    /// <summary>Frozen brush over <see cref="TextPrimary"/>.</summary>
    SolidColorBrush BrushTextPrimary { get; }
    /// <summary>Frozen brush over <see cref="TextSecondary"/>.</summary>
    SolidColorBrush BrushTextSecondary { get; }
    /// <summary>Frozen brush over <see cref="TextDim"/>.</summary>
    SolidColorBrush BrushTextDim { get; }
    /// <summary>Frozen brush over <see cref="MarkerMove"/>.</summary>
    SolidColorBrush BrushMarkerMove { get; }
    /// <summary>Frozen brush over <see cref="Accent"/>.</summary>
    SolidColorBrush BrushAccent { get; }
    /// <summary>Frozen brush over <see cref="StrokeHalo"/>.</summary>
    SolidColorBrush BrushStrokeHalo { get; }
    /// <summary>Frozen brush over <see cref="AccentSuccess"/>.</summary>
    SolidColorBrush BrushAccentSuccess { get; }
    /// <summary>Frozen brush over <see cref="AccentWarning"/>.</summary>
    SolidColorBrush BrushAccentWarning { get; }
    /// <summary>Frozen brush over <see cref="AccentDanger"/>.</summary>
    SolidColorBrush BrushAccentDanger { get; }
    /// <summary>Frozen brush over <see cref="ToggleOn"/>.</summary>
    SolidColorBrush BrushToggleOn { get; }
    /// <summary>Frozen brush over <see cref="ToggleOff"/>.</summary>
    SolidColorBrush BrushToggleOff { get; }
    /// <summary>Frozen brush over <see cref="ModalBackdrop"/>.</summary>
    SolidColorBrush BrushModalBackdrop { get; }

    /// <summary>
    /// <see cref="Accent"/> at 10% opacity — the translucent dwell-progress fill on
    /// <c>DwellFeedbackOverlay</c>. Derived, not a palette colour: there is no matching
    /// <c>Color</c> member, because XAML has no syntax for "this brush at N% alpha".
    /// </summary>
    SolidColorBrush BrushAccentDim10 { get; }

    /// <summary>
    /// The first non-colour theme-varying token (§4.4); see the geometry and
    /// typography blocks below. Midnight and Ember use 1; Contrast uses 2, because
    /// high-contrast structure is carried by the border rather than by fill steps.
    /// </summary>
    double BorderThicknessStandard { get; }

    // ── Geometry ────────────────────────────────────────────────────────────
    /// <summary>Compact bars and thin outlines: the toolbar, the dwell-feedback bar.</summary>
    double CornerRadiusSmall { get; }
    /// <summary>The default: overlay container roots, cards, keys, tiles, dwell controls.</summary>
    double CornerRadiusStandard { get; }
    /// <summary>Prominent cards and modal dialogs: splash, the settings card.</summary>
    double CornerRadiusLarge { get; }
    /// <summary>
    /// Fully round. **Valid on square elements only.** WPF clamps an over-large
    /// CornerRadius to an inscribed *ellipse*, so on a non-square Border this produces
    /// an ellipse, never a stadium — measured in spec §4.1 and pinned by
    /// CornerRadiusClampTests. On a square element it renders pixel-identically to
    /// size / 2 at every size and DPI tested.
    /// </summary>
    double CornerRadiusCircle { get; }

    // ── Typography ──────────────────────────────────────────────────────────
    /// <summary>
    /// Family name, or a comma-separated fallback chain. A chain is deliberate: an
    /// unresolvable bare name has an undefined degradation path (spec §4.2).
    /// </summary>
    string FontFamilyStandard { get; }
    /// <summary>Emphasis weight. SemiBold (600) — *not* FontWeights.Medium, which is 500.</summary>
    FontWeight FontWeightMedium { get; }
    /// <summary>Strong weight. Bold (700).</summary>
    FontWeight FontWeightStrong { get; }

    /// <summary>The ramp floor. Captions, body text, list rows. Gated at >= 12 (§9).</summary>
    double FontSizeBody { get; }
    /// <summary>Slightly emphasised body: tile labels, secondary rows.</summary>
    double FontSizeBodyLarge { get; }
    /// <summary>Section and notice text.</summary>
    double FontSizeSubtitle { get; }
    /// <summary>Headings and dwell-control labels.</summary>
    double FontSizeTitle { get; }
    /// <summary>The largest step: splash and window titles.</summary>
    double FontSizeDisplay { get; }

    // ── Derived (implemented by ThemeBase; see §6.2 and §6.6) ───────────────
    /// <summary><see cref="FontFamilyStandard"/> as a cached WPF object.</summary>
    FontFamily FontStandard { get; }
}
