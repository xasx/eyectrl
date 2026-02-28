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
    private readonly Dictionary<string, (Border Container, Rectangle ProgressBar, TextBlock Label)> _buttonVisuals = new();
    private double _buttonSize;

    private bool _isPaused;
    private bool _isZoomOn;
    private bool _targetsInitialized;

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
        _targetsInitialized = false;

        // id + symbol (Unicode)
        var buttons = new (string Id, string Symbol)[]
        {
            ("Pause",    "\u23F8"),    // ⏸ Pause
            ("Zoom",     "\u2295"),    // ⊕ Magnify/zoom
            ("Keyboard", "\u2328"),    // ⌨ Keyboard
            ("Settings", "\u2699"),    // ⚙ Gear
        };

        foreach (var (id, symbol) in buttons)
            AddButton(id, symbol, dwellTimeMs);
    }

    private void AddButton(string id, string symbol, int dwellTimeMs)
    {
        var progressBar = new Rectangle
        {
            Height = 4,
            Fill = Brushes.Lime,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Width = 0
        };

        var label = new TextBlock
        {
            Text = symbol,
            Foreground = Brushes.White,
            FontSize = _buttonSize * 0.4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var container = new Border
        {
            Width = _buttonSize,
            Height = _buttonSize,
            Margin = new Thickness(2),
            Background = new SolidColorBrush(Color.FromArgb(180, 60, 60, 60)),
            CornerRadius = new CornerRadius(4),
            Child = new Grid
            {
                Children = { label, progressBar }
            }
        };

        _buttonVisuals[id] = (container, progressBar, label);
        ButtonPanel.Children.Add(container);

        // DwellTarget gets its center updated on first gaze via UpdateTargetPositions
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

        _targetsInitialized = false;
    }

    public void UpdateGaze(ScreenPoint screenPoint, long timestampMicroseconds)
    {
        // Update target positions only once after layout or mode change
        if (!_targetsInitialized)
            UpdateTargetPositions();

        _dwellDetector.Update(screenPoint, timestampMicroseconds);
    }

    private void UpdateTargetPositions()
    {
        foreach (var (id, (container, _, _)) in _buttonVisuals)
        {
            try
            {
                var pos = container.PointToScreen(new Point(container.ActualWidth / 2, container.ActualHeight / 2));
                _dwellDetector.UpdateTargetCenter(id, new ScreenPoint(pos.X, pos.Y));
            }
            catch
            {
                // Container may not be laid out yet
                return;
            }
        }

        _targetsInitialized = true;
    }

    public void SetPausedState(bool isPaused)
    {
        _isPaused = isPaused;
        if (_buttonVisuals.TryGetValue("Pause", out var visuals))
        {
            // ▶ Play / ⏸ Pause
            visuals.Label.Text = isPaused ? "\u25B6" : "\u23F8";
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
        // Reset progress bar on activation
        if (_buttonVisuals.TryGetValue(targetId, out var visuals))
            visuals.ProgressBar.Width = 0;

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
            visuals.ProgressBar.Width = progress * _buttonSize;
    }
}
