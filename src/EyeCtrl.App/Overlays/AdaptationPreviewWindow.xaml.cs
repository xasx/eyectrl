namespace EyeCtrl.App.Overlays;

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using EyeCtrl.App.Services;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

/// <summary>
/// The adaptation preview panel (design §3.4): a non-interactive, click-through card anchored
/// beyond Reset's outer end of the toolbar strip (§3.3) that shows the four knob rows the pure
/// <see cref="AdaptationPreviewSnapshot"/> projects. It carries no dwell targets and no
/// hit-testing — a dismissal-only surface that cannot trap gaze — and is
/// <see cref="OverlayWindow.ExcludeFromScreenCapture"/>=true so the magnifier / action-menu grabs
/// never bake it in. <see cref="AdaptationPreviewService"/> owns every placement decision (P10);
/// this window converts between physical px (the seam's coordinate system) and DIP at its live DPI,
/// and reports its bounds back through <see cref="IPreviewPanel.BoundsChanged"/>.
/// <para>
/// Constructed ONLY on the first adaptation enable (the activation participant's lazy resolve, P9)
/// — never at startup while the feature is off. <see cref="ConstructionCount"/> is the seam the
/// composition test asserts that with.
/// </para>
/// </summary>
public partial class AdaptationPreviewWindow : OverlayWindow, IPreviewPanel
{
    /// <summary>Number of window constructions this process — the P9 composition assertion's
    /// observable ("resolving + starting the DI graph with adaptation disabled constructs none").</summary>
    internal static int ConstructionCount;


    private const double TrackWidthDip = 110;
    private const double TrackHeightDip = 6;

    private StackPanel? _rowsHost;
    private TextBlock? _nothingActiveLine;
    private TextBlock? _footer;

    /// <summary>Creates the preview window (click-through + excluded from screen capture, set
    /// before init — the <c>CueOverlayWindow</c> ctor pattern).</summary>
    public AdaptationPreviewWindow()
    {
        ConstructionCount++;
        IsClickThrough = true;
        ExcludeFromScreenCapture = true;
        InitializeComponent();
        // SizeToContent re-measures on every content refresh: keep the card inside the work area
        // (a grown panel must not run off the docked edge) and report the new bounds (P2: Rect on
        // every size change).
        SizeChanged += (_, _) =>
        {
            ClampIntoWorkArea();
            PublishBounds();
        };
    }

    /// <inheritdoc />
    public event Action<PhysicalRect?>? BoundsChanged;

    /// <inheritdoc />
    public void ShowPanel(AdaptationPreviewSnapshot snapshot, PhysicalPoint positionPhysical, string dockedEdge)
    {
        BuildContent(snapshot, dockedEdge);
        Left = DpiHelper.PhysicalToLogicalX(positionPhysical.X);
        Top = DpiHelper.PhysicalToLogicalY(positionPhysical.Y);
        ClampIntoWorkArea();
        if (!IsVisible)
        {
            Show();
            RaiseToTop();
        }
        PublishBounds();
    }

    /// <inheritdoc />
    public void HidePanel()
    {
        if (IsVisible)
            Hide();
        // Null = "bounds gone". The service decides whether that is a Rect, a keep-last or a
        // NoPanel situation (P2) — never the window.
        BoundsChanged?.Invoke(null);
    }

    /// <summary>The right-hand value column for a row (§3.4): the effective absolute against its
    /// baseline where the knob has one, the band fill with its direction word where it does not.
    /// Pure and headless-testable.</summary>
    internal static string ValueTextFor(StringTable strings, AdaptationPreviewRow row) => row.Knob switch
    {
        AdaptationKnob.DwellTime => row.TotalFill <= 0.0
            ? strings.T("overlays.adaptationPreview.direction.atBaseline")
            : strings.T("overlays.adaptationPreview.value.band",
                Math.Round(row.TotalFill * 100),
                strings.T(row.TighteningDirection ? "overlays.adaptationPreview.direction.longer" : "overlays.adaptationPreview.direction.shorter")),
        // Direct mode composes no parameter (both value columns null): "at baseline" is exactly
        // true there — effective equals baseline with no parameter in play.
        AdaptationKnob.Smoothing when row.AbsoluteValue is null => strings.T("overlays.adaptationPreview.direction.atBaseline"),
        AdaptationKnob.Smoothing => strings.T("overlays.adaptationPreview.value.absolute",
            row.AbsoluteValue!.Value, row.BaselineValue!.Value,
            strings.T(row.IsHzUnit ? "overlays.adaptationPreview.unit.hz" : "overlays.adaptationPreview.unit.factor")),
        AdaptationKnob.FixationRadius => strings.T("overlays.adaptationPreview.value.absolute",
            row.AbsoluteValue ?? 0.0, row.BaselineValue ?? 0.0, strings.T("overlays.adaptationPreview.unit.px")),
        AdaptationKnob.TargetGeometry => row.TotalFill <= 0.0
            ? strings.T("overlays.adaptationPreview.direction.atBaseline")
            : strings.T("overlays.adaptationPreview.value.band", Math.Round(row.TotalFill * 100), strings.T("overlays.adaptationPreview.direction.bigger")),
        _ => strings.T("overlays.adaptationPreview.direction.atBaseline"),
    };

