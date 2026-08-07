namespace EyeCtrl.Controls.Theme;

using System.Windows;
using System.Windows.Media;

/// <summary>
/// The static face of the active <see cref="ITheme"/>. Every colour, brush and
/// thickness in the overlay UI is read through here — 131 C# call sites and, via
/// OverlayStyles.xaml, 106 XAML lookups.
///
/// Deliberately static rather than an injected service: EyeCtrl.Controls.Tests and
/// EyeCtrl.Core.Tests run without a WPF Application, so a resource lookup would
/// NullReferenceException, and XAML's {x:Static} needs a static member either way.
/// Members are static *properties*, not fields, so activating a theme is visible to
/// every existing call site without touching one of them.
///
/// Theme changes recreate windows rather than re-resolving live, which is why
/// {StaticResource} throughout remains correct. <see cref="Activate"/> is called
/// once, from App.OnStartup, before any overlay exists.
/// </summary>
public static class Tokens
{
    /// <summary>
    /// The active theme. Defaults to Midnight so tests and any code that runs
    /// before startup see the shipping palette rather than null.
    /// </summary>
    public static ITheme Current { get; private set; } = MidnightTheme.Instance;

    /// <summary>
    /// Makes <paramref name="theme"/> active. Call once, during startup, on the UI
    /// thread and before any overlay window or dwell control is constructed —
    /// afterwards the value is only ever read, so cross-thread reads are safe
    /// (colours are structs, brushes are frozen, and <see cref="ITheme.FontStandard"/>
    /// is a shared <see cref="FontFamily"/>, which cannot be frozen — treat it as
    /// read-only).
    /// </summary>
    /// <param name="theme">The theme to activate.</param>
    public static void Activate(ITheme theme) => Current = theme;

    // ── Surfaces ────────────────────────────────────────────────────────────
    /// <inheritdoc cref="ITheme.SurfaceDark"/>
    public static Color SurfaceDark => Current.SurfaceDark;
    /// <inheritdoc cref="ITheme.SurfaceMedium"/>
    public static Color SurfaceMedium => Current.SurfaceMedium;
    /// <inheritdoc cref="ITheme.SurfaceLight"/>
    public static Color SurfaceLight => Current.SurfaceLight;
    /// <inheritdoc cref="ITheme.SurfaceKey"/>
    public static Color SurfaceKey => Current.SurfaceKey;
    /// <inheritdoc cref="ITheme.SurfaceKeyModifier"/>
    public static Color SurfaceKeyModifier => Current.SurfaceKeyModifier;
    /// <inheritdoc cref="ITheme.SurfaceKeySwitch"/>
    public static Color SurfaceKeySwitch => Current.SurfaceKeySwitch;
    /// <inheritdoc cref="ITheme.SurfaceKeyActive"/>
    public static Color SurfaceKeyActive => Current.SurfaceKeyActive;
    /// <inheritdoc cref="ITheme.SurfacePrediction"/>
    public static Color SurfacePrediction => Current.SurfacePrediction;

    // ── Text and borders ────────────────────────────────────────────────────
    /// <inheritdoc cref="ITheme.BorderSubtle"/>
    public static Color BorderSubtle => Current.BorderSubtle;
    /// <inheritdoc cref="ITheme.BorderFocus"/>
    public static Color BorderFocus => Current.BorderFocus;
    /// <inheritdoc cref="ITheme.TextPrimary"/>
    public static Color TextPrimary => Current.TextPrimary;
    /// <inheritdoc cref="ITheme.TextSecondary"/>
    public static Color TextSecondary => Current.TextSecondary;
    /// <inheritdoc cref="ITheme.TextDim"/>
    public static Color TextDim => Current.TextDim;
    /// <inheritdoc cref="ITheme.MarkerMove"/>
    public static Color MarkerMove => Current.MarkerMove;

    // ── Accent and semantics ────────────────────────────────────────────────
    /// <inheritdoc cref="ITheme.Accent"/>
    public static Color Accent => Current.Accent;
    /// <inheritdoc cref="ITheme.StrokeHalo"/>
    public static Color StrokeHalo => Current.StrokeHalo;
    /// <inheritdoc cref="ITheme.AccentSuccess"/>
    public static Color AccentSuccess => Current.AccentSuccess;
    /// <inheritdoc cref="ITheme.AccentWarning"/>
    public static Color AccentWarning => Current.AccentWarning;
    /// <inheritdoc cref="ITheme.AccentDanger"/>
    public static Color AccentDanger => Current.AccentDanger;
    /// <inheritdoc cref="ITheme.ToggleOn"/>
    public static Color ToggleOn => Current.ToggleOn;
    /// <inheritdoc cref="ITheme.ToggleOff"/>
    public static Color ToggleOff => Current.ToggleOff;
    /// <inheritdoc cref="ITheme.ModalBackdrop"/>
    public static Color ModalBackdrop => Current.ModalBackdrop;

