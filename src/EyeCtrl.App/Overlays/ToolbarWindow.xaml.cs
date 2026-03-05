namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;

public partial class ToolbarWindow : OverlayWindow
{
    private readonly Dictionary<string, (Border Container, Rectangle ProgressBar, TextBlock Label)> _buttonVisuals = new();
    private double _buttonSize;
    private bool _targetsInitialized;
    private bool? _providerIsTobii;

    public ToolbarWindow()
    {
        InitializeComponent();
    }

    public void Initialize(double buttonSize, int dwellTimeMs)
    {
        _buttonSize = buttonSize;
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
            AddButton(id, symbol);

        if (_providerIsTobii.HasValue)
            AddIndicatorElement(_providerIsTobii.Value);
    }

    private void AddButton(string id, string symbol)
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

        // Set AutomationId on the label (TextBlock) rather than the Border container.
        // WPF's UIA control view only exposes TextBlock elements from transparent overlay
        // windows; Border containers are collapsed out of the tree even with IsControlElement=true.
        System.Windows.Automation.AutomationProperties.SetAutomationId(label, $"Toolbar_Button_{id}");

        _buttonVisuals[id] = (container, progressBar, label);
        ButtonPanel.Children.Add(container);
    }

    private void AddIndicatorElement(bool isTobii)
    {
        var symbol = isTobii ? "\U0001F441" : "\U0001F5B1"; // eye or mouse

        var label = new TextBlock
        {
            Text = symbol,
            Foreground = Brushes.White,
            FontSize = _buttonSize * 0.4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var border = new Border
        {
            Width = _buttonSize,
            Height = _buttonSize,
            Margin = new Thickness(2),
            Background = new SolidColorBrush(Color.FromArgb(120, 40, 40, 40)),
            CornerRadius = new CornerRadius(4),
            Child = label
        };

        ButtonPanel.Children.Insert(0, border);
    }

    public void SetProviderIndicator(bool isTobii)
    {
        _providerIsTobii = isTobii;
        AddIndicatorElement(isTobii);
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
        if (!_targetsInitialized)
            UpdateTargetPositions();
    }

    private void UpdateTargetPositions()
    {
        if (DataContext is not ToolbarViewModel vm) return;

        foreach (var (id, (container, _, _)) in _buttonVisuals)
        {
            try
            {
                var pos = container.PointToScreen(new Point(container.ActualWidth / 2, container.ActualHeight / 2));
                vm.UpdateButtonScreenPosition(id, new ScreenPoint(pos.X, pos.Y));
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
        if (_buttonVisuals.TryGetValue("Pause", out var visuals))
        {
            // ▶ Play / ⏸ Pause
            visuals.Label.Text = isPaused ? "\u25B6" : "\u23F8";
        }
    }

    public void SetZoomState(bool isZoomOn)
    {
        if (_buttonVisuals.TryGetValue("Zoom", out var visuals))
        {
            visuals.Container.Background = isZoomOn
                ? new SolidColorBrush(Color.FromArgb(180, 0, 100, 0))
                : new SolidColorBrush(Color.FromArgb(180, 60, 60, 60));
        }
    }

    /// <summary>
    /// Subscribes to each ToolbarButtonViewModel.PropertyChanged to drive
    /// progress bar visuals from the ViewModel's dwell state.
    /// Call after DataContext is set.
    /// </summary>
    public void BindToViewModel()
    {
        if (DataContext is not ToolbarViewModel vm) return;
        foreach (var btn in vm.Buttons)
        {
            var id = btn.Id;
            btn.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ToolbarButtonViewModel.DwellProgress))
                {
                    if (_buttonVisuals.TryGetValue(id, out var visuals))
                        visuals.ProgressBar.Width = btn.DwellProgress * _buttonSize;
                }
            };
        }
    }
}
