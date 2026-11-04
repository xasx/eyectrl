namespace EyeCtrl.Setup.Display;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Localization;

/// <summary>
/// The display-only progress/result screen for update, rollback and uninstall: large centred text
/// in the theme tokens, topmost, click-through (<c>WS_EX_TRANSPARENT</c>) and non-activating
/// (<c>WS_EX_NOACTIVATE</c>). It has no targets and closes itself — 10 s after a success, 60 s
/// after a failure (Q12's degraded included).
/// </summary>
internal sealed class SetupProgressWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExLayered = 0x00080000L;

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
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // H1: WS_EX_TRANSPARENT only passes another process's clicks when the window is LAYERED.
        // The App measured this (clicking investigation R1 / #214); every click-through overlay
        // there sets AllowsTransparency. WindowStyle is already None, as WPF requires.
        AllowsTransparency = true;
        // N1: a layered window can still be visually opaque. Use the r01 SurfaceDark colour with
        // full alpha (frozen), so the result text keeps its contrast; click-through comes from
        // WS_EX_TRANSPARENT, not from alpha.
        var opaqueSurface = OpaqueSurfaceDark();
        Background = opaqueSurface;
        Foreground = Tokens.BrushTextPrimary;
        FontFamily = Tokens.FontStandard;
        FontSize = SetupWindowMetrics.ProseFontSize;
        UseLayoutRounding = true;

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

        SourceInitialized += (_, _) => ApplyClickThrough();
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

    /// <summary>Tests whether an HWND carries the click-through style.</summary>
    internal static bool HasClickThroughStyle(IntPtr hwnd) =>
        (ReadExStyle(hwnd) & WsExTransparent) != 0;

    /// <summary>Tests whether an HWND carries the non-activating style.</summary>
    internal static bool HasNoActivateStyle(IntPtr hwnd) =>
        (ReadExStyle(hwnd) & WsExNoActivate) != 0;

    /// <summary>Tests whether an HWND is layered, without which WS_EX_TRANSPARENT does not pass
    /// another process's clicks through.</summary>
    internal static bool HasLayeredStyle(IntPtr hwnd) =>
        (ReadExStyle(hwnd) & WsExLayered) != 0;

    /// <summary>
    /// The theme's SurfaceDark colour with full opacity, frozen. N1: the layered window must keep
    /// the r01 background and its contrast; WS_EX_TRANSPARENT, not alpha, provides click-through.
    /// </summary>
    private static SolidColorBrush OpaqueSurfaceDark()
    {
        var color = Tokens.SurfaceDark;
        var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private void ApplyClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var style = ReadExStyle(hwnd);
        style |= WsExTransparent | WsExNoActivate | WsExToolWindow;
        _ = SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
    }

    private static long ReadExStyle(IntPtr hwnd) =>
        GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
