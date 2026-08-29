namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Controls;
using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Controls.Theme;
using EyeCtrl.ViewModels;

public partial class KeyboardWindow : OverlayWindow
{
    private const double BaseKeySize = 70;

    private static Color KeyColorNormal => Tokens.SurfaceKey;
    private static Color KeyColorSpecial => Tokens.SurfaceKeyModifier;
    private static Color KeyColorSwitch => Tokens.SurfaceKeySwitch;
    // Active modifier (e.g. held Shift) takes the 5th key-surface step (SurfaceKeyActive),
    // a brighter blue than the resting switch keys and distinct from every ramp step (gate 8)
    // and from the cyan Accent reserved for dwell-in-progress.
    private static Color KeyColorModifierActive => Tokens.SurfaceKeyActive;
    private static Color PredictionBarColor => Tokens.SurfacePrediction;

    // Cached frozen brushes for prediction foreground colors (performance optimization)
    private static Brush ForegroundExactPrefix => Tokens.BrushTextPrimary;
    private static Brush ForegroundFuzzyMatch => Tokens.BrushTextSecondary;
    private static Brush ForegroundNextWord => Tokens.BrushAccentSuccess;
    private static Brush ForegroundPhrase => Tokens.BrushAccentWarning;
    private static Brush ForegroundSentenceCompletion => Tokens.BrushAccent;
    private static Brush ForegroundLMSuggestion => Tokens.BrushTextPrimary;

    private readonly DwellDetector _predictionDwellDetector;
    private readonly Dictionary<string, (Border Container, TextBlock? Label)> _keyVisuals = new();
    // One mutable brush per key, installed as its Container.Background and mutated in place by
    // ResetKeyVisual/UpdateModifierVisuals on a STATE change (modifier active/inactive).
    // Dwell progress no longer touches it — that is _keyOutlines — so the surface a key's label
    // sits on holds still for the whole dwell.
    private readonly Dictionary<string, SolidColorBrush> _keyBrushes = new();
    // Dwell feedback is an accent outline thickening inward from each key's edge, NOT a wash
    // over the key's fill. The wash collapsed a normal key's label from 10.42:1 to 1.08:1 at
    // full dwell (Midnight; 1.18 Ember, 1.25 Contrast) — see DwellOutlineOverlay for why no
    // choice of wash target repairs that on a dark key with a light label.
    private readonly Dictionary<string, DwellOutlineOverlay> _keyOutlines = new();
    private readonly Dictionary<string, KeyDefinition> _keyDefinitions = new();
    private readonly Dictionary<string, PhysicalPoint> _keyScreenPositions = new();
    private readonly List<DwellOutlineOverlay> _predictionOutlines = new();

    private KeyboardViewModel? _viewModel;
    private EmojiGridManager? _emojiManager;
    private KeyboardSoundService? _soundService;
    private EyeCtrlSettings? _settings;
    private readonly IStruggleTelemetrySink? _sink;

    private KeyboardLayout? _layout;
    private double _keySize;
    private int _predictionDwellMs;
    private string _screenEdge = "bottom";
    private bool _predictionTargetsInitialized;
    private string? _previousNearestKeyId;

    public KeyboardWindow(IStruggleTelemetrySink? sink = null)
    {
        InitializeComponent();
        _sink = sink;
        _predictionDwellDetector = new DwellDetector(cooldownMs: 0,
            context: DwellContext.KeyboardPrediction, sink: sink);
        SizeChanged += (_, _) => RepositionOnScreen();
        _predictionDwellDetector.TargetActivated += OnPredictionDwelled;
        _predictionDwellDetector.TargetProgress += OnPredictionProgress;
    }

