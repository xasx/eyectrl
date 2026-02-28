namespace EyeCtrl.App.Overlays;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Processing;

public partial class KeyboardWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const double BaseKeySize = 56;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private readonly DwellDetector _keyDwellDetector = new();
    private readonly DwellDetector _predictionDwellDetector = new();
    private readonly Dictionary<string, (Border Container, TextBlock Label)> _keyVisuals = new();
    private readonly Dictionary<string, KeyDefinition> _keyDefinitions = new();

    private KeyboardLayout? _layout;
    private string _activeSubLayoutId = "letters";
    private WordPredictor? _predictor;
    private bool _isShifted;
    private double _keySize;
    private int _keyDwellMs;
    private int _predictionDwellMs;
    private string _currentInput = "";

    public event Action<string>? KeyActivated;
    public event Action<string>? PredictionSelected;

    public KeyboardWindow()
    {
        InitializeComponent();

        _keyDwellDetector.TargetActivated += OnKeyDwelled;
        _keyDwellDetector.TargetProgress += OnKeyProgress;
        _predictionDwellDetector.TargetActivated += OnPredictionDwelled;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        var extStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, extStyle | WS_EX_TOOLWINDOW);
    }

    public void LoadLayout(KeyboardLayout layout, double keySizeMultiplier, int keyDwellMs, int predictionDwellMs)
    {
        _layout = layout;
        _activeSubLayoutId = layout.DefaultSubLayout;
        _keySize = BaseKeySize * keySizeMultiplier;
        _keyDwellMs = keyDwellMs;
        _predictionDwellMs = predictionDwellMs;

        RebuildKeys();
    }

    public void SetPredictor(WordPredictor predictor)
    {
        _predictor = predictor;
    }

    public void SetScreenEdge(string edge)
    {
        var screen = SystemParameters.WorkArea;

        switch (edge.ToLowerInvariant())
        {
            case "top":
                Left = (screen.Width - Width) / 2;
                Top = 0;
                break;
            case "bottom":
            default:
                Left = (screen.Width - Width) / 2;
                Top = screen.Height - Height;
                break;
        }
    }

    private void RebuildKeys()
    {
        if (_layout == null) return;
        if (!_layout.SubLayouts.TryGetValue(_activeSubLayoutId, out var subLayout)) return;

        _keyDwellDetector.ClearTargets();
        _keyVisuals.Clear();
        _keyDefinitions.Clear();
        KeyRows.Children.Clear();

        for (int rowIdx = 0; rowIdx < subLayout.Rows.Count; rowIdx++)
        {
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };

            for (int keyIdx = 0; keyIdx < subLayout.Rows[rowIdx].Count; keyIdx++)
            {
                var key = subLayout.Rows[rowIdx][keyIdx];
                var id = $"key_{rowIdx}_{keyIdx}";

                _keyDefinitions[id] = key;

                var label = new TextBlock
                {
                    Text = key.Label,
                    Foreground = Brushes.White,
                    FontSize = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var isSwitchSub = key.SpecialAction?.StartsWith("switchSub:") == true;
                var isSpecial = !string.IsNullOrEmpty(key.SpecialAction);
                var container = new Border
                {
                    Width = _keySize * key.Width + (key.Width > 1 ? (key.Width - 1) * 4 : 0),
                    Height = _keySize,
                    Margin = new Thickness(2),
                    Background = new SolidColorBrush(isSwitchSub
                        ? Color.FromArgb(200, 50, 70, 110)
                        : isSpecial
                            ? Color.FromArgb(200, 60, 60, 80)
                            : Color.FromArgb(200, 50, 50, 50)),
                    CornerRadius = new CornerRadius(6),
                    Child = label
                };

                _keyVisuals[id] = (container, label);
                rowPanel.Children.Add(container);

                _keyDwellDetector.AddTarget(new DwellTarget(id, new ScreenPoint(0, 0), _keySize / 2, _keyDwellMs));
            }

            KeyRows.Children.Add(rowPanel);
        }
    }

    public void UpdateGaze(ScreenPoint screenPoint, long timestampMicroseconds)
    {
        UpdateKeyTargetPositions();
        _keyDwellDetector.Update(screenPoint, timestampMicroseconds);
        _predictionDwellDetector.Update(screenPoint, timestampMicroseconds);
    }

    private void UpdateKeyTargetPositions()
    {
        foreach (var (id, (container, _)) in _keyVisuals)
        {
            try
            {
                var pos = container.PointToScreen(new Point(container.ActualWidth / 2, container.ActualHeight / 2));
                _keyDwellDetector.RemoveTarget(id);
                _keyDwellDetector.AddTarget(new DwellTarget(id, new ScreenPoint(pos.X, pos.Y), container.ActualWidth / 2, _keyDwellMs));
            }
            catch
            {
                // Window not yet rendered
            }
        }
    }

    private void OnKeyDwelled(string targetId)
    {
        if (!_keyDefinitions.TryGetValue(targetId, out var key)) return;

        if (!string.IsNullOrEmpty(key.SpecialAction))
        {
            HandleSpecialKey(key.SpecialAction);
            return;
        }

        var output = _isShifted && !string.IsNullOrEmpty(key.ShiftOutput)
            ? key.ShiftOutput
            : key.Output;

        if (_isShifted)
        {
            _isShifted = false;
            UpdateShiftDisplay();
        }

        _currentInput += output;
        _predictor?.LearnWord(output);
        UpdatePredictions();
        KeyActivated?.Invoke(output);
    }

    private void HandleSpecialKey(string action)
    {
        var lowerAction = action.ToLowerInvariant();

        if (lowerAction.StartsWith("switchsub:"))
        {
            var targetSubId = action[10..]; // preserve original casing of sub-layout id
            SwitchSubLayout(targetSubId);
            return;
        }

        switch (lowerAction)
        {
            case "shift":
                _isShifted = !_isShifted;
                UpdateShiftDisplay();
                break;
            case "backspace":
                if (_currentInput.Length > 0)
                    _currentInput = _currentInput[..^1];
                UpdatePredictions();
                KeyActivated?.Invoke("\b");
                break;
            case "enter":
                _currentInput = "";
                UpdatePredictions();
                KeyActivated?.Invoke("\n");
                break;
            case "tab":
                KeyActivated?.Invoke("\t");
                break;
            case "space":
                if (_currentInput.Length > 0)
                    _predictor?.LearnWord(_currentInput);
                _currentInput = "";
                UpdatePredictions();
                KeyActivated?.Invoke(" ");
                break;
        }
    }

    private void SwitchSubLayout(string subLayoutId)
    {
        if (_layout == null || !_layout.SubLayouts.ContainsKey(subLayoutId)) return;
        if (_activeSubLayoutId == subLayoutId) return;

        _activeSubLayoutId = subLayoutId;
        _isShifted = false;
        RebuildKeys();
    }

    private void UpdateShiftDisplay()
    {
        foreach (var (id, (_, label)) in _keyVisuals)
        {
            if (_keyDefinitions.TryGetValue(id, out var key) && string.IsNullOrEmpty(key.SpecialAction))
            {
                label.Text = _isShifted && !string.IsNullOrEmpty(key.ShiftOutput)
                    ? key.ShiftOutput
                    : key.Label;
            }
        }
    }

    public void UpdatePredictions()
    {
        if (_predictor == null) return;

        _predictionDwellDetector.ClearTargets();
        PredictionBar.Children.Clear();

        var suggestions = _predictor.GetSuggestions(_currentInput, maxCount: 5);

        for (int i = 0; i < suggestions.Count; i++)
        {
            var word = suggestions[i];
            var predId = $"pred_{i}";

            var btn = new Border
            {
                MinWidth = 80,
                Height = 36,
                Margin = new Thickness(3),
                Padding = new Thickness(12, 0, 12, 0),
                Background = new SolidColorBrush(Color.FromArgb(200, 40, 80, 40)),
                CornerRadius = new CornerRadius(4),
                Child = new TextBlock
                {
                    Text = word,
                    Foreground = Brushes.White,
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            };

            PredictionBar.Children.Add(btn);
            _predictionDwellDetector.AddTarget(new DwellTarget(predId, new ScreenPoint(0, 0), 40, _predictionDwellMs));
        }
    }

    private void OnPredictionDwelled(string targetId)
    {
        if (!targetId.StartsWith("pred_")) return;

        if (int.TryParse(targetId.AsSpan(5), out var index))
        {
            var suggestions = _predictor?.GetSuggestions(_currentInput, maxCount: 5);
            if (suggestions != null && index < suggestions.Count)
            {
                var word = suggestions[index];
                _predictor?.LearnWord(word);
                _currentInput = "";
                UpdatePredictions();
                PredictionSelected?.Invoke(word);
            }
        }
    }

    private void OnKeyProgress(string targetId, double progress)
    {
        if (!_keyVisuals.TryGetValue(targetId, out var visuals)) return;

        var specialAction = _keyDefinitions.GetValueOrDefault(targetId)?.SpecialAction;
        var isSwitchSub = specialAction?.StartsWith("switchSub:") == true;
        var isSpecial = !string.IsNullOrEmpty(specialAction);
        var baseColor = isSwitchSub
            ? Color.FromArgb(200, 50, 70, 110)
            : isSpecial
                ? Color.FromArgb(200, 60, 60, 80)
                : Color.FromArgb(200, 50, 50, 50);

        var highlightColor = Color.FromArgb(200, 0, (byte)(100 + 155 * progress), 0);
        var blended = Color.FromArgb(
            200,
            (byte)(baseColor.R * (1 - progress) + highlightColor.R * progress),
            (byte)(baseColor.G * (1 - progress) + highlightColor.G * progress),
            (byte)(baseColor.B * (1 - progress) + highlightColor.B * progress));

        visuals.Container.Background = new SolidColorBrush(blended);
    }
}
