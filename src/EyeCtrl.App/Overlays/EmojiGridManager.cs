namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EmojiTextBlock = Emoji.Wpf.TextBlock;

public class EmojiGridManager
{
    private const int EmojisPerPage = 40;
    private const int EmojiColumns = 10;

    private static readonly Color KeyColorNormal = Color.FromArgb(200, 50, 50, 50);
    private static readonly Color KeyColorSwitch = Color.FromArgb(200, 50, 70, 110);
    private static readonly Color KeyColorHighlight = Color.FromArgb(200, 0, 100, 0);
    private static readonly Color CategoryBarColor = Color.FromArgb(200, 40, 40, 40);
    private static readonly Color CategoryBarActiveColor = Color.FromArgb(200, 0, 100, 0);
    private static readonly Color EmojiGridBackgroundColor = Color.FromArgb(200, 50, 50, 50);

    private static readonly string[] SkinToneModifiers =
    {
        "",           // 0 = default yellow
        "\U0001F3FB", // 1 = light
        "\U0001F3FC", // 2 = medium-light
        "\U0001F3FD", // 3 = medium
        "\U0001F3FE", // 4 = medium-dark
        "\U0001F3FF", // 5 = dark
    };

    private readonly StackPanel _keyRows;
    private readonly StackPanel _categoryBar;
    private readonly Action<string, KeyDefinition, Border, TextBlock?> _registerKeyVisual;
    private readonly Func<double> _getKeySize;
    private readonly Action _clearKeyState;

    private readonly DwellDetector _categoryDwellDetector = new();
    private bool _categoryTargetsInitialized;

    private EmojiData? _emojiData;
    private RecentEmojiStore? _recentEmojiStore;
    private string _activeEmojiCategory = "recent";
    private int _emojiPage;
    private int _skinTone;
    private string? _emojiJsonPath;
    private string? _recentEmojiPath;
    private int _keyDwellMs;

    public EmojiGridManager(
        StackPanel keyRows,
        StackPanel categoryBar,
        Action<string, KeyDefinition, Border, TextBlock?> registerKeyVisual,
        Func<double> getKeySize,
        Action clearKeyState)
    {
        _keyRows = keyRows;
        _categoryBar = categoryBar;
        _registerKeyVisual = registerKeyVisual;
        _getKeySize = getKeySize;
        _clearKeyState = clearKeyState;
        _categoryDwellDetector.TargetActivated += OnCategoryDwelled;
        _categoryDwellDetector.TargetProgress += OnCategoryProgress;
    }

    public event Action? ExitRequested;
    public RecentEmojiStore? RecentStore => _recentEmojiStore;
    public int SkinTone => _skinTone;

    public void SetEmojiData(string emojiJsonPath, string recentEmojiPath)
    {
        _emojiJsonPath = emojiJsonPath;
        _recentEmojiPath = recentEmojiPath;
    }

    public void SetSkinTone(int skinTone) => _skinTone = Math.Clamp(skinTone, 0, 5);
    public void SetKeyDwellMs(int keyDwellMs) => _keyDwellMs = keyDwellMs;
    public void SetCooldown(int cooldownMs) => _categoryDwellDetector.SetCooldown(cooldownMs);

    public void Enter()
    {
        if (_emojiData == null && _emojiJsonPath != null)
        {
            _emojiData = EmojiDataLoader.Load(_emojiJsonPath);
            _recentEmojiStore = new RecentEmojiStore(_recentEmojiPath ?? "recent_emojis.json");
        }
        if (_emojiData == null) return;

        _activeEmojiCategory = _recentEmojiStore?.Recent.Count > 0 ? "recent" : _emojiData.Categories[0].Id;
        _emojiPage = 0;

        _categoryBar.Visibility = Visibility.Visible;

        BuildCategoryTabs();
        BuildGrid();
    }

    public void Exit()
    {
        _emojiPage = 0;
        _categoryBar.Visibility = Visibility.Collapsed;
    }

