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
/// The display-only progress/result screen for update, rollback and uninstall: large centred text
/// in the theme tokens. Owner decision Q13 made it an ordinary window: it is not click-through (no
/// <c>WS_EX_TRANSPARENT</c>/<c>WS_EX_LAYERED</c>), not topmost, visible in the taskbar and may
/// activate once when it appears. It has no targets and closes itself — 10 s after a success, 60 s
/// after a failure (Q12's degraded included). Closing it early only ends the display; the install
/// carries on.
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
    private readonly TextBlock _title;
    private readonly TextBlock _detail;
    private readonly TextBlock _stage;
    private readonly DispatcherTimer _closeTimer;
    private readonly Action _close;

    /// <summary>Builds the window. Nothing is shown until the display calls <see cref="Show"/>.</summary>
    /// <param name="strings">The resolved table.</param>
    /// <param name="verb">The verb whose progress text is shown.</param>
    /// <param name="close">
    /// The close callback; tests pass a flag setter so no window is ever shown or closed. Production
    /// leaves it null and <see cref="Window.Close"/> is used.
    /// </param>
    public SetupProgressWindow(StringTable strings, SetupVerb verb, Action? close = null)
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
        var opaqueSurface = OpaqueSurfaceDark();
        Background = opaqueSurface;
        Foreground = Tokens.BrushTextPrimary;
        FontFamily = Tokens.FontStandard;
        FontSize = SetupWindowMetrics.ProseFontSize;
        UseLayoutRounding = true;
        Icon = LoadAppIcon();

        var bounds = SetupWindowMetrics.ProgressBounds(SystemParameters.WorkArea);
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;

        _title = UninstallChoiceWindow.CreateProse(string.Empty, strong: true);
        _detail = UninstallChoiceWindow.CreateProse(string.Empty, secondary: true);
        _stage = UninstallChoiceWindow.CreateProse(string.Empty);

        _title.Text = progressTitle;
        _detail.Text = SetupDisplayText.ProgressDetail(strings, verb);

        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = SetupWindowMetrics.ProseWidth,
        };
        panel.Children.Add(_title);
        panel.Children.Add(_detail);
        panel.Children.Add(_stage);

        Content = new Border
        {
            Background = opaqueSurface,
            Padding = new Thickness(SetupWindowMetrics.WindowMargin),
            Child = panel,
        };

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

    /// <summary>The progress title, for the tests' text assertions.</summary>
    internal string TitleText => _title.Text;

    /// <summary>The fixed detail line.</summary>
    internal string DetailText => _detail.Text;

    /// <summary>The current stage line.</summary>
    internal string StageText => _stage.Text;

    /// <summary>How long the result will stay on screen.</summary>
    internal TimeSpan ScheduledCloseDelay => _closeTimer.Interval;

    /// <summary>True once <see cref="ShowResult"/> armed the close timer.</summary>
    internal bool IsCloseScheduled => _closeTimer.IsEnabled;

    /// <summary>Advances the stage line.</summary>
    internal void Stage(SetupStage stage) => _stage.Text = SetupDisplayText.Stage(_strings, _verb, stage);

    /// <summary>Replaces the progress with the terminal result and arms the self-close.</summary>
    internal void ShowResult(SetupDisplayResult result)
    {
        _title.Text = result.Text;
        _title.Foreground = result.IsFailure ? Tokens.BrushAccentDanger : Tokens.BrushTextPrimary;
        _detail.Text = string.Empty;
        _stage.Text = string.Empty;
        ScheduleClose(SetupDisplayText.CloseDelayFor(result.IsFailure));
    }

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

    /// <summary>
    /// The theme's SurfaceDark colour with full opacity, frozen. N1: the ordinary window keeps the
    /// r01 background and its contrast.
    /// </summary>
    private static SolidColorBrush OpaqueSurfaceDark()
    {
        var color = Tokens.SurfaceDark;
        var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// EyeCtrl's icon for the title text and the taskbar button. A missing icon must never stop the
    /// result from appearing, so every failure simply leaves the default.
    /// </summary>
    internal static ImageSource? LoadAppIcon()
    {
        try
        {
            var icon = new BitmapImage();
            icon.BeginInit();
            icon.UriSource = new Uri(IconResource, UriKind.Absolute);
            icon.CacheOption = BitmapCacheOption.OnLoad;
            icon.EndInit();
            icon.Freeze();
            return icon;
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
