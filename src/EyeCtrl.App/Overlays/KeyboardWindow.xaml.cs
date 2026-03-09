namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Core.Keyboard;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels.Messages;

public partial class KeyboardWindow : OverlayWindow
{
    private const double BaseKeySize = 70;

    // Prediction bar uses DwellDetector (small number of circular targets)
    private readonly DwellDetector _predictionDwellDetector = new();
    private readonly DwellDetector _categoryDwellDetector = new();
    private bool _categoryTargetsInitialized;
    private readonly Dictionary<string, (Border Container, TextBlock Label)> _keyVisuals = new();
    private readonly Dictionary<string, KeyDefinition> _keyDefinitions = new();

    // Nearest-key tracking (replaces circular dwell targets for keys)
    private readonly Dictionary<string, ScreenPoint> _keyScreenPositions = new();
    private KeyboardSoundService? _soundService;
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

    // Emoji mode state
    private bool _isEmojiMode;
    private EmojiData? _emojiData;
    private RecentEmojiStore? _recentEmojiStore;
    private EmojiRenderService? _emojiRenderer;
    private string _activeEmojiCategory = "recent";
    private int _emojiPage;
    private int _skinTone; // 0=default, 1-5=light..dark
    private string? _emojiJsonPath;
    private string? _recentEmojiPath;
    private const int EmojisPerPage = 40;
    private const int EmojiColumns = 10;

    private static readonly string[] SkinToneModifiers =
    {
        "",           // 0 = default yellow
        "\U0001F3FB", // 1 = light
        "\U0001F3FC", // 2 = medium-light
        "\U0001F3FD", // 3 = medium
        "\U0001F3FE", // 4 = medium-dark
        "\U0001F3FF", // 5 = dark
    };

    public KeyboardWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => RepositionOnScreen();
        _predictionDwellDetector.TargetActivated += OnPredictionDwelled;
        _categoryDwellDetector.TargetActivated += OnCategoryDwelled;
        Closed += (_, _) => _emojiRenderer?.Dispose();
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

    public void SetEmojiData(string emojiJsonPath, string recentEmojiPath)
    {
        _emojiJsonPath = emojiJsonPath;
        _recentEmojiPath = recentEmojiPath;
    }

    public void SetSoundService(KeyboardSoundService svc) => _soundService = svc;
    public void SetSkinTone(int skinTone) => _skinTone = Math.Clamp(skinTone, 0, 5);
    public int GetSkinTone() => _skinTone;
    public RecentEmojiStore? GetRecentStore() => _recentEmojiStore;

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

                // Set AutomationId on the label (TextBlock) — WPF's UIA control view only
                // exposes TextBlock nodes from transparent overlay windows; Border containers
                // are collapsed out of the UIA tree.
                System.Windows.Automation.AutomationProperties.SetAutomationId(label, $"Keyboard_Key_{id}");