    public event Action<int>? SkinToneChanged;

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
            ClearKeyState,
            _sink);
        _emojiManager.ExitRequested += () =>
        {
            _viewModel.IsEmojiMode = false;
            ExitEmojiMode();
        };
        _emojiManager.SkinToneChanged += tone => SkinToneChanged?.Invoke(tone);
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

    public void SetOrchestrator(PredictionOrchestrator? orchestrator) => _viewModel?.SetOrchestrator(orchestrator);

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
    public void SetSettings(EyeCtrlSettings settings) => _settings = settings;
    public void SetSkinTone(int skinTone) => _emojiManager?.SetSkinTone(skinTone);

    private void RepositionOnScreen()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var edge = string.Equals(_screenEdge, "top", StringComparison.OrdinalIgnoreCase) ? "top" : "bottom";
        (Left, Top) = OverlayPositioning.PlaceAtEdge(SystemParameters.WorkArea, edge, ActualWidth, ActualHeight);
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
                // Special-action keys (backspace, enter, modifiers, etc.) get a STABLE id from their
                // SpecialAction, so they carry the same TargetId across layouts/sub-layouts (the backspace
                // key sits at different row/col per sub-layout, so key_{row}_{col} would be unstable). This
                // lets struggle-detection's accidental-activation corroboration match it by id. SpecialAction
                // values are unique within a sub-layout (verified per layout), so the id stays unique. Regular
                // keys (no SpecialAction) keep the positional id.
                var id = !string.IsNullOrEmpty(key.SpecialAction) ? key.SpecialAction : $"key_{rowIdx}_{keyIdx}";

                var label = new TextBlock
                {
                    Text = key.Label,
                    Foreground = Tokens.BrushTextPrimary,
                    // Sized from the key that bounds it, so raising KeySizeMultiplier now grows
                    // the letter with the key instead of leaving it at a fixed 16.
                    //
                    // Clamped at Legibility.MinimumGlyphFontSize because KeySizeMultiplier
                    // bottoms out at 0.5 (KeyboardSettings), which puts a 35px key's label at
                    // 8.05px — below the 12px accessibility floor design spec §8.4 established.
                    // The clamp keeps the glyph legible while proportional scaling still applies
                    // above roughly 0.75x.
                    //
                    // The floor is its own named constant rather than Tokens.FontSizeBody. Both
                    // are 12 today, but FontSizeBody is a step on the type ramp and the guards
                    // gate it >= 12, not == 12 — so raising body text to 14 would silently grow
                    // this glyph, and three others, at the bottom of the multiplier range. A
                    // minimum also has no business being theme-dependent.
                    FontSize = Math.Max(_keySize * ContainerRatio.Small, Legibility.MinimumGlyphFontSize),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var keyBrush = new SolidColorBrush(GetKeyBaseColor(key));
                var container = new Border
                {
                    Width = _keySize * key.Width + (key.Width > 1 ? (key.Width - 1) * 4 : 0),
                    Height = _keySize,
                    Margin = new Thickness(2),
                    Background = keyBrush,
                    CornerRadius = Tokens.StandardCornerRadius
                };
                _keyBrushes[id] = keyBrush;
                _keyOutlines[id] = DwellOutlineOverlay.Attach(container, label);

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

        // Modifier keys — use GetModifierFromAction to avoid duplicating the action→ModifierKey mapping
        var modifier = GetModifierFromAction(action);
        if (modifier != null)
        {
            _viewModel.ToggleModifier(modifier.Value);
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
            var keyName = lowerAction[4..];  // Extract the normalized key name after "key:"
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
        _predictionOutlines.Clear();
        PredictionBar.Children.Clear();

        var suggestions = _viewModel.GetSuggestionsWithContext();

        for (int i = 0; i < suggestions.Count; i++)
        {
            var result = suggestions[i];
            var predId = $"pred_{i}";

            var predLabel = new TextBlock
            {
                Text = result.Word,
                Foreground = GetPredictionForeground(result.Type),
                // Sized from the prediction cell, which is _keySize tall (see the Border below),
                // clamped at Legibility.MinimumGlyphFontSize for the reason recorded at the
                // key-label site: KeySizeMultiplier's 0.5 floor would otherwise put this at
                // 8.05px, under the 12px accessibility floor (§8.4). Proportional scaling still
                // applies above ~0.75x.
                FontSize = Math.Max(_keySize * ContainerRatio.Small, Legibility.MinimumGlyphFontSize),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(predLabel, $"Keyboard_Prediction_{i}");

            // Calculate background color with optional confidence shading
            var baseColor = PredictionBarColor;
            if (_settings?.Keyboard.ShowPredictionConfidence == true)
            {
                // Clamp and validate confidence to 0-1 range before converting to alpha
                var confidence = result.Confidence;
                if (double.IsNaN(confidence) || double.IsInfinity(confidence))
                {
                    confidence = 0;
                }
                confidence = Math.Clamp(confidence, 0.0, 1.0);

                // Adjust alpha based on confidence (150-230 range for visibility)
                var alpha = (byte)(150 + (confidence * 80));
                baseColor = Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);
            }

            var brush = new SolidColorBrush(baseColor);

            // Optionally add type badge
            UIElement labelOrStack = predLabel;
            if (_settings?.Keyboard.ShowPredictionTypes == true)
            {
                var badge = CreateTypeBadge(result.Type);
                if (badge != null)
                {
                    var stack = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    stack.Children.Add(predLabel);
                    stack.Children.Add(badge);
                    labelOrStack = stack;
                }
            }

            var btn = new Border
            {
                MinWidth = 120,
                Height = _keySize,
                Margin = new Thickness(3),
                Padding = new Thickness(16, 0, 16, 0),
                Background = brush,
                CornerRadius = Tokens.StandardCornerRadius
            };
            _predictionOutlines.Add(DwellOutlineOverlay.Attach(btn, labelOrStack));

            PredictionBar.Children.Add(btn);
            _predictionDwellDetector.AddTarget(new DwellTarget(predId, new PhysicalPoint(0, 0), DpiHelper.LogicalToPhysicalX(_keySize / 2), _predictionDwellMs));
        }
    }

    /// <summary>
    /// Gets the foreground color for a prediction based on its type.
    /// Uses cached frozen brushes for better WPF rendering performance.
    /// </summary>
    private static Brush GetPredictionForeground(PredictionType type)
    {
        return type switch
        {
            PredictionType.ExactPrefix => ForegroundExactPrefix,
            PredictionType.FuzzyMatch => ForegroundFuzzyMatch,
            PredictionType.NextWord => ForegroundNextWord,
            PredictionType.Phrase => ForegroundPhrase,
            PredictionType.SentenceCompletion => ForegroundSentenceCompletion,
            PredictionType.LMSuggestion => ForegroundLMSuggestion,
            _ => ForegroundExactPrefix
        };
    }

    /// <summary>
    /// Creates an optional type badge for a prediction.
    /// </summary>
    private static Border? CreateTypeBadge(PredictionType type)
    {
        string? symbol = type switch
        {
            PredictionType.NextWord => "\u2192",
            PredictionType.Phrase => "\u22EF",
            PredictionType.FuzzyMatch => "~",
            PredictionType.SentenceCompletion => "\u2606",
            PredictionType.LMSuggestion => "\u2726",
            _ => null
        };

        if (symbol == null) return null;

        return new Border
        {
            Child = new TextBlock
            {
                Text = symbol,
                FontSize = Tokens.FontSizeBody,
                Foreground = Tokens.BrushTextDim,
                Margin = new Thickness(4, 0, 0, 0)
            }
        };
    }

    private void OnPredictionProgress(string targetId, double progress)
    {
        if (!targetId.StartsWith("pred_")) return;
        if (!int.TryParse(targetId.AsSpan(5), out var index)) return;
        if (index < 0 || index >= _predictionOutlines.Count) return;

        // Same reasoning as the keys: the wash here ran at full strength too, so a suggestion's
        // label collapsed as the user read it — and predictions sit directly above the keys.
        _predictionOutlines[index].SetProgress(progress);
    }

    private void OnPredictionDwelled(string targetId)
    {
        if (_viewModel == null || !targetId.StartsWith("pred_")) return;

        if (int.TryParse(targetId.AsSpan(5), out var index))
        {
            var suggestions = _viewModel.GetSuggestionsWithContext();
            if (index < suggestions.Count)
            {
                _viewModel.HandlePredictionSelected(suggestions[index].Word);
                UpdatePredictions();
            }
        }
    }

    private void UpdateKeyVisual(string targetId, double progress)
    {
        // Membership check only — the visual itself is not needed here any more, now that dwell
        // rides _keyOutlines rather than the key's own brush.
        if (!_keyVisuals.ContainsKey(targetId)) return;

        // Thicken the outline; the key's fill and label are left alone, so the label's
        // contrast at the end of a dwell equals its contrast at rest.
        if (_keyOutlines.TryGetValue(targetId, out var outline))
        {
            outline.SetProgress(progress);
        }
    }

    private void ResetKeyVisual(string targetId)
    {
        // Clear the outline first: it is independent of the fill below, so an early return on a
        // missing brush must not leave a half-drawn outline behind.
        if (_keyOutlines.TryGetValue(targetId, out var outline))
        {
            outline.SetProgress(0);
        }

        if (!_keyVisuals.TryGetValue(targetId, out var visuals)) return;
        if (!_keyBrushes.TryGetValue(targetId, out var brush)) return;

        if (_viewModel != null && _keyDefinitions.TryGetValue(targetId, out var key))
        {
            var modifier = GetModifierFromAction(key.SpecialAction);
            if (modifier != null && _viewModel.ActiveModifiers.Contains(modifier.Value))
            {
                brush.Color = KeyColorModifierActive;
                return;
            }
        }

        brush.Color = GetKeyBaseColor(targetId);
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
            if (!_keyBrushes.TryGetValue(id, out var brush)) continue;
            var modifier = GetModifierFromAction(key.SpecialAction);
            if (modifier == null) continue;

            var isActive = _viewModel.ActiveModifiers.Contains(modifier.Value);
            brush.Color = isActive ? KeyColorModifierActive : GetKeyBaseColor(key);
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
        _keyBrushes.Clear();
        _keyOutlines.Clear();
        _keyDefinitions.Clear();
        _keyScreenPositions.Clear();
    }
}
