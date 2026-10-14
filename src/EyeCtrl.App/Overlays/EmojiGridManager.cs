namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Messages;
using Serilog;
using EmojiTextBlock = Emoji.Wpf.TextBlock;

public class EmojiGridManager
{
    private const int EmojisPerPage = 40;
    private const int EmojiColumns = 10;

    /// <summary>
    /// The skin-tone preview cells are one and a half key cells across. Named because the glyph
    /// inside them is sized from the same expression, and RawGeometryLiteralTests rejects a bare
    /// number in a FontSize value even when the value is otherwise ratio-backed.
    /// </summary>
    private const double SkinToneCellScale = 1.5;

    // Palette tokens, matching KeyboardWindow's key ramp — the emoji grid is built inside
    // KeyboardWindow (opaque BrushSurfaceDark background), so these sit on another surface
    // and are correctly opaque (§4.1.1: alpha only where it composites over the desktop).
    private static Color KeyColorNormal => Tokens.SurfaceKey;
    private static Color KeyColorSwitch => Tokens.SurfaceKeySwitch;
    private static Color KeyColorHighlight => Tokens.ToggleOn;
    private static Color CategoryBarColor => Tokens.SurfaceDark;
    /// <summary>Active category tab — the 5th key-surface step, same role as a held modifier.</summary>
    private static Color CategoryBarActiveColor => Tokens.SurfaceKeyActive;
    private static Color EmojiGridBackgroundColor => Tokens.SurfaceMedium;

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
    // The content element is handed to the registrar, never set as the Border's own Child: the
    // registrar wraps it in the dwell outline, and an already-parented child makes Attach throw.
    private readonly Action<string, KeyDefinition, Border, TextBlock?, FrameworkElement?> _registerKeyVisual;
    private readonly Func<double> _getKeySize;
    private readonly Action _clearKeyState;

