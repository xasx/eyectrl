namespace EyeCtrl.Setup.Display;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Localization;

/// <summary>
/// The display-only progress/result screen for update, rollback and uninstall: a band with the
/// verb's title, a body with the per-verb stage plan, and a terminal result that replaces the
/// progress (state edge, state glyph, confirming text). Owner decision Q13 made it an ordinary
/// window: it is not click-through (no <c>WS_EX_TRANSPARENT</c>/<c>WS_EX_LAYERED</c>), not
/// topmost, visible in the taskbar and may activate once when it appears. It has no targets and
/// closes itself — 10 s after a success, 60 s after a failure (Q12's degraded included). Closing
/// it early only ends the display; the install carries on.
/// </summary>
internal sealed class SetupProgressWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExTopmost = 0x00000008L;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExLayered = 0x00080000L;

    /// <summary>EyeCtrl's own icon, embedded from the App's single source file at build time, so
    /// the taskbar button is recognisable.</summary>
    internal const string IconResource =
        "pack://application:,,,/EyeCtrl.Setup;component/Assets/eyectrl.ico";

    private readonly StringTable _strings;
    private readonly SetupVerb _verb;
    private readonly Border _edge = null!;
    private readonly Border _band = null!;
    private readonly Border _bodyHost = null!;
    private readonly TextBlock _bandTitle = null!;
    private StackPanel _progressBody = null!;
    private StackPanel _resultBody = null!;
    private TextBlock _detail = null!;
    private TextBlock _resultText = null!;
    private System.Windows.Shapes.Path _resultIcon = null!;
    private System.Windows.Controls.Border _neutralDash = null!;
    private readonly List<StageRow> _stageRows = [];
    private readonly DispatcherTimer _closeTimer;
    private readonly Action _close;

    private sealed class StageRow
    {
        public SetupStage Stage = default;
        public System.Windows.Shapes.Path Glyph = null!;
        public TextBlock Label = null!;
        public bool Active;
    }

    /// <summary>Builds the window. Nothing is shown until the display calls <see cref="Show"/>.</summary>
    /// <param name="strings">The resolved table.</param>
    /// <param name="verb">The verb whose progress text is shown.</param>
    /// <param name="close">
    /// The close callback; tests pass a flag setter so no window is ever shown or closed. Production
    /// leaves it null and <see cref="Window.Close"/> is used.
    /// </param>
    /// <param name="workArea">
    /// The work area to lay the window into; tests pass the owner's 1920 x 1152 DIP so the
    /// per-monitor DPI pins do not depend on the machine running them. Production passes
    /// <see cref="SystemParameters.WorkArea"/>.
    /// </param>
    public SetupProgressWindow(StringTable strings, SetupVerb verb, Action? close = null, Rect? workArea = null)
    {
        _strings = strings;
        _verb = verb;
        _close = close ?? Close;

        var progressTitle = SetupDisplayText.ProgressTitle(strings, verb);
        Title = progressTitle;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        // Owner decision Q13: an ordinary window. It is not topmost, it has a taskbar button, and
        // it may activate once when it appears; the display calls Show() once and never re-forces it.
        Topmost = false;
        ShowInTaskbar = true;
        ShowActivated = true;
        AllowsTransparency = false;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // The r01 background stays: an ordinary (non-layered) window with the frozen SurfaceDark
        // colour, so the result text keeps its contrast in every theme (N1).
        Background = SetupBrushes.Opaque(Tokens.SurfaceDark);
        Foreground = Tokens.BrushTextPrimary;
        FontFamily = Tokens.FontStandard;
        FontSize = SetupWindowMetrics.ProseFontSize;
        UseLayoutRounding = true;
        Icon = LoadAppIcon();

        var bounds = SetupWindowMetrics.ProgressBounds(workArea ?? SystemParameters.WorkArea);
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;

        _edge = BuildEdge();
        _band = BuildBand(out _bandTitle);
        _bodyHost = BuildBodyHost();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // state edge
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // band
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_edge, 0);
        Grid.SetRow(_band, 1);
        Grid.SetRow(_bodyHost, 2);
        root.Children.Add(_edge);
        root.Children.Add(_band);
        root.Children.Add(_bodyHost);
        Content = root;
        _edge.Visibility = Visibility.Collapsed;

        _closeTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = SetupDisplayText.SuccessCloseDelay,
        };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            _close();
        };
        Closed += (_, _) => _closeTimer.Stop();
    }

    /// <summary>D9/r3 F3: the display-side per-verb plan. Uninstall plans one stage today (Stop —
    /// the elevated job stops and deletes in one pass); a new SetupStage member slots into this
    /// table. Any emitted stage outside the plan is appended by Stage().</summary>
    internal static IReadOnlyList<SetupStage> PlanFor(SetupVerb verb) => verb switch
    {
        SetupVerb.Uninstall => [SetupStage.Stop],
        _ => [SetupStage.Verify, SetupStage.Stop, SetupStage.Swap, SetupStage.Start],
    };

    private Border BuildEdge() => new()
    {
        Height = 6,
        Visibility = Visibility.Collapsed,
        Background = SetupBrushes.Frozen(Tokens.SurfaceDark),
    };

    private Border BuildBand(out TextBlock title)
    {
        var mark = new System.Windows.Controls.Image
        {
            Width = SetupWindowMetrics.BandIconSize,
            Height = SetupWindowMetrics.BandIconSize,
            Source = LoadAppIcon(),
            VerticalAlignment = VerticalAlignment.Center,
        };
        title = UninstallChoiceWindow.CreateTitle(SetupDisplayText.ProgressTitle(_strings, _verb));
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(22, 0, 0, 0);

        // r3 F14: a horizontal StackPanel offers the title unlimited width, so TextWrapping never
        // applies. The two-column Grid gives the mark an Auto slot and the title the rest.
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(mark, 0);
        Grid.SetColumn(title, 1);
        grid.Children.Add(mark);
        grid.Children.Add(title);

        return new Border
        {
            Background = SetupBrushes.Opaque(Tokens.SurfaceMedium),
            Padding = new Thickness(88, 22, 88, 22),
            MinHeight = 132,
            Child = grid,
        };
    }

    private Border BuildBodyHost()
    {
        var host = new Border
        {
            Padding = new Thickness(88, 64, 88, 48),
            Background = SetupBrushes.Opaque(Tokens.SurfaceDark),
        };
        var stack = new StackPanel();

        // The progress body: detail line + one row per planned stage (D9).
        _progressBody = new StackPanel();
        _detail = UninstallChoiceWindow.CreateProse(
            SetupDisplayText.ProgressDetail(_strings, _verb), secondary: true);
        _detail.Margin = new Thickness(0, 0, 0, 32);
        _progressBody.Children.Add(_detail);
        foreach (var stage in PlanFor(_verb))
        {
            var row = new StageRow { Stage = stage };
            row.Glyph = NewPendingGlyph();
            row.Label = UninstallChoiceWindow.CreateStageLabel(
                SetupDisplayText.Stage(_strings, _verb, stage));
            row.Label.Margin = new Thickness(24, 0, 0, 0);
            row.Label.VerticalAlignment = VerticalAlignment.Center;
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 15, 0, 15) };
            line.Children.Add(row.Glyph);
            line.Children.Add(row.Label);
            _stageRows.Add(row);
            _progressBody.Children.Add(line);
        }

        // The result body (D8): neutral dash / state icon + confirming text, collapsed until
        // ShowResult. SIBLING of the progress body — never nested inside it (r3 finding 2).
        _resultBody = new StackPanel();
        _resultBody.Visibility = Visibility.Collapsed;
        _neutralDash = new System.Windows.Controls.Border
        {
            Width = 64,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            Background = SetupBrushes.Frozen(Tokens.TextDim),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 24),
            Visibility = Visibility.Collapsed,
        };
        _resultIcon = NewStateGlyph();
        _resultText = UninstallChoiceWindow.CreateTitle(string.Empty);
        _resultBody.Children.Add(_neutralDash);
        _resultBody.Children.Add(_resultIcon);
        _resultBody.Children.Add(_resultText);

        stack.Children.Add(_progressBody);
        stack.Children.Add(_resultBody);
        host.Child = stack;
        return host;
    }

    /// <summary>A pending stage row: outline diamond (r1 F14 — non-round), drawn at creation
    /// (r3 finding: pending glyphs that are never painted show nothing). Width/Height are the
    /// GEOMETRY units (24); the LayoutTransform lifts the drawn glyph to the 30-DIP box and the
    /// layout footprint stays 30 (r4 finding 5 — Width = box + transform double-scaled it).</summary>
    private System.Windows.Shapes.Path NewPendingGlyph() => new()
    {
        Data = Geometry.Parse("M12 3.6 L20.4 12 L12 20.4 L3.6 12 Z"),
        Stroke = Tokens.BrushBorderSubtle,
        StrokeThickness = 1.7,
        StrokeLineJoin = PenLineJoin.Round,
        Width = 24,
        Height = 24,
        Stretch = Stretch.None,
        LayoutTransform = new ScaleTransform(30 / 24.0, 30 / 24.0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>The result state glyph: 24 geometry units under a LayoutTransform to the 64-DIP
    /// box (r4 finding 5 — draws at 64 and occupies 64, not 171). Exposed for the contrast test.</summary>
    private System.Windows.Shapes.Path NewStateGlyph() => new()
    {
        StrokeThickness = 1.7,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Width = 24,
        Height = 24,
        Stretch = Stretch.None,
        LayoutTransform = new ScaleTransform(
            SetupWindowMetrics.ResultIconSize / 24.0, SetupWindowMetrics.ResultIconSize / 24.0),
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, 0, 0, 24),
    };

    private void MarkActive(StageRow row)
    {
        // r3 finding: the previously active row must leave the active state, or StageText sticks
        // to the first stage forever.
        foreach (var other in _stageRows.Where(r => r != row && r.Active))
        {
            other.Active = false;
            other.Glyph.Data = Geometry.Parse("M4 12.5 L9.5 18 L20 6.5");
            other.Glyph.Fill = null;
            other.Glyph.Stroke = Tokens.BrushAccentSuccess;
            other.Label.Foreground = Tokens.BrushTextSecondary;
        }

        row.Active = true;
        row.Glyph.Data = Geometry.Parse("M12 3.6 L20.4 12 L12 20.4 L3.6 12 Z");
        row.Glyph.Fill = Tokens.BrushAccent;
        row.Glyph.Stroke = Tokens.BrushAccent;
        row.Label.Foreground = Tokens.BrushTextPrimary;
    }

    /// <summary>D9/r3 F3: an emitted stage outside the plan is appended (with its own weight and
    /// colour) and marked active — never silently dropped.</summary>
    private StageRow AppendStageRow(SetupStage stage, string label)
    {
        var row = new StageRow { Stage = stage };
        row.Glyph = NewPendingGlyph();
        row.Label = UninstallChoiceWindow.CreateStageLabel(label);
        row.Label.Margin = new Thickness(24, 0, 0, 0);
        row.Label.VerticalAlignment = VerticalAlignment.Center;
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 15, 0, 15) };
        line.Children.Add(row.Glyph);
        line.Children.Add(row.Label);
        // Child 0 is the detail line — the rows start at 1 (r3 finding: the index was off by one).
        _progressBody.Children.Insert(1 + _stageRows.Count, line);
        _stageRows.Add(row);
        return row;
    }

    /// <summary>D8's state colours: edge + result icon carry the colour (D7 — the title stays
    /// TextPrimary). Neutral = the dim dash Border instead of the path (r1 F14, mockup).</summary>
    private void PaintStateGlyph(SetupDisplayState state)
    {
        _resultIcon.Visibility = state == SetupDisplayState.Neutral
            ? Visibility.Collapsed
            : Visibility.Visible;
        _neutralDash.Visibility = state == SetupDisplayState.Neutral
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (state == SetupDisplayState.Neutral)
            return;

        _resultIcon.StrokeThickness = 1.7;
        switch (state)
        {
            case SetupDisplayState.Success:
                _resultIcon.Data = Geometry.Parse("M4 12.5 L9.5 18 L20 6.5");
                _resultIcon.Stroke = Tokens.BrushAccentSuccess;
                _resultIcon.Fill = null;
                break;
            case SetupDisplayState.Failure:
                _resultIcon.Data = Geometry.Parse(
                    "M12 3.5 L21.2 19.8 L2.8 19.8 Z M12 9.6 L12 14.2 M12 17.2 L12 17.3");
                _resultIcon.Stroke = Tokens.BrushAccentDanger;
                _resultIcon.Fill = null;
                break;
        }
    }

    private void DemoteBand()
    {
        _bandTitle.Foreground = Tokens.BrushTextSecondary;
        _bandTitle.FontWeight = Tokens.FontWeightMedium;
    }

    /// <summary>The taskbar/window title: the process title (FC7: re-resolved on results via
    /// SetBandTitle).</summary>
    internal string TitleText => Title;

    /// <summary>The band title as drawn (the FC7 test reads it).</summary>
    internal string BandTitleText => _bandTitle.Text;

    /// <summary>The confirming body text (D8 — SetupProgressWindowTests:122 reads it).</summary>
    internal string ResultText => _resultText.Text;

    internal string DetailText => _detail.Text;

    /// <summary>The active stage row's label, or empty once the result body is shown.</summary>
    internal string StageText =>
        _resultBody.Visibility == Visibility.Visible
            ? string.Empty
            : _stageRows.FirstOrDefault(r => r.Active)?.Label.Text ?? string.Empty;

    /// <summary>True while the result body is the visible one. The logical-tree walk STOPS AT THE
    /// WINDOW (r4 finding 3: a never-shown Window's own Visibility defaults to Collapsed — walking
    /// into it made this accessor always false on correct code). Mutant M2 (nesting the result body
    /// inside the collapsed progress stack) still fails it: the collapsed ancestor is the stack.</summary>
    internal bool ShowsResultBody
    {
        get
        {
            if (_resultBody.Visibility != Visibility.Visible) return false;
            for (System.Windows.DependencyObject? ancestor = System.Windows.LogicalTreeHelper.GetParent(_resultBody);
                 ancestor is not null && !ReferenceEquals(ancestor, this);
                 ancestor = System.Windows.LogicalTreeHelper.GetParent(ancestor))
            {
                if (ancestor is System.Windows.UIElement element
                    && element.Visibility != Visibility.Visible)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>The result state glyph (the contrast test reads stroke, transform and bounds
    /// directly instead of searching the tree by Width — r4 finding 5).</summary>
    internal System.Windows.Shapes.Path ResultStateGlyph => _resultIcon;

    /// <summary>True while the neutral dash (Cancelled's 64 × 10 marker) is the visible one —
    /// the per-theme state test pins it beside the icon collapse (PR-review nit 3).</summary>
    internal bool NeutralDashVisible => _neutralDash.Visibility == Visibility.Visible;

    /// <summary>How long the result will stay on screen.</summary>
    internal TimeSpan ScheduledCloseDelay => _closeTimer.Interval;

    /// <summary>True once <see cref="ShowResult"/> armed the close timer.</summary>
    internal bool IsCloseScheduled => _closeTimer.IsEnabled;

    /// <summary>Advances the stage plan: marks the emitted stage active, earlier active rows done,
    /// and appends any stage outside the plan (D9/r3 F3).</summary>
    internal void Stage(SetupStage stage)
    {
        var row = _stageRows.FirstOrDefault(r => r.Stage == stage)
            ?? AppendStageRow(stage, SetupDisplayText.Stage(_strings, _verb, stage));
        MarkActive(row);
    }

    /// <summary>Re-resolves the band title from the given table (FC7 after D8, r3 F1) — and keeps
    /// the taskbar title in sync, so the package cannot park its claim there either.</summary>
    internal void SetBandTitle(string title)
    {
        _bandTitle.Text = title;
        Title = title;
    }

    /// <summary>Replaces the progress with the terminal result: the state edge and icon carry the
    /// colour (D7), the band is demoted (r3 F2), the progress texts are CLEARED so no preferred-table
    /// text survives anywhere in the visual tree (FC7), and the close timer arms (FC5).</summary>
    internal void ShowResult(SetupDisplayResult result)
    {
        _edge.Background = SetupBrushes.Frozen(StateColor(result.State));
        _edge.Visibility = Visibility.Visible;
        DemoteBand();
        _progressBody.Visibility = Visibility.Collapsed;
        _resultBody.Visibility = Visibility.Visible;
        PaintStateGlyph(result.State);
        _resultText.Text = result.Text;
        _detail.Text = string.Empty;
        foreach (var row in _stageRows)
            row.Label.Text = string.Empty;
        ScheduleClose(SetupDisplayText.CloseDelayFor(result.State));
    }

    /// <summary>The edge colours per state (r3 T4: the per-theme contrast tests assert them).</summary>
    private static System.Windows.Media.Color StateColor(SetupDisplayState state) => state switch
    {
        SetupDisplayState.Success => Tokens.AccentSuccess,
        SetupDisplayState.Failure => Tokens.AccentDanger,
        _ => Tokens.TextDim,
    };

    /// <summary>Arms the self-close for <paramref name="delay"/>.</summary>
    internal void ScheduleClose(TimeSpan delay)
    {
        _closeTimer.Stop();
        _closeTimer.Interval = delay;
        _closeTimer.Start();
    }

    /// <summary>Tests whether an HWND carries the click-through style (Q13: it must not).</summary>
    internal static bool HasClickThroughStyle(IntPtr hwnd) =>
        (ReadExStyle(hwnd) & WsExTransparent) != 0;

    /// <summary>Tests whether an HWND carries the non-activating style (Q13: it must not).</summary>
    internal static bool HasNoActivateStyle(IntPtr hwnd) =>
        (ReadExStyle(hwnd) & WsExNoActivate) != 0;

    /// <summary>Tests whether an HWND is layered (Q13: an ordinary window is not).</summary>
    internal static bool HasLayeredStyle(IntPtr hwnd) =>
        (ReadExStyle(hwnd) & WsExLayered) != 0;

    /// <summary>Tests whether an HWND is topmost (Q13: it must not be).</summary>
    internal static bool HasTopmostStyle(IntPtr hwnd) =>
        (ReadExStyle(hwnd) & WsExTopmost) != 0;

    /// <summary>Tests whether an HWND is a tool window, which would hide it from the taskbar
    /// (Q13: it must not be).</summary>
    internal static bool HasToolWindowStyle(IntPtr hwnd) =>
        (ReadExStyle(hwnd) & WsExToolWindow) != 0;

    /// <summary>EyeCtrl's icon for the band mark and the taskbar button. The .ico's frame 0 is
    /// 16×16 (r1 F11), so the LARGEST frame is selected explicitly; a missing icon must never stop
    /// the screen from appearing. The pack scheme is touched first so a lone-test run (no Window
    /// ctor yet) does not swallow a UriFormatException into null (r3 F17); the stream is disposed —
    /// OnLoad has already copied the frames.</summary>
    internal static BitmapSource? LoadAppIcon()
    {
        try
        {
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
            var stream = Application.GetResourceStream(new Uri(IconResource))?.Stream;
            if (stream is null) return null;
            using (stream)
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).First();
                frame.Freeze();
                return frame;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static long ReadExStyle(IntPtr hwnd) =>
        GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
}
