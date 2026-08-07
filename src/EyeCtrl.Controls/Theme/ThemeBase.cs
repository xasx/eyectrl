namespace EyeCtrl.Controls.Theme;

using System.Windows;
using System.Windows.Media;

/// <summary>
/// Derives every brush in <see cref="ITheme"/> from that theme's colours, so a
/// concrete theme declares 22 colours and one thickness and nothing else.
///
/// Brushes are built on first access rather than in the constructor: a constructor
/// would have to call the abstract colour getters before the derived type is fully
/// constructed. The lazy form has no such ordering hazard, and a race between two
/// threads is harmless — both produce equal frozen brushes over the same colour,
/// and nothing in the codebase compares brushes by reference.
/// </summary>
public abstract class ThemeBase : ITheme
{
    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();     // shared across overlays that redraw at gaze rate
        return b;
    }

    private static SolidColorBrush Dimmed(Color c, double opacity)
    {
        var b = new SolidColorBrush(c) { Opacity = opacity };
        b.Freeze();
        return b;
    }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract Color SurfaceDark { get; }
    /// <inheritdoc />
    public abstract Color SurfaceMedium { get; }
    /// <inheritdoc />
    public abstract Color SurfaceLight { get; }
    /// <inheritdoc />
    public abstract Color SurfaceKey { get; }
    /// <inheritdoc />
    public abstract Color SurfaceKeyModifier { get; }
    /// <inheritdoc />
    public abstract Color SurfaceKeySwitch { get; }
    /// <inheritdoc />
    public abstract Color SurfaceKeyActive { get; }
    /// <inheritdoc />
    public abstract Color SurfacePrediction { get; }
    /// <inheritdoc />
    public abstract Color BorderSubtle { get; }
    /// <inheritdoc />
    public abstract Color BorderFocus { get; }
    /// <inheritdoc />
    public abstract Color TextPrimary { get; }
    /// <inheritdoc />
    public abstract Color TextSecondary { get; }
    /// <inheritdoc />
    public abstract Color TextDim { get; }
    /// <inheritdoc />
    public abstract Color MarkerMove { get; }
    /// <inheritdoc />
    public abstract Color Accent { get; }
    /// <inheritdoc />
    public abstract Color StrokeHalo { get; }
    /// <inheritdoc />
    public abstract Color AccentSuccess { get; }
    /// <inheritdoc />
    public abstract Color AccentWarning { get; }
    /// <inheritdoc />
    public abstract Color AccentDanger { get; }
    /// <inheritdoc />
    public abstract Color ToggleOn { get; }
    /// <inheritdoc />
    public abstract Color ToggleOff { get; }
    /// <inheritdoc />
    public abstract Color ModalBackdrop { get; }

    /// <inheritdoc />
    public abstract double BorderThicknessStandard { get; }

    /// <inheritdoc />
    public abstract double CornerRadiusSmall { get; }
    /// <inheritdoc />
    public abstract double CornerRadiusStandard { get; }
    /// <inheritdoc />
    public abstract double CornerRadiusLarge { get; }
    /// <inheritdoc />
    public abstract double CornerRadiusCircle { get; }

    /// <inheritdoc />
    public abstract string FontFamilyStandard { get; }
    /// <inheritdoc />
    public abstract FontWeight FontWeightMedium { get; }
    /// <inheritdoc />
    public abstract FontWeight FontWeightStrong { get; }

    /// <inheritdoc />
    public abstract double FontSizeBody { get; }
    /// <inheritdoc />
    public abstract double FontSizeBodyLarge { get; }
    /// <inheritdoc />
    public abstract double FontSizeSubtitle { get; }
    /// <inheritdoc />
    public abstract double FontSizeTitle { get; }
    /// <inheritdoc />
    public abstract double FontSizeDisplay { get; }

    private SolidColorBrush? _surfaceDark;
    private SolidColorBrush? _surfaceMedium;
    private SolidColorBrush? _surfaceLight;
    private SolidColorBrush? _surfaceKey;
    private SolidColorBrush? _surfaceKeyModifier;
    private SolidColorBrush? _surfaceKeySwitch;
    private SolidColorBrush? _surfaceKeyActive;
    private SolidColorBrush? _surfacePrediction;
    private SolidColorBrush? _borderSubtle;
    private SolidColorBrush? _borderFocus;
    private SolidColorBrush? _textPrimary;
    private SolidColorBrush? _textSecondary;
    private SolidColorBrush? _textDim;
    private SolidColorBrush? _markerMove;
    private SolidColorBrush? _accent;
    private SolidColorBrush? _strokeHalo;
    private SolidColorBrush? _accentSuccess;
    private SolidColorBrush? _accentWarning;
    private SolidColorBrush? _accentDanger;
    private SolidColorBrush? _toggleOn;
    private SolidColorBrush? _toggleOff;
    private SolidColorBrush? _modalBackdrop;
    private SolidColorBrush? _accentDim10;
    private FontFamily? _fontStandard;

    /// <inheritdoc />
    public SolidColorBrush BrushSurfaceDark => _surfaceDark ??= Frozen(SurfaceDark);
    /// <inheritdoc />
    public SolidColorBrush BrushSurfaceMedium => _surfaceMedium ??= Frozen(SurfaceMedium);
    /// <inheritdoc />
    public SolidColorBrush BrushSurfaceLight => _surfaceLight ??= Frozen(SurfaceLight);
    /// <inheritdoc />
    public SolidColorBrush BrushSurfaceKey => _surfaceKey ??= Frozen(SurfaceKey);
    /// <inheritdoc />
    public SolidColorBrush BrushSurfaceKeyModifier => _surfaceKeyModifier ??= Frozen(SurfaceKeyModifier);
    /// <inheritdoc />
    public SolidColorBrush BrushSurfaceKeySwitch => _surfaceKeySwitch ??= Frozen(SurfaceKeySwitch);
    /// <inheritdoc />
    public SolidColorBrush BrushSurfaceKeyActive => _surfaceKeyActive ??= Frozen(SurfaceKeyActive);
    /// <inheritdoc />
    public SolidColorBrush BrushSurfacePrediction => _surfacePrediction ??= Frozen(SurfacePrediction);
    /// <inheritdoc />
    public SolidColorBrush BrushBorderSubtle => _borderSubtle ??= Frozen(BorderSubtle);
    /// <inheritdoc />
    public SolidColorBrush BrushBorderFocus => _borderFocus ??= Frozen(BorderFocus);
    /// <inheritdoc />
    public SolidColorBrush BrushTextPrimary => _textPrimary ??= Frozen(TextPrimary);
    /// <inheritdoc />
    public SolidColorBrush BrushTextSecondary => _textSecondary ??= Frozen(TextSecondary);
    /// <inheritdoc />
    public SolidColorBrush BrushTextDim => _textDim ??= Frozen(TextDim);
    /// <inheritdoc />
    public SolidColorBrush BrushMarkerMove => _markerMove ??= Frozen(MarkerMove);
    /// <inheritdoc />
    public SolidColorBrush BrushAccent => _accent ??= Frozen(Accent);
    /// <inheritdoc />
    public SolidColorBrush BrushStrokeHalo => _strokeHalo ??= Frozen(StrokeHalo);
    /// <inheritdoc />
    public SolidColorBrush BrushAccentSuccess => _accentSuccess ??= Frozen(AccentSuccess);
    /// <inheritdoc />
    public SolidColorBrush BrushAccentWarning => _accentWarning ??= Frozen(AccentWarning);
    /// <inheritdoc />
    public SolidColorBrush BrushAccentDanger => _accentDanger ??= Frozen(AccentDanger);
    /// <inheritdoc />
    public SolidColorBrush BrushToggleOn => _toggleOn ??= Frozen(ToggleOn);
    /// <inheritdoc />
    public SolidColorBrush BrushToggleOff => _toggleOff ??= Frozen(ToggleOff);
    /// <inheritdoc />
    public SolidColorBrush BrushModalBackdrop => _modalBackdrop ??= Frozen(ModalBackdrop);
    /// <inheritdoc />
    public SolidColorBrush BrushAccentDim10 => _accentDim10 ??= Dimmed(Accent, 0.10);

    /// <inheritdoc />
    public FontFamily FontStandard => _fontStandard ??= new FontFamily(FontFamilyStandard);

    /// <inheritdoc />
    public override string ToString() => Name;
}
