namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Core;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

public partial class KeyboardWindow : OverlayWindow
{
    private const double BaseKeySize = 70;

    private static readonly Color KeyColorNormal = Color.FromArgb(200, 50, 50, 50);
    private static readonly Color KeyColorSpecial = Color.FromArgb(200, 60, 60, 80);
    private static readonly Color KeyColorSwitch = Color.FromArgb(200, 50, 70, 110);
    private static readonly Color KeyColorModifierActive = Color.FromArgb(230, 80, 180, 255);
    private static readonly Color PredictionBarColor = Color.FromArgb(200, 40, 80, 40);

    private readonly DwellDetector _predictionDwellDetector = new();
    private readonly Dictionary<string, (Border Container, TextBlock? Label)> _keyVisuals = new();
    private readonly Dictionary<string, KeyDefinition> _keyDefinitions = new();
    private readonly Dictionary<string, PhysicalPoint> _keyScreenPositions = new();
    private readonly List<SolidColorBrush> _predictionBrushes = new();

    private KeyboardViewModel? _viewModel;
    private EmojiGridManager? _emojiManager;
    private KeyboardSoundService? _soundService;

    private KeyboardLayout? _layout;
    private double _keySize;
    private int _predictionDwellMs;
    private string _screenEdge = "bottom";
    private bool _predictionTargetsInitialized;
    private string? _previousNearestKeyId;