    /// <summary>Builds the whole card from the snapshot. Rows are code-built (the ToolbarBand
    /// pattern): the row set is dynamic projection data, not fixed XAML.</summary>
    internal void BuildContent(AdaptationPreviewSnapshot snapshot, string dockedEdge)
    {
        var strings = StringTable.Current;
        RootStack.Children.Clear();
        _rowsHost = null;
        _nothingActiveLine = null;
        _footer = null;

        RootStack.Children.Add(Caption(strings.T("overlays.adaptationPreview.title"), subtitle: false, emphasize: true));
        RootStack.Children.Add(Caption(strings.T("overlays.adaptationPreview.subtitle"), subtitle: true, emphasize: false));

        _rowsHost = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var row in snapshot.Rows)
            _rowsHost.Children.Add(RowFor(strings, row));
        RootStack.Children.Add(_rowsHost);

        // §3.4: at baseline the four flat rows stay AND the muted line appears — a stable panel
        // shape, so the nothing-active flip costs no resize.
        _nothingActiveLine = new TextBlock
        {
            Text = strings.T("overlays.adaptationPreview.nothingActive"),
            Foreground = Tokens.BrushTextDim,
            FontSize = Tokens.FontSizeBody,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = snapshot.AnythingActive ? Visibility.Collapsed : Visibility.Visible,
        };
        RootStack.Children.Add(_nothingActiveLine);

        RootStack.Children.Add(new Border
        {
            Height = 1,
            Background = Tokens.BrushBorderSubtle,
            Margin = new Thickness(0, 10, 0, 6),
        });
        _footer = new TextBlock
        {
            Text = strings.T("overlays.adaptationPreview.footer"),
            Foreground = Tokens.BrushTextDim,
            FontSize = Tokens.FontSizeBody,
            TextWrapping = TextWrapping.Wrap,
        };
        RootStack.Children.Add(_footer);

