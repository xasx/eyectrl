namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;
using SelectionMode = EyeCtrl.Core.Models.SelectionMode;

public partial class ToolbarWindow : OverlayWindow
{
    // UI Layout Constants
    private const int ProgressBarHeight = 4; // Height of dwell progress bar
    // 12, not the old 2: buttons carry inscribed hit circles (size/2), so a 2-px margin left adjacent
    // circles near-tangent and every slight aim error landed on a neighbour (2026-08-17 field report:
    // "I mis dwell often"). 12 per side = 24 px between circles, the same clearance the settings
    // window's 100-DIP dwell rows use between their 100-DIP hit circles.
    private const int ButtonMargin = 12; // Margin between toolbar buttons
    // The local 0.4 symbol ratio moved to ContainerRatio.Large, which this file's 0.4 defined.

    private readonly Dictionary<string, (Border Container, Rectangle ProgressBar, TextBlock Label)> _buttonVisuals = new();
    private double _buttonSize;
    private bool _targetsInitialized;
    private GazeProviderType? _providerType;

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

        // id + symbol (Unicode). Every id here must exist in ToolbarViewModel's button list and have
        // a case in its OnTargetActivated switch; ToolbarButtonDriftTests enforces that, and records
        // why the ids deliberately absent from this list are absent.
        var buttons = new (string Id, string Symbol)[]
        {
            ("Pause",    "\u23F8"),    // ⏸ Pause
            ("SelectionMode",     "\u2295"),    // ⊕ Magnify/ManualSelection
            ("Keyboard", "\u2328"),    // ⌨ Keyboard
            ("Settings", "\u2699"),    // ⚙ Gear
            ("TaskSwitcher", "\u21C4"),  // 🪟 Window switcher (BMP; the old window-emoji glyph rendered as tofu in the toolbar font)
            ("ResetAdaptation", "\u21BA"),  // ↺ Reset struggle-adaptation (§8.6 always-reachable escape)
        };

        foreach (var (id, symbol) in buttons)
        {
            AddButton(id, symbol);
        }