    public void BuildGrid()
    {
        _clearKeyState();
        _keyRows.Children.Clear();

        var emojis = GetCurrentPageEmojis();
        var keySize = _getKeySize();

        for (int i = 0; i < emojis.Count; i += EmojiColumns)
        {
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            var rowCount = Math.Min(EmojiColumns, emojis.Count - i);

            for (int j = 0; j < rowCount; j++)
            {
                var baseEmoji = emojis[i + j];
                var displayEmoji = ApplySkinTone(baseEmoji);
                var id = $"emoji_{i + j}";
                var keyDef = new KeyDefinition { Label = displayEmoji, Output = displayEmoji };

                var emojiChild = new EmojiTextBlock
                {
                    Text = displayEmoji,
                    FontSize = 28,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var container = new Border
                {
                    Width = keySize,
                    Height = keySize,
                    Margin = new Thickness(2),
                    Background = new SolidColorBrush(EmojiGridBackgroundColor),
                    CornerRadius = new CornerRadius(6),
                    Child = emojiChild
                };

                _registerKeyVisual(id, keyDef, container, null);
                rowPanel.Children.Add(container);
            }

            _keyRows.Children.Add(rowPanel);
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

        _keyRows.Children.Add(bottomRow);
    }

    public void HandleAction(string action)
    {
        var lowerAction = action.ToLowerInvariant();

        if (lowerAction == "switchtext")
        {
            ExitRequested?.Invoke();
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
                BuildGrid();
            }
            return;
        }

        if (lowerAction == "emojipage:next")
        {
            _emojiPage++;
            BuildGrid();
            return;
        }

        if (lowerAction == "emojipage:prev")
        {
            _emojiPage = Math.Max(0, _emojiPage - 1);
            BuildGrid();
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
                BuildGrid();
            }
            return;
        }

        if (lowerAction == "skintoneback")
        {
            BuildGrid();
        }
    }

    public void TrackEmojiInRecents(string output)
    {
        if (string.IsNullOrEmpty(output)) return;
        var baseEmoji = _skinTone > 0 && output.EndsWith(SkinToneModifiers[_skinTone])
            ? output[..^SkinToneModifiers[_skinTone].Length]
            : output;
        _recentEmojiStore?.Add(baseEmoji);
    }

    public void UpdateCategoryTargets(PhysicalPoint gazePoint, long timestampMicroseconds)
    {
        UpdateCategoryTargetPositions();
        _categoryDwellDetector.Update(gazePoint, timestampMicroseconds);
    }

    private void UpdateCategoryTargetPositions()
    {
        if (_categoryBar.Children.Count == 0 || _emojiData == null) return;

        for (int i = 0; i < _categoryBar.Children.Count; i++)
        {
            if (_categoryBar.Children[i] is not Border btn) continue;
            var catId = i == 0 ? "recent"
                : i <= _emojiData.Categories.Count ? _emojiData.Categories[i - 1].Id
                : "_skinTone";
            var tabId = $"cat_{catId}";

            try
            {
                var pos = btn.PointToScreen(new Point(btn.ActualWidth / 2, btn.ActualHeight / 2));
                if (!_categoryTargetsInitialized)
                {
                    _categoryDwellDetector.RemoveTarget(tabId);
                    _categoryDwellDetector.AddTarget(new DwellTarget(tabId, new PhysicalPoint(pos.X, pos.Y), DpiHelper.LogicalToPhysicalX(20), _keyDwellMs));
                }
                else
                {
                    _categoryDwellDetector.UpdateTargetCenter(tabId, new PhysicalPoint(pos.X, pos.Y));
                }
            }
            catch (InvalidOperationException)
            {
                // Element not yet rendered or not in visual tree
            }
        }
        _categoryTargetsInitialized = true;
    }

    private void BuildCategoryTabs()
    {
        _categoryDwellDetector.ClearTargets();
        _categoryTargetsInitialized = false;
        _categoryBar.Children.Clear();

        AddCategoryTab("recent", "\uD83D\uDD50");

        foreach (var cat in _emojiData!.Categories)
            AddCategoryTab(cat.Id, cat.Icon);

        AddCategoryTab("_skinTone", "\uD83C\uDFA8");

        HighlightActiveCategory();
    }

