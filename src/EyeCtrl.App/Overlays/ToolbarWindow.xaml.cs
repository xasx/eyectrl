namespace EyeCtrl.App.Overlays;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public partial class ToolbarWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private readonly DwellDetector _dwellDetector = new();
    private readonly Dictionary<string, (Border Container, Ellipse ProgressDot)> _buttonVisuals = new();
    private double _buttonSize;

    private bool _isPaused;
    private bool _isZoomOn;

    public event Action? PauseToggled;
    public event Action? ZoomToggled;
    public event Action? KeyboardToggled;
    public event Action? SettingsRequested;

    public ToolbarWindow()
    {
        InitializeComponent();
        _dwellDetector.TargetActivated += OnTargetActivated;
        _dwellDetector.TargetProgress += OnTargetProgress;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        var extStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, extStyle | WS_EX_TOOLWINDOW);
    }

    public void Initialize(double buttonSize, int dwellTimeMs)
    {
        _buttonSize = buttonSize;
        _dwellDetector.ClearTargets();
        _buttonVisuals.Clear();
        ButtonPanel.Children.Clear();

        var buttons = new[] { "Pause", "Zoom", "Keyboard", "Settings" };
        var labels = new[] { "Pause", "Zoom", "Keys", "Settings" };

        for (int i = 0; i < buttons.Length; i++)
            AddButton(buttons[i], labels[i], dwellTimeMs);
    }

    private void AddButton(string id, string label, int dwellTimeMs)
    {
        var container = new Border
        {
            Width = _buttonSize,
            Height = _buttonSize,
            Margin = new Thickness(2),
            Background = new SolidColorBrush(Color.FromArgb(180, 60, 60, 60)),
            CornerRadius = new CornerRadius(4),
            Child = new Grid
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = label,
                        Foreground = Brushes.White,
                        FontSize = 11,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    (new Ellipse
                    {
                        Width = 8,
                        Height = 8,
                        Fill = Brushes.Lime,
                        Opacity = 0,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(0, 0, 4, 4)
                    })
                }
            }
        };

        var progressDot = (Ellipse)((Grid)container.Child).Children[1];
        _buttonVisuals[id] = (container, progressDot);
        ButtonPanel.Children.Add(container);

        // DwellTarget will be positioned after layout, using a placeholder center
        _dwellDetector.AddTarget(new DwellTarget(id, new ScreenPoint(0, 0), _buttonSize / 2, dwellTimeMs));
    }

    public void SetMode(bool isDocked, string edge)
    {
        var screen = SystemParameters.WorkArea;

        if (isDocked)
        {
            switch (edge.ToLowerInvariant())
            {
                case "top":
                    Left = (screen.Width - ActualWidth) / 2;
                    Top = 0;
                    break;
                case "bottom":
                    Left = (screen.Width - ActualWidth) / 2;
                    Top = screen.Height - ActualHeight;
                    break;
                case "left":
                    Left = 0;
                    Top = (screen.Height - ActualHeight) / 2;
                    ButtonPanel.Orientation = Orientation.Vertical;
                    break;
                case "right":
                    Left = screen.Width - ActualWidth;
                    Top = (screen.Height - ActualHeight) / 2;
                    ButtonPanel.Orientation = Orientation.Vertical;
                    break;
                default:
                    Left = (screen.Width - ActualWidth) / 2;
                    Top = 0;
                    break;
            }
        }
    }

    public void UpdateGaze(ScreenPoint screenPoint, long timestampMicroseconds)
    {
        // Update dwell target positions based on actual button positions on screen
        UpdateTargetPositions();
        _dwellDetector.Update(screenPoint, timestampMicroseconds);
    }

    private void UpdateTargetPositions()
    {
        foreach (var (id, (container, _)) in _buttonVisuals)
        {
            var pos = container.PointToScreen(new Point(container.ActualWidth / 2, container.ActualHeight / 2));
            var targets = _dwellDetector;
            // Update existing target center by removing and re-adding
            // DwellTarget.Center is settable
            _dwellDetector.RemoveTarget(id);
            _dwellDetector.AddTarget(new DwellTarget(id, new ScreenPoint(pos.X, pos.Y), _buttonSize / 2, 500));
        }
    }

    public void SetPausedState(bool isPaused)
    {
        _isPaused = isPaused;
        if (_buttonVisuals.TryGetValue("Pause", out var visuals))
        {
            var textBlock = (TextBlock)((Grid)visuals.Container.Child).Children[0];
            textBlock.Text = isPaused ? "Resume" : "Pause";
        }
    }

    public void SetZoomState(bool isZoomOn)
    {
        _isZoomOn = isZoomOn;
        if (_buttonVisuals.TryGetValue("Zoom", out var visuals))
        {
            visuals.Container.Background = isZoomOn
                ? new SolidColorBrush(Color.FromArgb(180, 0, 100, 0))
                : new SolidColorBrush(Color.FromArgb(180, 60, 60, 60));
        }
    }

    private void OnTargetActivated(string targetId)
    {
        switch (targetId)
        {
            case "Pause": PauseToggled?.Invoke(); break;
            case "Zoom": ZoomToggled?.Invoke(); break;
            case "Keyboard": KeyboardToggled?.Invoke(); break;
            case "Settings": SettingsRequested?.Invoke(); break;
        }
    }

    private void OnTargetProgress(string targetId, double progress)
    {
        if (_buttonVisuals.TryGetValue(targetId, out var visuals))
            visuals.ProgressDot.Opacity = progress;
    }
}