        if (_providerType.HasValue)
            AddIndicatorElement(_providerType.Value);
    }

    private void AddButton(string id, string symbol)
    {
        var progressBar = new Rectangle
        {
            Height = ProgressBarHeight,
            Fill = Tokens.BrushAccent,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Width = 0
        };

        var label = new TextBlock
        {
            Text = symbol,
            Foreground = Tokens.BrushTextPrimary,
            FontSize = _buttonSize * ContainerRatio.Large,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var container = new Border
        {
            Width = _buttonSize,
            Height = _buttonSize,
            Margin = new Thickness(ButtonMargin),
            Background = Tokens.BrushSurfaceMedium,
            CornerRadius = Tokens.SmallCornerRadius,
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

    private void AddIndicatorElement(GazeProviderType providerType)
    {
        var symbol = providerType switch
        {
            GazeProviderType.StreamEngine => "\U0001F441",     // eye
#pragma warning disable CS0618 // Type or member is obsolete
            GazeProviderType.InteractionLib => "\U0001F441",   // eye (legacy, treated as StreamEngine)
#pragma warning restore CS0618 // Type or member is obsolete
            GazeProviderType.Mouse => "\U0001F5B1",           // mouse
            _ => "?"
        };

        var label = new TextBlock
        {
            Text = symbol,
            Foreground = Tokens.BrushTextPrimary,
            FontSize = _buttonSize * ContainerRatio.Large,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var border = new Border
        {
            Width = _buttonSize,
            Height = _buttonSize,
            Margin = new Thickness(ButtonMargin),
            Background = Tokens.BrushSurfaceDark,
            CornerRadius = Tokens.SmallCornerRadius,
            Child = label
        };

        ButtonPanel.Children.Insert(0, border);
    }

    public void SetProviderIndicator(GazeProviderType providerType)
    {
        _providerType = providerType;
        AddIndicatorElement(providerType);
    }

    public void SetMode(bool isDocked, string edge)
    {
        // Force layout so ActualWidth reflects the current button count (Initialize may have
        // changed it). With SizeToContent="WidthAndHeight" the width isn't re-measured until the
        // next layout pass, so without this the centering math below reads a stale width and the
        // toolbar sits off-center until the next SetMode call.
        UpdateLayout();

        var screen = SystemParameters.WorkArea;

        if (isDocked)
        {
            var (left, top) = OverlayPositioning.PlaceAtEdge(screen, edge, ActualWidth, ActualHeight);
            Left = left;
            Top = top;
            ButtonPanel.Orientation = edge.ToLowerInvariant() is "left" or "right"
                ? Orientation.Vertical
                : ButtonPanel.Orientation;
        }

        _targetsInitialized = false;
    }

    public void UpdateGaze(PhysicalPoint screenPoint, long timestampMicroseconds)
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
                vm.UpdateButtonScreenPosition(id, new PhysicalPoint(pos.X, pos.Y));
            }
            catch
            {
                // Container may not be laid out yet
                return;
            }
        }

        // Don't latch until every container has been laid out — a gaze dispatch at Normal
        // priority (9) can outrank the Render (7) layout pass, leaving ActualWidth == 0 and
        // collapsing all targets onto (0,0). Retry on the next frame instead.
        foreach (var (_, (container, _, _)) in _buttonVisuals)
        {
            if (container.ActualWidth == 0)
                return;  // not laid out yet; don't latch
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

    public void SetSelectionModeState(SelectionMode mode)
    {
        if (_buttonVisuals.TryGetValue("SelectionMode", out var visuals))
        {
            visuals.Container.Background = mode switch
            {
                SelectionMode.ManualSelection => Tokens.BrushToggleOn,
                SelectionMode.AutoSelection => Tokens.BrushSurfaceKeySwitch,
                _ => Tokens.BrushSurfaceMedium
            };
        }
    }

    // Ids whose ToolbarButtonViewModel.PropertyChanged we've already wired up. Buttons is
    // populated once in the ViewModel's constructor and never mutated afterwards, so this is a
    // cheap guard against BindToViewModel being called more than once rather than a rebind
    // mechanism. (It previously backed runtime add/remove of the window-actions region, which
    // moved to the Quick Actions toolbar in the 2026-08-09 migration.)
    private readonly HashSet<string> _boundIds = new();

    /// <summary>
    /// Subscribes to each ToolbarButtonViewModel.PropertyChanged to drive
    /// progress bar visuals from the ViewModel's dwell state.
    /// Call after DataContext is set.
    /// </summary>
    public void BindToViewModel()
    {
        if (DataContext is not ToolbarViewModel vm) return;

        // Bind the VM's buttons — the constructor-populated set, which never changes afterwards.
        foreach (var btn in vm.Buttons)
            BindButton(btn);
    }

    /// <summary>
    /// Subscribes one button's PropertyChanged to its progress bar/symbol visuals. Idempotent:
    /// a repeat call for an already-bound id no-ops (guarded by <see cref="_boundIds"/>), so a
    /// second BindToViewModel call cannot double-subscribe. The handler reads
    /// <c>_buttonVisuals[id]</c> at event time, so it stays correct after Initialize rebuilds the
    /// visuals dictionary (size/dock/edge saves).
    /// </summary>
    private void BindButton(ToolbarButtonViewModel btn)
    {
        var id = btn.Id;
        if (!_boundIds.Add(id))
            return; // already bound

        // Apply current symbol so the initial label/background match the actual mode (the
        // ViewModel may already have received the startup SelectionModeChangedMessage before
        // BindToViewModel is called).
        if (_buttonVisuals.TryGetValue(id, out var currentVisuals))
        {
            currentVisuals.Label.Text = btn.Symbol;
            if (id == "SelectionMode")
                SetSelectionModeState(SelectionModeGlyphs.ToMode(btn.Symbol));
        }

        btn.PropertyChanged += (_, e) =>
        {
            if (!_buttonVisuals.TryGetValue(id, out var visuals))
                return;

            if (e.PropertyName == nameof(ToolbarButtonViewModel.DwellProgress))
            {
                visuals.ProgressBar.Width = btn.DwellProgress * _buttonSize;
            }
            else if (e.PropertyName == nameof(ToolbarButtonViewModel.Symbol))
            {
                visuals.Label.Text = btn.Symbol;
                if (id == "SelectionMode")
                    SetSelectionModeState(SelectionModeGlyphs.ToMode(btn.Symbol));
            }
        };
    }
}