                _keyVisuals[id] = (container, label);
                rowPanel.Children.Add(container);
            }

            KeyRows.Children.Add(rowPanel);
        }
    }

    public void UpdateGaze(ScreenPoint screenPoint, long timestampMicroseconds)
    {
        UpdateKeyScreenPositions();

        if (_isEmojiMode)
        {
            UpdateCategoryTargetPositions();
            _categoryDwellDetector.Update(screenPoint, timestampMicroseconds);
        }
        else
        {
            UpdatePredictionTargetPositions();
            _predictionDwellDetector.Update(screenPoint, timestampMicroseconds);
        }

        ProcessKeyGaze(screenPoint, timestampMicroseconds);
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
        _soundService?.PlayKeyClick();
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
        WeakReferenceMessenger.Default.Send(new KeyActivatedMessage(output));

        // Track emoji in recents
        if (_isEmojiMode && !string.IsNullOrEmpty(output))
        {
            var baseEmoji = _skinTone > 0 && output.EndsWith(SkinToneModifiers[_skinTone])
                ? output[..^SkinToneModifiers[_skinTone].Length]
                : output;
            _recentEmojiStore?.Add(baseEmoji);
        }
    }

    private void HandleSpecialKey(string action)
    {
        var lowerAction = action.ToLowerInvariant();

        if (lowerAction == "switchemoji")
        {
            EnterEmojiMode();
            return;
        }

        if (lowerAction == "switchtext")
        {
            ExitEmojiMode();
            return;
        }

        if (lowerAction.StartsWith("emojicategory:"))
        {
            var catId = action[14..];
            if (_activeEmojiCategory != catId)
            {
                _activeEmojiCategory = catId;
                _emojiPage = 0;
                HighlightActiveCategory();
                BuildEmojiGrid();
            }
            return;
        }

        if (lowerAction == "emojipage:next")
        {
            _emojiPage++;
            BuildEmojiGrid();
            return;
        }

        if (lowerAction == "emojipage:prev")
        {
            _emojiPage = Math.Max(0, _emojiPage - 1);
            BuildEmojiGrid();
            return;
        }

        if (lowerAction == "skintone")
        {
            ShowSkinToneSelector();
            return;
        }

        if (lowerAction.StartsWith("setskintone:"))
        {
            if (int.TryParse(action[12..], out var tone))
            {
                _skinTone = Math.Clamp(tone, 0, 5);
                // Return to emoji grid with new skin tone applied
                BuildEmojiGrid();
            }
            return;
        }

        if (lowerAction == "skintoneback")
        {
            BuildEmojiGrid();
            return;
        }

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
                WeakReferenceMessenger.Default.Send(new KeyActivatedMessage("\b"));
                break;
            case "enter":
                _currentInput = "";
                UpdatePredictions();
                WeakReferenceMessenger.Default.Send(new KeyActivatedMessage("\n"));
                break;
            case "tab":
                WeakReferenceMessenger.Default.Send(new KeyActivatedMessage("\t"));
                break;
            case "space":
                if (_currentInput.Length > 0)
                    _predictor?.LearnWord(_currentInput);
                _currentInput = "";
                UpdatePredictions();
                WeakReferenceMessenger.Default.Send(new KeyActivatedMessage(" "));
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
            if (label == null) continue; // Image-based emoji cells have no TextBlock
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

            var predLabel = new TextBlock
            {
                Text = word,
                Foreground = Brushes.White,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            // Set AutomationId on the TextBlock — Border containers are collapsed from the UIA tree.
            System.Windows.Automation.AutomationProperties.SetAutomationId(predLabel, $"Keyboard_Prediction_{i}");

            var btn = new Border
            {
                MinWidth = 80,
                Height = 36,
                Margin = new Thickness(3),
                Padding = new Thickness(12, 0, 12, 0),
                Background = new SolidColorBrush(Color.FromArgb(200, 40, 80, 40)),
                CornerRadius = new CornerRadius(4),
                Child = predLabel
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
                WeakReferenceMessenger.Default.Send(new PredictionSelectedMessage(word));
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

    // ── Emoji mode ──────────────────────────────────────────────────────

    private void EnterEmojiMode()
    {
        if (_emojiData == null && _emojiJsonPath != null)
        {
            _emojiData = EmojiDataLoader.Load(_emojiJsonPath);
            _recentEmojiStore = new RecentEmojiStore(_recentEmojiPath ?? "recent_emojis.json");
        }
        if (_emojiData == null) return;

        _emojiRenderer ??= new EmojiRenderService();
        _isEmojiMode = true;
        _activeEmojiCategory = _recentEmojiStore?.Recent.Count > 0 ? "recent" : _emojiData.Categories[0].Id;
        _emojiPage = 0;

        PredictionBar.Visibility = Visibility.Collapsed;
        CategoryBar.Visibility = Visibility.Visible;

        BuildCategoryTabs();
        BuildEmojiGrid();
    }

    private void ExitEmojiMode()
    {
        _isEmojiMode = false;
        _emojiPage = 0;

        PredictionBar.Visibility = Visibility.Visible;
        CategoryBar.Visibility = Visibility.Collapsed;

        RebuildKeys();
    }

    private void BuildEmojiGrid()
    {
        _keyVisuals.Clear();
        _keyDefinitions.Clear();
        _keyScreenPositions.Clear();
        _currentNearestKeyId = null;
        KeyRows.Children.Clear();

        var emojis = GetCurrentPageEmojis();

        for (int i = 0; i < emojis.Count; i += EmojiColumns)
        {
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            var rowCount = Math.Min(EmojiColumns, emojis.Count - i);

            for (int j = 0; j < rowCount; j++)
            {
                var baseEmoji = emojis[i + j];
                var displayEmoji = ApplySkinTone(baseEmoji);
                var id = $"emoji_{i + j}";

                _keyDefinitions[id] = new KeyDefinition { Label = displayEmoji, Output = displayEmoji };

                UIElement emojiChild;
                if (_emojiRenderer?.IsAvailable == true)
                {
                    emojiChild = new Image
                    {
                        Source = _emojiRenderer.Get(displayEmoji, 40),
                        Width = 36,
                        Height = 36,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                }
                else
                {
                    // Fallback: monochrome TextBlock (Segoe UI Emoji not available)
                    emojiChild = new TextBlock
                    {
                        Text = displayEmoji,
                        Foreground = Brushes.White,
                        FontSize = 24,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                }

                var container = new Border
                {
                    Width = _keySize,
                    Height = _keySize,
                    Margin = new Thickness(2),
                    Background = new SolidColorBrush(Color.FromArgb(200, 50, 50, 50)),
                    CornerRadius = new CornerRadius(6),
                    Child = emojiChild
                };

                // Label is null for Image cells — safe: UpdateKeyVisual/ResetKeyVisual only use
                // Container.Background; UpdateShiftDisplay only runs in text mode.
                _keyVisuals[id] = (container, null!);
                rowPanel.Children.Add(container);
            }

            KeyRows.Children.Add(rowPanel);
        }

        // Bottom row: pagination + ABC + skin tone
        var bottomRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var totalEmojis = GetCategoryEmojis(_activeEmojiCategory).Count;
        var totalPages = (int)Math.Ceiling((double)totalEmojis / EmojisPerPage);

        if (totalPages > 1 && _emojiPage > 0)
            AddSpecialButton(bottomRow, "\u25C0", "emojiPage:prev", 1.0);

        AddSpecialButton(bottomRow, "ABC", "switchText", 2.0);
        AddSpecialButton(bottomRow, "\uD83C\uDFA8", "skinTone", 1.0);

        if (totalPages > 1 && _emojiPage < totalPages - 1)
            AddSpecialButton(bottomRow, "\u25B6", "emojiPage:next", 1.0);

        KeyRows.Children.Add(bottomRow);
    }

    private void AddSpecialButton(StackPanel row, string text, string action, double widthMultiplier)
    {
        var id = $"special_{action}";
        _keyDefinitions[id] = new KeyDefinition { Label = text, Output = "", SpecialAction = action };

        var label = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var container = new Border
        {
            Width = _keySize * widthMultiplier + (widthMultiplier > 1 ? (widthMultiplier - 1) * 4 : 0),
            Height = _keySize,
            Margin = new Thickness(2),
            Background = new SolidColorBrush(Color.FromArgb(200, 50, 70, 110)),
            CornerRadius = new CornerRadius(6),
            Child = label
        };

        _keyVisuals[id] = (container, label);
        row.Children.Add(container);
    }

    private List<string> GetCategoryEmojis(string categoryId)
    {
        if (categoryId == "recent")
            return _recentEmojiStore?.Recent.ToList() ?? new List<string>();

        return _emojiData?.Categories.FirstOrDefault(c => c.Id == categoryId)?.Emojis
               ?? new List<string>();
    }

    private List<string> GetCurrentPageEmojis()
    {
        var all = GetCategoryEmojis(_activeEmojiCategory);
        return all.Skip(_emojiPage * EmojisPerPage).Take(EmojisPerPage).ToList();
    }

    private string ApplySkinTone(string emoji)
    {
        if (_skinTone == 0) return emoji;
        if (_emojiData == null || !_emojiData.SkinToneBase.Contains(emoji)) return emoji;
        return emoji + SkinToneModifiers[_skinTone];
    }

    // ── Category tabs ─────────────────────────────────────────────────

    private void BuildCategoryTabs()
    {
        _categoryDwellDetector.ClearTargets();
        _categoryTargetsInitialized = false;
        CategoryBar.Children.Clear();

        // Recent tab
        AddCategoryTab("recent", "\uD83D\uDD50");

        // Category tabs
        foreach (var cat in _emojiData!.Categories)
            AddCategoryTab(cat.Id, cat.Icon);

        // Skin tone tab
        AddCategoryTab("_skinTone", "\uD83C\uDFA8");

        HighlightActiveCategory();
    }

    private void AddCategoryTab(string id, string icon)
    {
        var tabId = $"cat_{id}";
        var btn = new Border
        {
            Width = 40,
            Height = 40,
            Margin = new Thickness(2),
            Background = new SolidColorBrush(Color.FromArgb(200, 40, 40, 40)),
            CornerRadius = new CornerRadius(6),
            Child = new TextBlock
            {
                Text = icon,
                FontSize = 20,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        CategoryBar.Children.Add(btn);
        _categoryDwellDetector.AddTarget(new DwellTarget(tabId, new ScreenPoint(0, 0), 20, _keyDwellMs));
    }

    private void HighlightActiveCategory()
    {
        for (int i = 0; i < CategoryBar.Children.Count; i++)
        {
            if (CategoryBar.Children[i] is Border border)
            {
                var isActive = i == GetActiveCategoryIndex();
                border.Background = new SolidColorBrush(isActive
                    ? Color.FromArgb(200, 0, 100, 0)
                    : Color.FromArgb(200, 40, 40, 40));
            }
        }
    }

    private int GetActiveCategoryIndex()
    {
        if (_activeEmojiCategory == "recent") return 0;
        if (_emojiData == null) return 0;
        var idx = _emojiData.Categories.FindIndex(c => c.Id == _activeEmojiCategory);
        return idx >= 0 ? idx + 1 : 0; // +1 for Recent tab at index 0
    }

    private void OnCategoryDwelled(string targetId)
    {
        if (!targetId.StartsWith("cat_")) return;
        var catId = targetId[4..];

        if (catId == "_skinTone")
        {
            ShowSkinToneSelector();
            return;
        }

        if (_activeEmojiCategory != catId)
        {
            _activeEmojiCategory = catId;
            _emojiPage = 0;
            HighlightActiveCategory();
            BuildEmojiGrid();
        }
    }

    private void UpdateCategoryTargetPositions()
    {
        if (CategoryBar.Children.Count == 0) return;

        for (int i = 0; i < CategoryBar.Children.Count; i++)
        {
            if (CategoryBar.Children[i] is not Border btn) continue;
            var catId = i == 0 ? "recent"
                : i <= _emojiData!.Categories.Count ? _emojiData.Categories[i - 1].Id
                : "_skinTone";
            var tabId = $"cat_{catId}";

            try
            {
                var pos = btn.PointToScreen(new Point(btn.ActualWidth / 2, btn.ActualHeight / 2));
                if (!_categoryTargetsInitialized)
                {
                    _categoryDwellDetector.RemoveTarget(tabId);
                    _categoryDwellDetector.AddTarget(new DwellTarget(tabId, new ScreenPoint(pos.X, pos.Y), 20, _keyDwellMs));
                }
                else
                {
                    _categoryDwellDetector.UpdateTargetCenter(tabId, new ScreenPoint(pos.X, pos.Y));
                }
            }
            catch { }
        }
        _categoryTargetsInitialized = true;
    }

    private void ShowSkinToneSelector()
    {
        _keyVisuals.Clear();
        _keyDefinitions.Clear();
        _keyScreenPositions.Clear();
        _currentNearestKeyId = null;
        KeyRows.Children.Clear();

        var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };

        string[] previews = { "\uD83D\uDC4B", "\uD83D\uDC4B\uD83C\uDFFB", "\uD83D\uDC4B\uD83C\uDFFC", "\uD83D\uDC4B\uD83C\uDFFD", "\uD83D\uDC4B\uD83C\uDFFE", "\uD83D\uDC4B\uD83C\uDFFF" };
        for (int i = 0; i < 6; i++)
        {
            var id = $"skin_{i}";
            _keyDefinitions[id] = new KeyDefinition { Label = previews[i], Output = "", SpecialAction = $"setSkinTone:{i}" };

            var isActive = i == _skinTone;
            var label = new TextBlock
            {
                Text = previews[i],
                FontSize = 32,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var container = new Border
            {
                Width = _keySize * 1.5,
                Height = _keySize * 1.5,
                Margin = new Thickness(4),
                Background = new SolidColorBrush(isActive
                    ? Color.FromArgb(200, 0, 100, 0)
                    : Color.FromArgb(200, 50, 50, 50)),
                CornerRadius = new CornerRadius(8),
                Child = label
            };

            _keyVisuals[id] = (container, label);
            rowPanel.Children.Add(container);
        }

        KeyRows.Children.Add(rowPanel);

        // Back button
        var backRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        AddSpecialButton(backRow, "Back", "skinToneBack", 2.0);
        KeyRows.Children.Add(backRow);
    }
}