        OrientTick(dockedEdge);
    }

    /// <summary>The connector tick (§3.3, optional): a 2-px rule on the panel edge facing Reset's
    /// end of the strip — the left edge for a horizontal strip, the top edge for a vertical one.</summary>
    private void OrientTick(string dockedEdge)
    {
        bool verticalStrip = string.Equals(dockedEdge, "Left", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(dockedEdge, "Right", StringComparison.OrdinalIgnoreCase);
        if (verticalStrip)
        {
            Tick.Width = 14;
            Tick.Height = 2;
            Tick.HorizontalAlignment = HorizontalAlignment.Left;
            Tick.VerticalAlignment = VerticalAlignment.Top;
            Tick.Margin = new Thickness(0, -1, 0, 0);
        }
        else
        {
            Tick.Width = 2;
            Tick.Height = 14;
            Tick.HorizontalAlignment = HorizontalAlignment.Left;
            Tick.VerticalAlignment = VerticalAlignment.Center;
            Tick.Margin = new Thickness(-1, 0, 0, 0);
        }
    }

    private static TextBlock Caption(string text, bool subtitle, bool emphasize)
    {
        var block = new TextBlock
        {
            Text = text,
            // Token-backed at the assignment sites (the geometry gate reads each assignment's own
            // expression; a parameter name would reference no token).
            FontSize = subtitle ? Tokens.FontSizeBody : Tokens.FontSizeSubtitle,
            Foreground = subtitle ? Tokens.BrushTextDim : Tokens.BrushTextPrimary,
            TextWrapping = TextWrapping.Wrap,
        };
        if (emphasize)
            block.FontWeight = Tokens.FontWeightStrong;
        return block;
    }

    /// <summary>An all-zero row (an empty track): what the fit harness's <see cref="RowForTest"/>
    /// lays the probe texts over — the fill shades draw no width, so the geometry is the row's.</summary>
    private static readonly AdaptationPreviewRow EmptyRow =
        new(AdaptationKnob.FixationRadius, 0.0, 0.0, TighteningDirection: false, null, null, false);

    private static UIElement RowFor(StringTable strings, AdaptationPreviewRow row) =>
        BuildRow(LabelFor(strings, row.Knob), ValueTextFor(strings, row), row);

    /// <summary>One row's grid (§3.4): knob label left, fill bar centre, right-aligned value right.
    /// The production path composes both texts from the snapshot; the fit harness injects its own
    /// through <see cref="RowForTest"/>.</summary>
    private static UIElement BuildRow(string labelText, string valueText, AdaptationPreviewRow row)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = labelText,
            FontSize = Tokens.FontSizeBody,
            Foreground = Tokens.BrushTextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var bar = Bar(row);
        bar.VerticalAlignment = VerticalAlignment.Center;
        bar.Margin = new Thickness(10, 0, 10, 0);
        Grid.SetColumn(bar, 1);
        grid.Children.Add(bar);

        var value = new TextBlock
        {
            Text = valueText,
            FontSize = Tokens.FontSizeBody,
            Foreground = Tokens.BrushTextDim,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right, // §3.4: right-aligned values
        };
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);

        return grid;
    }

    /// <summary>The fit harness's seam (class PV1): one production row with the label and value
    /// texts injected at the data level — A68's <c>with { Label = text }</c> pattern — over an
    /// empty track. Measured by tests/EyeCtrl.App.Tests/Localization/Fit.</summary>
    internal static Grid RowForTest(string labelText, string valueText) =>
        (Grid)BuildRow(labelText, valueText, EmptyRow);

    private static string LabelFor(StringTable strings, AdaptationKnob knob) => knob switch
    {
        AdaptationKnob.DwellTime => strings.T("overlays.adaptationPreview.knob.dwell"),
        AdaptationKnob.FixationRadius => strings.T("overlays.adaptationPreview.knob.tolerance"),
        AdaptationKnob.Smoothing => strings.T("overlays.adaptationPreview.knob.smoothing"),
        AdaptationKnob.TargetGeometry => strings.T("overlays.adaptationPreview.knob.geometry"),
        _ => string.Empty,
    };

    /// <summary>
    /// The two-segment fill bar (§3.4): the clamped composed total, with the learned portion shaded
    /// dim inside it. Dwell is bidirectional — shorter left of centre, longer right — and the
    /// learned shade appears on the forgiving side only (learned offsets are shorter-ward by
    /// construction; a tightening-dominant row carries none).
    /// </summary>
    private static FrameworkElement Bar(AdaptationPreviewRow row)
    {
        double total = Math.Clamp(row.TotalFill, 0.0, 1.0);
        double learned = Math.Clamp(row.LearnedFill, 0.0, total);

        var host = new Grid { Width = TrackWidthDip, Height = TrackHeightDip };
        host.Children.Add(new Border
        {
            Background = Tokens.BrushSurfaceMedium,
            CornerRadius = Tokens.SmallCornerRadius,
        });

        if (row.Knob == AdaptationKnob.DwellTime)
        {
            host.Children.Add(new Border
            {
                Width = 1,
                Background = Tokens.BrushBorderSubtle,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            if (total <= 0.0)
                return host;
            if (row.TighteningDirection)
            {
                host.Children.Add(Segment(TrackWidthDip / 2 * total, HorizontalAlignment.Right,
                    Tokens.BrushAccentWarning));
            }
            else
            {
                host.Children.Add(Segment(TrackWidthDip / 2 * total, HorizontalAlignment.Left, Tokens.BrushAccent));
                if (learned > 0.0)
                    host.Children.Add(Segment(TrackWidthDip / 2 * learned, HorizontalAlignment.Left, Tokens.BrushTextDim));
            }
            return host;
        }

        if (total > 0.0)
        {
            host.Children.Add(Segment(TrackWidthDip * total, HorizontalAlignment.Left, Tokens.BrushAccent));
            if (learned > 0.0)
                host.Children.Add(Segment(TrackWidthDip * learned, HorizontalAlignment.Left, Tokens.BrushTextDim));
        }
        return host;
    }

    private static Border Segment(double widthDip, HorizontalAlignment alignment, Brush fill) => new()
    {
        Width = Math.Max(widthDip, 0.0),
        Background = fill,
        CornerRadius = Tokens.SmallCornerRadius,
        HorizontalAlignment = alignment,
    };

    /// <summary>Keeps the card inside the work area after a placement or a SizeToContent re-measure.
    /// The pre-layout call clamps only what is measurable; the SizeChanged re-clamp covers the rest.</summary>
    private void ClampIntoWorkArea()
    {
        var area = SystemParameters.WorkArea;
        if (Left < area.Left)
            Left = area.Left;
        if (Top < area.Top)
            Top = area.Top;
        if (ActualWidth > 0 && Left + ActualWidth > area.Right)
            Left = area.Right - ActualWidth;
        if (ActualHeight > 0 && Top + ActualHeight > area.Bottom)
            Top = area.Bottom - ActualHeight;
    }

    private void PublishBounds()
    {
        // Before the first layout pass there is nothing true to report; the SizeChanged publish
        // lands within the same dispatcher cycle and well inside the VM's 500 ms budget (D14).
        if (!IsVisible || ActualWidth <= 0 || ActualHeight <= 0)
            return;
        BoundsChanged?.Invoke(new PhysicalRect(
            DpiHelper.LogicalToPhysicalX(Left),
            DpiHelper.LogicalToPhysicalY(Top),
            DpiHelper.LogicalToPhysicalX(ActualWidth),
            DpiHelper.LogicalToPhysicalY(ActualHeight)));
    }

    // Test seams (construction-without-showing): the STA tests drive BuildContent directly and read
    // the built structure back without a shown window / HWND.
    internal void BuildContentForTest(AdaptationPreviewSnapshot snapshot, string dockedEdge) =>
        BuildContent(snapshot, dockedEdge);

    internal int RowItemCountForTest() => _rowsHost?.Children.Count ?? -1;

    internal bool NothingActiveIsVisibleForTest() =>
        _nothingActiveLine?.Visibility == Visibility.Visible;

    internal string FooterTextForTest() => _footer?.Text ?? string.Empty;
}
