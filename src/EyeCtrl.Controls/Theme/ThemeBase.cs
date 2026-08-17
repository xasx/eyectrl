namespace EyeCtrl.Controls.Theme;

using System.Threading;
using System.Windows;
using System.Windows.Media;

/// <summary>
/// Derives every brush in <see cref="ITheme"/> from that theme's colours, and the
/// <see cref="ITheme.FontStandard"/> object from its family string, so a concrete theme
/// declares 22 colours, one thickness, and 12 geometry/typography values, and nothing else.
///
/// Brushes are built on first access rather than in the constructor: a constructor would
/// have to call the abstract colour getters before the derived type is fully constructed,
/// which is exactly the default(Color) hazard ThemeShapeTests guards against. The lazy
/// form has no such ordering hazard.
///
/// Publication is atomic (<see cref="CachedBrush"/>, <see cref="CachedFont"/>) rather
/// than a plain <c>??=</c>. <see cref="Tokens"/> exposes one theme process-wide, so a
/// race must still hand every caller the *same* instance: with <c>??=</c> two threads
/// could each build a brush and one would be handed out without ever being stored,
/// leaving callers holding two equal-but-distinct brushes. That is observable — the
/// settings label mapping is pinned by reference identity — and it flaked under xUnit's
/// parallel execution. A losing thread's brush is simply dropped.
/// </summary>
public abstract class ThemeBase : ITheme
{
    /// <summary>
    /// Returns <paramref name="slot"/>'s brush, building and publishing a frozen brush
    /// over <paramref name="colour"/> on first access. Safe to call concurrently: every
    /// caller receives the one published instance.
    /// </summary>
    private static SolidColorBrush CachedBrush(ref SolidColorBrush? slot, Color colour, double opacity = 1.0)
    {
        var existing = Volatile.Read(ref slot);
        if (existing is not null)
            return existing;

        var created = new SolidColorBrush(colour) { Opacity = opacity };
        created.Freeze();       // shared across overlays that redraw at gaze rate

        // CompareExchange returns the slot's PREVIOUS value: null when we won the race,
        // the winner's brush when we lost. Either way the result is what every other
        // caller will see from here on.
        return Interlocked.CompareExchange(ref slot, created, null) ?? created;
    }

    /// <summary>
    /// The <see cref="FontFamily"/> counterpart of <see cref="CachedBrush"/>. A FontFamily
    /// cannot be frozen, so it is treated as read-only once published.
    /// </summary>
    private static FontFamily CachedFont(ref FontFamily? slot, string family)
    {
        var existing = Volatile.Read(ref slot);
        if (existing is not null)
            return existing;

        var created = new FontFamily(family);
        return Interlocked.CompareExchange(ref slot, created, null) ?? created;
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
    public SolidColorBrush BrushSurfaceDark => CachedBrush(ref _surfaceDark, SurfaceDark);
    /// <inheritdoc />
    public SolidColorBrush BrushSurfaceMedium => CachedBrush(ref _surfaceMedium, SurfaceMedium);
    /// <inheritdoc />
    public SolidColorBrush BrushSurfaceLight => CachedBrush(ref _surfaceLight, SurfaceLight);
    /// <inheritdoc />
    public SolidColorBrush BrushSurfaceKey => CachedBrush(ref _surfaceKey, SurfaceKey);
    /// <inheritdoc />
    public SolidColorBrush BrushSurfaceKeyModifier => CachedBrush(ref _surfaceKeyModifier, SurfaceKeyModifier);
    /// <inheritdoc />
    public SolidColorBrush BrushSurfaceKeySwitch => CachedBrush(ref _surfaceKeySwitch, SurfaceKeySwitch);
    /// <inheritdoc />
    public SolidColorBrush BrushSurfaceKeyActive => CachedBrush(ref _surfaceKeyActive, SurfaceKeyActive);
    /// <inheritdoc />
    public SolidColorBrush BrushSurfacePrediction => CachedBrush(ref _surfacePrediction, SurfacePrediction);
    /// <inheritdoc />
    public SolidColorBrush BrushBorderSubtle => CachedBrush(ref _borderSubtle, BorderSubtle);
    /// <inheritdoc />
    public SolidColorBrush BrushBorderFocus => CachedBrush(ref _borderFocus, BorderFocus);
    /// <inheritdoc />
    public SolidColorBrush BrushTextPrimary => CachedBrush(ref _textPrimary, TextPrimary);
    /// <inheritdoc />
    public SolidColorBrush BrushTextSecondary => CachedBrush(ref _textSecondary, TextSecondary);
    /// <inheritdoc />
    public SolidColorBrush BrushTextDim => CachedBrush(ref _textDim, TextDim);
    /// <inheritdoc />
    public SolidColorBrush BrushMarkerMove => CachedBrush(ref _markerMove, MarkerMove);
    /// <inheritdoc />
    public SolidColorBrush BrushAccent => CachedBrush(ref _accent, Accent);
    /// <inheritdoc />
    public SolidColorBrush BrushStrokeHalo => CachedBrush(ref _strokeHalo, StrokeHalo);
    /// <inheritdoc />
    public SolidColorBrush BrushAccentSuccess => CachedBrush(ref _accentSuccess, AccentSuccess);
    /// <inheritdoc />
    public SolidColorBrush BrushAccentWarning => CachedBrush(ref _accentWarning, AccentWarning);
    /// <inheritdoc />
    public SolidColorBrush BrushAccentDanger => CachedBrush(ref _accentDanger, AccentDanger);
    /// <inheritdoc />
    public SolidColorBrush BrushToggleOn => CachedBrush(ref _toggleOn, ToggleOn);
    /// <inheritdoc />
    public SolidColorBrush BrushToggleOff => CachedBrush(ref _toggleOff, ToggleOff);
    /// <inheritdoc />
    public SolidColorBrush BrushModalBackdrop => CachedBrush(ref _modalBackdrop, ModalBackdrop);
    /// <inheritdoc />
    public SolidColorBrush BrushAccentDim10 => CachedBrush(ref _accentDim10, Accent, 0.10);

    /// <inheritdoc />
    public FontFamily FontStandard => CachedFont(ref _fontStandard, FontFamilyStandard);

    /// <inheritdoc />
    public override string ToString() => Name;
}