    public KeyboardWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => RepositionOnScreen();
        _predictionDwellDetector.TargetActivated += OnPredictionDwelled;
        _predictionDwellDetector.TargetProgress += OnPredictionProgress;
    }

    public void SetViewModel(KeyboardViewModel viewModel)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.KeyDwellCompleted -= OnKeyDwellCompleted;
        }

        _viewModel = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.KeyDwellCompleted += OnKeyDwellCompleted;

        _emojiManager = new EmojiGridManager(
            KeyRows,
            CategoryBar,
            RegisterKeyVisual,
            () => _keySize,
            ClearKeyState);
        _emojiManager.ExitRequested += () =>
        {
            _viewModel.IsEmojiMode = false;
            ExitEmojiMode();
        };
    }

    public void LoadLayout(KeyboardLayout layout, double keySizeMultiplier, int keyDwellMs, int predictionDwellMs, int cooldownMs)
    {
        _layout = layout;
        _keySize = BaseKeySize * keySizeMultiplier;
        _predictionDwellMs = predictionDwellMs;

        _predictionDwellDetector.SetCooldown(cooldownMs);

        _viewModel?.SetLayout(layout);
        _viewModel?.Configure(keyDwellMs, cooldownMs);

        _emojiManager?.SetKeyDwellMs(keyDwellMs);
        _emojiManager?.SetCooldown(cooldownMs);

        RebuildKeys();
    }

    public void SetPredictor(WordPredictor predictor) => _viewModel?.SetPredictor(predictor);

    public void SetScreenEdge(string edge)
    {
        _screenEdge = edge;
        RepositionOnScreen();
    }

    public void SetEmojiData(string emojiJsonPath, string recentEmojiPath)
    {
        _emojiManager?.SetEmojiData(emojiJsonPath, recentEmojiPath);
    }

    public void SetSoundService(KeyboardSoundService svc) => _soundService = svc;
    public void SetSkinTone(int skinTone) => _emojiManager?.SetSkinTone(skinTone);
    public int GetSkinTone() => _emojiManager?.SkinTone ?? 0;
    public RecentEmojiStore? GetRecentStore() => _emojiManager?.RecentStore;

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
        if (_layout == null || _viewModel == null) return;
        if (!_layout.SubLayouts.TryGetValue(_viewModel.ActiveSubLayoutId, out var subLayout)) return;

        ClearKeyState();
        KeyRows.Children.Clear();

        for (int rowIdx = 0; rowIdx < subLayout.Rows.Count; rowIdx++)
        {
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };

            for (int keyIdx = 0; keyIdx < subLayout.Rows[rowIdx].Count; keyIdx++)
            {
                var key = subLayout.Rows[rowIdx][keyIdx];
                var id = $"key_{rowIdx}_{keyIdx}";

                var label = new TextBlock
                {
                    Text = key.Label,
                    Foreground = Brushes.White,
                    FontSize = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var container = new Border
                {
                    Width = _keySize * key.Width + (key.Width > 1 ? (key.Width - 1) * 4 : 0),
                    Height = _keySize,
                    Margin = new Thickness(2),
                    Background = new SolidColorBrush(GetKeyBaseColor(key)),
                    CornerRadius = new CornerRadius(6),
                    Child = label
                };

                System.Windows.Automation.AutomationProperties.SetAutomationId(label, $"Keyboard_Key_{id}");

                RegisterKeyVisual(id, key, container, label);
                rowPanel.Children.Add(container);
            }

            KeyRows.Children.Add(rowPanel);
        }
    }

    public void UpdateGaze(PhysicalPoint screenPoint, long timestampMicroseconds)
    {
        if (_viewModel == null) return;

        UpdateKeyScreenPositions();

        if (_viewModel.IsEmojiMode)
        {
            _emojiManager?.UpdateCategoryTargets(screenPoint, timestampMicroseconds);
        }
        else
        {
            UpdatePredictionTargetPositions();
            _predictionDwellDetector.Update(screenPoint, timestampMicroseconds);
        }

        _viewModel.ProcessKeyGaze(_keyScreenPositions, screenPoint, IsGazeInsideKeyArea(screenPoint), timestampMicroseconds);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(KeyboardViewModel.NearestKeyId):
                if (_previousNearestKeyId != null)
                    ResetKeyVisual(_previousNearestKeyId);
                _previousNearestKeyId = _viewModel?.NearestKeyId;
                break;
            case nameof(KeyboardViewModel.NearestKeyProgress):
                if (_viewModel?.NearestKeyId != null)
                    UpdateKeyVisual(_viewModel.NearestKeyId, _viewModel.NearestKeyProgress);
                break;
            case nameof(KeyboardViewModel.ActiveModifiers):
                UpdateModifierVisuals();
                break;
            case nameof(KeyboardViewModel.ActiveSubLayoutId):
                RebuildKeys();
                break;
        }
    }

    private void OnKeyDwellCompleted(string keyId)
    {
        _soundService?.PlayKeyClick();
        if (!_keyDefinitions.TryGetValue(keyId, out var key) || _viewModel == null) return;

        if (string.IsNullOrEmpty(key.SpecialAction))
        {
            // Compute actual output before ActivateKey changes IsShifted
            var output = _viewModel.IsShifted && !string.IsNullOrEmpty(key.ShiftOutput)
                ? key.ShiftOutput
                : key.Output;

            _viewModel.ActivateKey(key.Output, key.ShiftOutput);

            if (_viewModel.IsEmojiMode)
                _emojiManager?.TrackEmojiInRecents(output);

            UpdatePredictions();
            return;
        }

        var action = key.SpecialAction;
        var lowerAction = action.ToLowerInvariant();

        // Mode switching
        if (lowerAction == "switchemoji")
        {
            _viewModel.IsEmojiMode = true;
            EnterEmojiMode();
            return;
        }

        // Modifier keys
        if (lowerAction == "shift" || lowerAction == "modifier:shift")
        {
            _viewModel.ToggleModifier(ModifierKey.Shift);
            UpdateModifierVisuals();
            return;
        }

        if (lowerAction == "modifier:ctrl")
        {
            _viewModel.ToggleModifier(ModifierKey.Ctrl);
            UpdateModifierVisuals();
            return;
        }

        if (lowerAction == "modifier:alt")
        {
            _viewModel.ToggleModifier(ModifierKey.Alt);
            UpdateModifierVisuals();
            return;
        }

        if (lowerAction == "modifier:win")
        {
            _viewModel.ToggleModifier(ModifierKey.Win);
            UpdateModifierVisuals();
            return;
        }

        if (lowerAction == "toggle:capslock")
        {
            _viewModel.ToggleModifier(ModifierKey.CapsLock);
            UpdateModifierVisuals();
            return;
        }

        if (lowerAction == "backspace")
        {
            _viewModel.HandleBackspace();
            UpdatePredictions();
            return;
        }

        if (lowerAction == "enter")
        {
            _viewModel.HandleEnter();
            UpdatePredictions();
            return;
        }

        if (lowerAction == "tab")
        {
            _viewModel.HandleTab();
            return;
        }

        if (lowerAction == "space")
        {
            _viewModel.HandleSpace();
            UpdatePredictions();
            return;
        }

        if (lowerAction.StartsWith("switchsub:"))
        {
            _viewModel.SwitchSubLayout(action[10..]);
            return;
        }

        // Handle "key:" special actions for function and navigation keys
        if (lowerAction.StartsWith("key:"))
        {
            var keyName = action[4..];  // Extract the key name after "key:"
            _viewModel.HandleSpecialKey(keyName);
            return;
        }

        // Emoji-domain actions — delegate to manager
        _emojiManager?.HandleAction(action);
    }

    private void EnterEmojiMode()
    {
        _predictionDwellDetector.ResetAllState();
        PredictionBar.Visibility = Visibility.Collapsed;
        _emojiManager?.Enter();
    }

    private void ExitEmojiMode()
    {
        _emojiManager?.Exit();
        _predictionDwellDetector.ResetAllState();
        PredictionBar.Visibility = Visibility.Visible;
        RebuildKeys();
    }

    private void UpdateKeyScreenPositions()
    {
        foreach (var (id, (container, _)) in _keyVisuals)
        {
            try
            {
                var pos = container.PointToScreen(new Point(container.ActualWidth / 2, container.ActualHeight / 2));
                _keyScreenPositions[id] = new PhysicalPoint(pos.X, pos.Y);
            }
            catch (InvalidOperationException)
            {
                // Window not yet rendered or element not in visual tree
            }
        }
    }

    /// <summary>
    /// Check if gaze is inside the key grid area (KeyRows panel), NOT the full window.
    /// This prevents the nearest-key algorithm from conflicting with prediction bar
    /// and category bar dwell targets at the top of the window.
    /// </summary>
    private bool IsGazeInsideKeyArea(PhysicalPoint gazePoint)
    {
        try
        {
            if (KeyRows.ActualHeight <= 0) return false;
            var topLeft = KeyRows.PointToScreen(new Point(0, 0));
            return gazePoint.X >= topLeft.X && gazePoint.X <= topLeft.X + KeyRows.ActualWidth
                && gazePoint.Y >= topLeft.Y && gazePoint.Y <= topLeft.Y + KeyRows.ActualHeight;
        }
        catch (InvalidOperationException)
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
                    // Use half-diagonal so the circular hit area covers the full rectangle
                    var halfDiag = Math.Sqrt(btn.ActualWidth * btn.ActualWidth + btn.ActualHeight * btn.ActualHeight) / 2;
                    _predictionDwellDetector.RemoveTarget(predId);
                    _predictionDwellDetector.AddTarget(new DwellTarget(predId, new PhysicalPoint(pos.X, pos.Y), DpiHelper.LogicalToPhysicalX(halfDiag), _predictionDwellMs));
                }
                else
                {
                    _predictionDwellDetector.UpdateTargetCenter(predId, new PhysicalPoint(pos.X, pos.Y));
                }
            }
            catch (InvalidOperationException)
            {
                // Not yet rendered
            }
        }
        _predictionTargetsInitialized = true;
    }

    public void UpdatePredictions()
    {
        if (_viewModel == null) return;

        _predictionDwellDetector.ClearTargets();
        _predictionTargetsInitialized = false;
        _predictionBrushes.Clear();
        PredictionBar.Children.Clear();

        var suggestions = _viewModel.GetSuggestions();

        for (int i = 0; i < suggestions.Count; i++)
        {
            var word = suggestions[i];
            var predId = $"pred_{i}";

            var predLabel = new TextBlock
            {
                Text = word,
                Foreground = Brushes.White,
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(predLabel, $"Keyboard_Prediction_{i}");

            var brush = new SolidColorBrush(PredictionBarColor);
            _predictionBrushes.Add(brush);

            var btn = new Border
            {
                MinWidth = 120,
                Height = _keySize,
                Margin = new Thickness(3),
                Padding = new Thickness(16, 0, 16, 0),
                Background = brush,
                CornerRadius = new CornerRadius(6),
                Child = predLabel
            };

            PredictionBar.Children.Add(btn);
            _predictionDwellDetector.AddTarget(new DwellTarget(predId, new PhysicalPoint(0, 0), DpiHelper.LogicalToPhysicalX(_keySize / 2), _predictionDwellMs));
        }
    }

    private void OnPredictionProgress(string targetId, double progress)
    {
        if (!targetId.StartsWith("pred_")) return;
        if (!int.TryParse(targetId.AsSpan(5), out var index)) return;
        if (index < 0 || index >= _predictionBrushes.Count) return;

        var highlightColor = Color.FromArgb(200, 0, (byte)(100 + 155 * progress), 0);
        _predictionBrushes[index].Color = Color.FromArgb(
            200,
            (byte)(PredictionBarColor.R * (1 - progress) + highlightColor.R * progress),
            (byte)(PredictionBarColor.G * (1 - progress) + highlightColor.G * progress),
            (byte)(PredictionBarColor.B * (1 - progress) + highlightColor.B * progress));
    }

    private void OnPredictionDwelled(string targetId)
    {
        if (_viewModel == null || !targetId.StartsWith("pred_")) return;

        if (int.TryParse(targetId.AsSpan(5), out var index))
        {
            var suggestions = _viewModel.GetSuggestions();
            if (index < suggestions.Count)
            {
                _viewModel.HandlePredictionSelected(suggestions[index]);
                UpdatePredictions();
            }
        }
    }

    private void UpdateKeyVisual(string targetId, double progress)
    {
        if (!_keyVisuals.TryGetValue(targetId, out var visuals)) return;

        var baseColor = GetKeyBaseColor(targetId);
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

        if (_viewModel != null && _keyDefinitions.TryGetValue(targetId, out var key))
        {
            var modifier = GetModifierFromAction(key.SpecialAction);
            if (modifier != null && _viewModel.ActiveModifiers.Contains(modifier.Value))
            {
                visuals.Container.Background = new SolidColorBrush(KeyColorModifierActive);
                return;
            }
        }

        visuals.Container.Background = new SolidColorBrush(GetKeyBaseColor(targetId));
    }

    private Color GetKeyBaseColor(string keyId)
    {
        var key = _keyDefinitions.GetValueOrDefault(keyId);
        return key != null ? GetKeyBaseColor(key) : KeyColorNormal;
    }

    private static ModifierKey? GetModifierFromAction(string? specialAction)
    {
        return specialAction?.ToLowerInvariant() switch
        {
            "shift" or "modifier:shift" => ModifierKey.Shift,
            "modifier:ctrl" => ModifierKey.Ctrl,
            "modifier:alt" => ModifierKey.Alt,
            "modifier:win" => ModifierKey.Win,
            "toggle:capslock" => ModifierKey.CapsLock,
            _ => null
        };
    }

    private static Color GetKeyBaseColor(KeyDefinition key)
    {
        var specialAction = key.SpecialAction;
        var isSwitchSub = specialAction?.StartsWith("switchSub:") ?? false;
        var isSpecial = !string.IsNullOrEmpty(specialAction);
        return isSwitchSub ? KeyColorSwitch : isSpecial ? KeyColorSpecial : KeyColorNormal;
    }

    private void UpdateShiftDisplay()
    {
        if (_viewModel == null) return;
        foreach (var (id, (_, label)) in _keyVisuals)
        {
            if (label == null) continue;
            if (_keyDefinitions.TryGetValue(id, out var key) && string.IsNullOrEmpty(key.SpecialAction))
            {
                label.Text = _viewModel.IsShifted && !string.IsNullOrEmpty(key.ShiftOutput)
                    ? key.ShiftOutput
                    : key.Label;
            }
        }
    }

    private void UpdateModifierVisuals()
    {
        if (_viewModel == null) return;

        foreach (var (id, (container, _)) in _keyVisuals)
        {
            if (!_keyDefinitions.TryGetValue(id, out var key)) continue;
            var modifier = GetModifierFromAction(key.SpecialAction);
            if (modifier == null) continue;

            var isActive = _viewModel.ActiveModifiers.Contains(modifier.Value);
            container.Background = new SolidColorBrush(isActive ? KeyColorModifierActive : GetKeyBaseColor(key));
        }

        UpdateShiftDisplay();
    }

    private void RegisterKeyVisual(string id, KeyDefinition keyDef, Border container, TextBlock? label)
    {
        _keyDefinitions[id] = keyDef;
        _keyVisuals[id] = (container, label);
    }

    private void ClearKeyState()
    {
        _keyVisuals.Clear();
        _keyDefinitions.Clear();
        _keyScreenPositions.Clear();
    }
}