    // ── Brushes ─────────────────────────────────────────────────────────────
    /// <inheritdoc cref="ITheme.BrushSurfaceDark"/>
    public static SolidColorBrush BrushSurfaceDark => Current.BrushSurfaceDark;
    /// <inheritdoc cref="ITheme.BrushSurfaceMedium"/>
    public static SolidColorBrush BrushSurfaceMedium => Current.BrushSurfaceMedium;
    /// <inheritdoc cref="ITheme.BrushSurfaceLight"/>
    public static SolidColorBrush BrushSurfaceLight => Current.BrushSurfaceLight;
    /// <inheritdoc cref="ITheme.BrushSurfaceKey"/>
    public static SolidColorBrush BrushSurfaceKey => Current.BrushSurfaceKey;
    /// <inheritdoc cref="ITheme.BrushSurfaceKeyModifier"/>
    public static SolidColorBrush BrushSurfaceKeyModifier => Current.BrushSurfaceKeyModifier;
    /// <inheritdoc cref="ITheme.BrushSurfaceKeySwitch"/>
    public static SolidColorBrush BrushSurfaceKeySwitch => Current.BrushSurfaceKeySwitch;
    /// <inheritdoc cref="ITheme.BrushSurfaceKeyActive"/>
    public static SolidColorBrush BrushSurfaceKeyActive => Current.BrushSurfaceKeyActive;
    /// <inheritdoc cref="ITheme.BrushSurfacePrediction"/>
    public static SolidColorBrush BrushSurfacePrediction => Current.BrushSurfacePrediction;
    /// <inheritdoc cref="ITheme.BrushBorderSubtle"/>
    public static SolidColorBrush BrushBorderSubtle => Current.BrushBorderSubtle;
    /// <inheritdoc cref="ITheme.BrushBorderFocus"/>
    public static SolidColorBrush BrushBorderFocus => Current.BrushBorderFocus;
    /// <inheritdoc cref="ITheme.BrushTextPrimary"/>
    public static SolidColorBrush BrushTextPrimary => Current.BrushTextPrimary;
    /// <inheritdoc cref="ITheme.BrushTextSecondary"/>
    public static SolidColorBrush BrushTextSecondary => Current.BrushTextSecondary;
    /// <inheritdoc cref="ITheme.BrushTextDim"/>
    public static SolidColorBrush BrushTextDim => Current.BrushTextDim;
    /// <inheritdoc cref="ITheme.BrushMarkerMove"/>
    public static SolidColorBrush BrushMarkerMove => Current.BrushMarkerMove;
    /// <inheritdoc cref="ITheme.BrushAccent"/>
    public static SolidColorBrush BrushAccent => Current.BrushAccent;
    /// <inheritdoc cref="ITheme.BrushStrokeHalo"/>
    public static SolidColorBrush BrushStrokeHalo => Current.BrushStrokeHalo;
    /// <inheritdoc cref="ITheme.BrushAccentSuccess"/>
    public static SolidColorBrush BrushAccentSuccess => Current.BrushAccentSuccess;
    /// <inheritdoc cref="ITheme.BrushAccentWarning"/>
    public static SolidColorBrush BrushAccentWarning => Current.BrushAccentWarning;
    /// <inheritdoc cref="ITheme.BrushAccentDanger"/>
    public static SolidColorBrush BrushAccentDanger => Current.BrushAccentDanger;
    /// <inheritdoc cref="ITheme.BrushToggleOn"/>
    public static SolidColorBrush BrushToggleOn => Current.BrushToggleOn;
    /// <inheritdoc cref="ITheme.BrushToggleOff"/>
    public static SolidColorBrush BrushToggleOff => Current.BrushToggleOff;
    /// <inheritdoc cref="ITheme.BrushModalBackdrop"/>
    public static SolidColorBrush BrushModalBackdrop => Current.BrushModalBackdrop;
    /// <inheritdoc cref="ITheme.BrushAccentDim10"/>
    public static SolidColorBrush BrushAccentDim10 => Current.BrushAccentDim10;

    /// <inheritdoc cref="ITheme.BorderThicknessStandard"/>
    public static double BorderThicknessStandard => Current.BorderThicknessStandard;

    /// <inheritdoc cref="ITheme.CornerRadiusSmall"/>
    public static double CornerRadiusSmall => Current.CornerRadiusSmall;
    /// <inheritdoc cref="ITheme.CornerRadiusStandard"/>
    public static double CornerRadiusStandard => Current.CornerRadiusStandard;
    /// <inheritdoc cref="ITheme.CornerRadiusLarge"/>
    public static double CornerRadiusLarge => Current.CornerRadiusLarge;
    /// <inheritdoc cref="ITheme.CornerRadiusCircle"/>
    public static double CornerRadiusCircle => Current.CornerRadiusCircle;

    /// <inheritdoc cref="ITheme.FontFamilyStandard"/>
    public static string FontFamilyStandard => Current.FontFamilyStandard;
    /// <inheritdoc cref="ITheme.FontStandard"/>
    public static FontFamily FontStandard => Current.FontStandard;
    /// <inheritdoc cref="ITheme.FontWeightMedium"/>
    public static FontWeight FontWeightMedium => Current.FontWeightMedium;
    /// <inheritdoc cref="ITheme.FontWeightStrong"/>
    public static FontWeight FontWeightStrong => Current.FontWeightStrong;

    /// <inheritdoc cref="ITheme.FontSizeBody"/>
    public static double FontSizeBody => Current.FontSizeBody;
    /// <inheritdoc cref="ITheme.FontSizeBodyLarge"/>
    public static double FontSizeBodyLarge => Current.FontSizeBodyLarge;
    /// <inheritdoc cref="ITheme.FontSizeSubtitle"/>
    public static double FontSizeSubtitle => Current.FontSizeSubtitle;
    /// <inheritdoc cref="ITheme.FontSizeTitle"/>
    public static double FontSizeTitle => Current.FontSizeTitle;
    /// <inheritdoc cref="ITheme.FontSizeDisplay"/>
    public static double FontSizeDisplay => Current.FontSizeDisplay;
}
