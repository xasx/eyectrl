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

    // Prediction bar uses DwellDetector (small number of circular targets)
    private readonly DwellDetector _predictionDwellDetector = new();
    private readonly Dictionary<string, (Border Container, TextBlock Label)> _keyVisuals = new();
    private readonly Dictionary<string, KeyDefinition> _keyDefinitions = new();

    // Nearest-key tracking (replaces circular dwell targets for keys)
    private readonly Dictionary<string, ScreenPoint> _keyScreenPositions = new();
    private string? _currentNearestKeyId;
    private long _nearestKeyEnteredTimestamp;

    private KeyboardLayout? _layout;
    private string _activeSubLayoutId = "letters";
    private WordPredictor? _predictor;
    private bool _isShifted;
    private double _keySize;
    private int _keyDwellMs;
    private int _predictionDwellMs;
    private string _currentInput = "";
    private string _screenEdge = "bottom";
    private bool _predictionTargetsInitialized;

    public event Action<string>? KeyActivated;
    public event Action<string>? PredictionSelected;

    public KeyboardWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => RepositionOnScreen();
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

    public void SetPredictor(WordPredictor predictor) => _predictor = predictor;

    public void SetScreenEdge(string edge)
    {
        _screenEdge = edge;
        RepositionOnScreen();
    }

    private void RepositionOnScreen()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var screen = SystemParameters.WorkArea;
        switch (_screenEdge.ToLowerInvariant())
        {
            case "top":
                Left = (screen.Width - ActualWidth) / 2;
                Top = 0;
                break;
            case "bottom":
            default:
                Left = (screen.Width - ActualWidth) / 2;
                Top = screen.Height - ActualHeight;
                break;
        }
    }

    private void RebuildKeys()
    {
        if (_layout == null) return;
        if (!_layout.SubLayouts.TryGetValue(_activeSubLayoutId, out var subLayout)) return;

        _keyVisuals.Clear();
        _keyDefinitions.Clear();
        _keyScreenPositions.Clear();
        _currentNearestKeyId = null;
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
            }

            KeyRows.Children.Add(rowPanel);
        }
    }

    public void UpdateGaze(ScreenPoint screenPoint, long timestampMicroseconds)
    {
        UpdateKeyScreenPositions();
        UpdatePredictionTargetPositions();
        ProcessKeyGaze(screenPoint, timestampMicroseconds);
        _predictionDwellDetector.Update(screenPoint, timestampMicroseconds);
    }

    private void UpdateKeyScreenPositions()
    {
        foreach (var (id, (container, _)) in _keyVisuals)
        {
            try
            {
                var pos = container.PointToScreen(new Point(container.ActualWidth / 2, container.ActualHeight / 2));
                _keyScreenPositions[id] = new ScreenPoint(pos.X, pos.Y);
            }
            catch
            {
                // Window not yet rendered
            }
        }
    }

    /// <summary>
    /// Nearest-key dwell tracking: finds the closest key to the gaze point and
    /// tracks dwell time directly. Eliminates dead zones between keys and avoids
    /// circular hit-area overlap issues with rectangular keyboard keys.
    /// </summary>
    private void ProcessKeyGaze(ScreenPoint gazePoint, long timestampMicroseconds)
    {
        if (_keyScreenPositions.Count == 0) return;

        // Only track when gaze is inside the keyboard window
        if (!IsGazeInsideKeyboard(gazePoint))
        {
            if (_currentNearestKeyId != null)
            {
                ResetKeyVisual(_currentNearestKeyId);
                _currentNearestKeyId = null;
            }
            return;
        }

        // Find nearest key to gaze point
        string? nearestId = null;
        double nearestDist = double.MaxValue;

        foreach (var (id, center) in _keyScreenPositions)
        {
            double dx = gazePoint.X - center.X;
            double dy = gazePoint.Y - center.Y;
            double dist = dx * dx + dy * dy;
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearestId = id;
            }
        }

        if (nearestId != _currentNearestKeyId)
        {
            // Gaze moved to different key — reset previous
            if (_currentNearestKeyId != null)
                ResetKeyVisual(_currentNearestKeyId);

            _currentNearestKeyId = nearestId;
            _nearestKeyEnteredTimestamp = timestampMicroseconds;
        }
        else if (nearestId != null)
        {
            // Same key — accumulate dwell
            long elapsed = timestampMicroseconds - _nearestKeyEnteredTimestamp;
            double progress = Math.Min(1.0, (double)elapsed / (_keyDwellMs * 1000L));
            UpdateKeyVisual(nearestId, progress);

            if (elapsed >= _keyDwellMs * 1000L)
            {
                ResetKeyVisual(nearestId);
                _currentNearestKeyId = null;
                OnKeyDwelled(nearestId);
            }
        }
    }

    private bool IsGazeInsideKeyboard(ScreenPoint gazePoint)
    {
        try
        {
            var topLeft = PointToScreen(new Point(0, 0));
            return gazePoint.X >= topLeft.X && gazePoint.X <= topLeft.X + ActualWidth
                && gazePoint.Y >= topLeft.Y && gazePoint.Y <= topLeft.Y + ActualHeight;
        }
        catch
        {
            return false;
        }
    }

    private void UpdatePredictionTargetPositions()
    {
        if (PredictionBar.Children.Count == 0) return;

        for (int i = 0; i < PredictionBar.Children.Count; i++)
        {
            var predId = $"pred_{i}";
            if (PredictionBar.Children[i] is not Border btn) continue;

            try
            {
                var pos = btn.PointToScreen(new Point(btn.ActualWidth / 2, btn.ActualHeight / 2));
                if (!_predictionTargetsInitialized)
                {
                    _predictionDwellDetector.RemoveTarget(predId);
                    _predictionDwellDetector.AddTarget(new DwellTarget(predId, new ScreenPoint(pos.X, pos.Y), 40, _predictionDwellMs));
                }
                else
                {
                    _predictionDwellDetector.UpdateTargetCenter(predId, new ScreenPoint(pos.X, pos.Y));
                }
            }
            catch
            {
                // Not yet rendered
            }
        }
        _predictionTargetsInitialized = true;
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
        _predictionTargetsInitialized = false;
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

    private void UpdateKeyVisual(string targetId, double progress)
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

    private void ResetKeyVisual(string targetId)
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

        visuals.Container.Background = new SolidColorBrush(baseColor);
    }
}