    private void AddCategoryTab(string id, string icon)
    {
        var tabId = $"cat_{id}";
        var keySize = _getKeySize();
        var btn = new Border
        {
            Width = keySize,
            Height = keySize,
            Margin = new Thickness(2),
            Background = new SolidColorBrush(CategoryBarColor),
            CornerRadius = new CornerRadius(6),
            Child = new TextBlock
            {
                Text = icon,
                FontSize = 22,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        _categoryBar.Children.Add(btn);
        // Use diagonal half-length so the circular hit area covers the full square button
        var hitRadius = DpiHelper.LogicalToPhysicalX(keySize / 2 * Math.Sqrt(2));
        _categoryDwellDetector.AddTarget(new DwellTarget(tabId, new PhysicalPoint(0, 0), hitRadius, _keyDwellMs));
    }

    private void HighlightActiveCategory()
    {
        for (int i = 0; i < _categoryBar.Children.Count; i++)
        {
            if (_categoryBar.Children[i] is Border border)
            {
                var isActive = i == GetActiveCategoryIndex();
                border.Background = new SolidColorBrush(isActive ? CategoryBarActiveColor : CategoryBarColor);
            }
        }
    }

    private int GetActiveCategoryIndex()
    {
        if (_activeEmojiCategory == "recent") return 0;
        if (_emojiData == null) return 0;
        var idx = _emojiData.Categories.FindIndex(c => c.Id == _activeEmojiCategory);
        return idx >= 0 ? idx + 1 : 0;
    }

    private void OnCategoryProgress(string targetId, double progress)
    {
        if (!targetId.StartsWith("cat_")) return;
        var catId = targetId[4..];

        // Find the matching category tab and update its background
        for (int i = 0; i < _categoryBar.Children.Count; i++)
        {
            if (_categoryBar.Children[i] is not Border border) continue;
            var tabCatId = i == 0 ? "recent"
                : (_emojiData != null && i <= _emojiData.Categories.Count) ? _emojiData.Categories[i - 1].Id
                : "_skinTone";
            if (tabCatId != catId) continue;

            var isActive = i == GetActiveCategoryIndex();
            var baseColor = isActive ? CategoryBarActiveColor : CategoryBarColor;
            if (progress <= 0)
            {
                border.Background = new SolidColorBrush(baseColor);
            }
            else
            {
                var highlightColor = Color.FromArgb(200, 0, (byte)(100 + 155 * progress), 0);
                var blended = Color.FromArgb(
                    200,
                    (byte)(baseColor.R * (1 - progress) + highlightColor.R * progress),
                    (byte)(baseColor.G * (1 - progress) + highlightColor.G * progress),
                    (byte)(baseColor.B * (1 - progress) + highlightColor.B * progress));
                border.Background = new SolidColorBrush(blended);
            }
            break;
        }
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
            BuildGrid();
        }
    }

    private void ShowSkinToneSelector()
    {
        _clearKeyState();
        _keyRows.Children.Clear();
        var keySize = _getKeySize();

        var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };

        string[] previews = { "\uD83D\uDC4B", "\uD83D\uDC4B\uD83C\uDFFB", "\uD83D\uDC4B\uD83C\uDFFC", "\uD83D\uDC4B\uD83C\uDFFD", "\uD83D\uDC4B\uD83C\uDFFE", "\uD83D\uDC4B\uD83C\uDFFF" };
        for (int i = 0; i < 6; i++)
        {
            var id = $"skin_{i}";
            var keyDef = new KeyDefinition { Label = previews[i], Output = "", SpecialAction = $"setSkinTone:{i}" };

            var isActive = i == _skinTone;
            var skinChild = new EmojiTextBlock
            {
                Text = previews[i],
                FontSize = 36,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var container = new Border
            {
                Width = keySize * 1.5,
                Height = keySize * 1.5,
                Margin = new Thickness(4),
                Background = new SolidColorBrush(isActive ? KeyColorHighlight : KeyColorNormal),
                CornerRadius = new CornerRadius(8),
                Child = skinChild
            };

            _registerKeyVisual(id, keyDef, container, null);
            rowPanel.Children.Add(container);
        }

        _keyRows.Children.Add(rowPanel);

        var backRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        AddSpecialButton(backRow, "Back", "skinToneBack", 2.0);
        _keyRows.Children.Add(backRow);
    }

    private void AddSpecialButton(StackPanel row, string text, string action, double widthMultiplier)
    {
        var keySize = _getKeySize();
        var id = $"special_{action}";
        var keyDef = new KeyDefinition { Label = text, Output = "", SpecialAction = action };

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
            Width = keySize * widthMultiplier + (widthMultiplier > 1 ? (widthMultiplier - 1) * 4 : 0),
            Height = keySize,
            Margin = new Thickness(2),
            Background = new SolidColorBrush(KeyColorSwitch),
            CornerRadius = new CornerRadius(6),
            Child = label
        };

        _registerKeyVisual(id, keyDef, container, label);
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
}