    private readonly DwellDetector _categoryDwellDetector;
    private readonly List<SolidColorBrush> _categoryBrushes = new();
    // Dwell-completion cue trigger. Optional-trailing so no caller changes; the fallback IS the
    // app's registered singleton (AppBootstrapper registers WeakReferenceMessenger.Default as
    // IMessenger — spec D9), so the default constructor path publishes on the same bus the
    // DwellCueService subscribes to.
    private readonly IMessenger _messenger;
    // Dwell feedback for the category tabs: an accent outline thickening inward. The wash it
    // replaced blended the tile toward Accent at full strength, collapsing its icon's contrast
    // as the user dwelt on it. _categoryBrushes now carries ACTIVE-category state only, which
    // is the one thing that should move that fill.
    private readonly List<DwellOutlineOverlay> _categoryOutlines = new();
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
        Action<string, KeyDefinition, Border, TextBlock?, FrameworkElement?> registerKeyVisual,
        Func<double> getKeySize,
        Action clearKeyState,
        IStruggleTelemetrySink? sink = null,
        IDwellDetectorRegistry? dwellRegistry = null,
        IMessenger? messenger = null)
    {
        // 0 is a construction placeholder, not a value this detector ever evaluates gaze with.
        // KeyboardWindow.LoadLayout calls SetCooldown(settings.Dwell.DwellCooldownMs) (validated
        // 100..5000, DwellSettings) with the same value the key grid gets via Configure, so the
        // two surfaces are deliberately configured identically. Between SetViewModel (which
        // creates this manager) and LoadLayout the detector has no targets — BuildCategoryTabs
        // has not run — so the 0 is unobservable, and changing it would be a no-op dressed as a
        // fix. Read it as "not yet configured", never as an endorsement of a 0 cooldown.
        _categoryDwellDetector = new DwellDetector(cooldownMs: 0, context: DwellContext.Emoji, sink: sink);
        _keyRows = keyRows;
        _categoryBar = categoryBar;
        _registerKeyVisual = registerKeyVisual;
        _getKeySize = getKeySize;
        _clearKeyState = clearKeyState;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _categoryDwellDetector.TargetActivated += OnCategoryDwellActivated;
        _categoryDwellDetector.TargetProgress += OnCategoryProgress;
        // Keyboard range: the category tabs' dwell times come from KeyboardKeyDwellMs, so the emoji
        // grid's validated dwell span is the keyboard guard family, not Standard.
        dwellRegistry?.Register(this, DwellRoutingRegistration.ForDetector(
            _categoryDwellDetector, DwellContext.Emoji,
            AdaptationDwellRanges.KeyboardMinDwellMs, AdaptationDwellRanges.KeyboardMaxDwellMs));
    }

    public event Action? ExitRequested;
    public event Action<int>? SkinToneChanged;
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

        // Mode boundaries and grid rebuilds only. The logging design's event-only policy
        // (docs/plans/2026-03-07-logging-design.md) forbids dwell progress and gaze points, so
        // nothing on this surface logs per sample: OnCategoryProgress stays silent by design.
        // Every line carries ids, counts and outcomes — never a glyph, never the emoji a user
        // selected, because the log is plain text on disk and this grid is their only text-entry
        // surface. No per-cell id either (never "emoji_7"): emojis.json ships in the install
        // directory beside this code, so (category, page, index, tone) is a total function onto
        // the glyph — a cell id would be an ENCODED selection, not an anonymous one. Category,
        // page and cell count are logged because the diagnosis needs them and none names a cell.
        Log.Information("Emoji mode {State}", "entered");
        BuildCategoryTabs();
        BuildGrid();
    }

    public void Exit()
    {
        _emojiPage = 0;
        _categoryBar.Visibility = Visibility.Collapsed;
        Log.Information("Emoji mode {State}", "exited");
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
                    // Sized from the keySize cell below, so the glyph scales with KeySizeMultiplier.
                    FontSize = keySize * ContainerRatio.Large,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var container = new Border
                {
                    Width = keySize,
                    Height = keySize,
                    Margin = new Thickness(2),
                    Background = new SolidColorBrush(EmojiGridBackgroundColor),
                    CornerRadius = Tokens.StandardCornerRadius
                };

                _registerKeyVisual(id, keyDef, container, emojiChild, emojiChild);
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

        // The live cell count is the diagnostic that named the last two defects: a category
        // activation rebuilds this grid, and when it lands on an empty page the count collapses
        // (measured live: 43 -> 2). One line per rebuild, with the count measured from the tree
        // the rebuild just populated, so the log can answer "did a category activation steal an
        // emoji dwell" — while saying nothing about which emoji is where.
        Log.Information("Emoji grid rebuilt: category={Category} page={Page} cells={Cells}",
            _activeEmojiCategory, _emojiPage, CountLiveCells());
    }

    /// <summary>
    /// The dwell cells the grid currently shows — every rebuilt cell is registered through
    /// <see cref="BuildGrid"/>'s or <see cref="ShowSkinToneSelector"/>'s registrar call and lands
    /// in <c>_keyRows</c> as one <see cref="Border"/>. Counted from that tree rather than from a
    /// parallel tally so the logged count cannot drift from what was actually registered.
    /// </summary>
    private int CountLiveCells() =>
        _keyRows.Children.OfType<StackPanel>().Sum(row => row.Children.OfType<Border>().Count());

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
                SkinToneChanged?.Invoke(_skinTone);
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

    /// <summary>
    /// Clears in-progress category-tab dwell — the category side of the positional partition
    /// documented in <c>KeyboardWindow.UpdateGaze</c>, called whenever the gaze is inside the
    /// key/emoji grid. The reset (rather than merely skipping the update) is what makes the
    /// verdict exact: an untouched detector would credit the on-grid interval to the tab on the
    /// next off-grid sample whenever that excursion was under its own 150 ms gap clamp
    /// (<c>DwellDetector.MaxCreditedSampleGapUs</c>), and would leave the tab's outline frozen
    /// mid-progress instead of clearing it. Quiet when repeated — <c>ResetAllState</c> raises
    /// progress 0 only for a target whose <c>IsGazeInside</c> is set, so per-sample calls emit
    /// once, on entry — the same reset-then-add reasoning the prediction branch records.
    /// </summary>
    public void ResetCategoryState() => _categoryDwellDetector.ResetAllState();

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

            if (!OverlayPositioning.TryGetScreenCenter(btn, out var pos))
                continue;
            if (!_categoryTargetsInitialized)
            {
                var keySize = _getKeySize();
                var hitRadius = DpiHelper.LogicalToPhysicalX(keySize / 2 * Math.Sqrt(2));
                _categoryDwellDetector.RemoveTarget(tabId);
                _categoryDwellDetector.AddTarget(new DwellTarget(tabId, pos, hitRadius, _keyDwellMs));
            }
            else
            {
                _categoryDwellDetector.UpdateTargetCenter(tabId, pos);
            }
        }
        _categoryTargetsInitialized = true;
    }

    private void BuildCategoryTabs()
    {
        _categoryDwellDetector.ClearTargets();
        _categoryTargetsInitialized = false;
        _categoryBrushes.Clear();
        _categoryOutlines.Clear();
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
        var brush = new SolidColorBrush(CategoryBarColor);
        _categoryBrushes.Add(brush);
        var btn = new Border
        {
            Width = keySize,
            Height = keySize,
            Margin = new Thickness(2),
            Background = brush,
            CornerRadius = Tokens.StandardCornerRadius
        };

        var iconBlock = new TextBlock
        {
            Text = icon,
            // Clamped at Legibility.MinimumGlyphFontSize: KeySizeMultiplier bottoms out at
            // 0.5 (KeyboardSettings), which puts this tab's icon at 11.20px in a 35px cell —
            // below the 12px accessibility floor design spec §8.4 established. Read from the
            // named minimum rather than from Tokens.FontSizeBody: the two are both 12 today,
            // but the ramp step is gated >= 12, not == 12, so raising body text would
            // silently grow this glyph. Proportional scaling still applies above ~0.54x.
            FontSize = Math.Max(keySize * ContainerRatio.Medium, Legibility.MinimumGlyphFontSize),
            Foreground = Tokens.BrushTextPrimary,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _categoryOutlines.Add(DwellOutlineOverlay.Attach(btn, iconBlock));

        _categoryBar.Children.Add(btn);
        // Use diagonal half-length so the circular hit area covers the full square button
        var hitRadius = DpiHelper.LogicalToPhysicalX(keySize / 2 * Math.Sqrt(2));
        _categoryDwellDetector.AddTarget(new DwellTarget(tabId, new PhysicalPoint(0, 0), hitRadius, _keyDwellMs));
    }

    private void HighlightActiveCategory()
    {
        var activeIdx = GetActiveCategoryIndex();
        for (int i = 0; i < _categoryBrushes.Count; i++)
            _categoryBrushes[i].Color = i == activeIdx ? CategoryBarActiveColor : CategoryBarColor;
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

        for (int i = 0; i < _categoryBrushes.Count; i++)
        {
            var tabCatId = i == 0 ? "recent"
                : (_emojiData != null && i <= _emojiData.Categories.Count) ? _emojiData.Categories[i - 1].Id
                : "_skinTone";
            if (tabCatId != catId) continue;

            // The fill carries ACTIVE-category state only; dwell rides the outline over it.
            // Keeping the two apart is what stops the icon's contrast decaying as the user dwells.
            _categoryBrushes[i].Color = i == GetActiveCategoryIndex()
                ? CategoryBarActiveColor
                : CategoryBarColor;

            if (i < _categoryOutlines.Count)
            {
                _categoryOutlines[i].SetProgress(progress);
            }
            break;
        }
    }

    // Post-effect publish (spec D8): the handler runs first — including its _skinTone
    // early-return — then the cue fires, so no branch of the handler is left silent. The
    // wrapper exists only for that post-effect send — the method group it replaced
    // (+= OnCategoryDwelled) was already unsubscribable by name.
    private void OnCategoryDwellActivated(string targetId)
    {
        OnCategoryDwelled(targetId);
        _messenger.Send(new DwellActivationMessage());
    }

    private void OnCategoryDwelled(string targetId)
    {
        if (!targetId.StartsWith("cat_")) return;
        var catId = targetId[4..];

        if (catId == "_skinTone")
        {
            Log.Information("Emoji category activated: {Category} (skin-tone selector)", targetId);
            ShowSkinToneSelector();
            return;
        }

        var changed = _activeEmojiCategory != catId;
        Log.Information("Emoji category activated: {Category} changed={Changed}", targetId, changed);
        if (changed)
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
                FontSize = keySize * SkinToneCellScale * ContainerRatio.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var container = new Border
            {
                Width = keySize * SkinToneCellScale,
                Height = keySize * SkinToneCellScale,
                Margin = new Thickness(4),
                Background = new SolidColorBrush(isActive ? KeyColorHighlight : KeyColorNormal),
                CornerRadius = Tokens.StandardCornerRadius
            };

            _registerKeyVisual(id, keyDef, container, skinChild, skinChild);
            rowPanel.Children.Add(container);
        }

        _keyRows.Children.Add(rowPanel);

        var backRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        // A language-free caption: the keyboard's own drawn left arrow (T14's wide arrows), a third
        // of the key's width, with the spoken name from the overlays surface. A hard-coded "Back"
        // here read English on a German overlay (holistic review F4), and the caption is the only
        // text a gaze user would see. Drawn rather than a font glyph: the glyph fallback is a hairline.
        AddSpecialButton(backRow, "\u2190", "skinToneBack", 2.0,
            accessibleName: StringTable.Current.T("overlays.edge.back"),
            labelFrame: ArrowLabelFrame.CursorLeft);
        _keyRows.Children.Add(backRow);

        // No changed-guard, unlike the category branch: parked gaze on the skin-tone tab re-fires
        // this per dwell+cooldown cycle, and the tab's own activation line fires with it — two
        // lines per cycle. ~2.4 lines/s / ~203k lines/day at the shipped 450 ms dwell and 400 ms
        // cooldown, up to ~5/s / ~432k/day at the validated floors. At the measured ~80 B/line
        // that is 16-35 MB/day, so even the floors sit at 69 % of the 50 MB cap and never roll.
        // One line per fire is deliberate — deduplicating the repeat would hide the stuck-gaze
        // signal — so the cost is legibility: it buries other lines while it lasts, not rotation.
        // cells= belongs on THIS line, not only on the rebuild it replaced: this is the line that
        // repeats, and without its own count a reader carries the previous rebuild's value
        // forward and misreads the swap.
        Log.Information("Emoji {Panel} shown: cells={Cells}", "skin-tone selector", CountLiveCells());
    }

    /// <param name="text">The caption: a glyph or a layout-data word, never UI prose. Prose is a
    /// sink of the text-literal ratchet, so a hard-coded sentence here fails the gate.</param>
    /// <param name="accessibleName">The screen-reader name of a caption nobody can pronounce
    /// (a glyph), resolved from the string table by the caller; null leaves the caption as the name.</param>
    /// <param name="labelFrame">One of the keyboard's drawn arrows (<see cref="ArrowLabelFrame"/>): the
    /// caption becomes the invisible automation anchor and the arrow is drawn a third of the key's
    /// width, in the keyboard's own stroke; null draws the caption as text.</param>
    private void AddSpecialButton(
        StackPanel row, string text, string action, double widthMultiplier, string? accessibleName = null,
        string? labelFrame = null)
    {
        var keySize = _getKeySize();
        var id = $"special_{action}";
        var keyDef = new KeyDefinition { Label = text, Output = "", SpecialAction = action, LabelFrame = labelFrame };
        var boxWidth = keySize * widthMultiplier + (widthMultiplier > 1 ? (widthMultiplier - 1) * 4 : 0);

        var label = new TextBlock
        {
            Text = text,
            Foreground = Tokens.BrushTextPrimary,
            // Clamped at Legibility.MinimumGlyphFontSize for the reason recorded at
            // AddCategoryTab: KeySizeMultiplier's 0.5 floor would otherwise put this label at
            // 8.05px, under the 12px accessibility floor (§8.4). Proportional scaling still
            // applies above ~0.75x.
            FontSize = Math.Max(keySize * ContainerRatio.Small, Legibility.MinimumGlyphFontSize),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (accessibleName is not null)
            System.Windows.Automation.AutomationProperties.SetName(label, accessibleName);

        FrameworkElement captionContent = label;
        if (labelFrame is not null)
        {
            // The keyboard's symbol font (base x the T14 scale), so the arrow's stroke is the one
            // the drawn arrows on the keyboard have.
            label.FontSize *= SymbolCaptionRule.Scale;
            captionContent = ArrowLabelFrame.Build(labelFrame, label, keySize, inkWidth: boxWidth / 3);
        }

        var container = new Border
        {
            Width = boxWidth,
            Height = keySize,
            Margin = new Thickness(2),
            Background = new SolidColorBrush(KeyColorSwitch),
            CornerRadius = Tokens.StandardCornerRadius
        };

        _registerKeyVisual(id, keyDef, container, label, captionContent);
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
