namespace EyeCtrl.App.Overlays;

using EyeCtrl.App.Services;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.App.Services;   // KeyboardYield
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
    // Each key's visible box in physical pixels, beside its centre: box-first targeting
    // (KeyTargeting.PickKey). Filled and cleared together with _keyScreenPositions.
    private readonly Dictionary<string, PhysicalRect> _keyScreenBoxes = new();
    private readonly List<DwellOutlineOverlay> _predictionOutlines = new();
    // The list the bar RENDERED. OnPredictionDwelled resolves its index against this, never
    // against a fresh GetSuggestionsWithContext() call: that call re-polls the LM
    // (TryGetResults consumes on read) and re-sorts on merge, so a re-fetch at activation both
    // risks inserting a word that was never on screen and consumes the LM output before the
    // render that follows can show it — the render call is what triggers the inference, so the
    // dwell window is exactly the race window. Do not "simplify" this back to a re-fetch.
    private List<PredictionResult> _renderedPredictions = new();

    private KeyboardViewModel? _viewModel;
    private EmojiGridManager? _emojiManager;
    private KeyboardSoundService? _soundService;
    private EyeCtrlSettings? _settings;
    private readonly IStruggleTelemetrySink? _sink;
    private readonly IDwellDetectorRegistry? _dwellRegistry;

    private KeyboardLayout? _layout;
    private double _keySize;
    // The widest key row, in DIP. The prediction bar is exactly this wide, divided into
    // MaxSuggestions equal slots, so no suggestion can ever resize the keyboard.
    private double _keyGridWidth;
    private int _predictionDwellMs;
    private string _screenEdge = "bottom";
    // Toolbar occupancy for the yield (spec B3 #6): when the toolbar is on screen at this
    // keyboard's edge, RepositionOnScreen shifts the window by its extent toward center.
    private bool _toolbarOccupied;
    private string _toolbarEdge = "none";
    private double _toolbarExtentDip;
    private bool _predictionTargetsInitialized;
    private string? _previousNearestKeyId;

    // E1 (content-free per-key logging) window-side facts. The recorder's clock, so the lines the
    // view-model writes and these share one timebase. None of it is read by behaviour.
    private long _lastMoveMs = long.MinValue;      // last time the window's position actually changed
    private long _barRenderedMs = long.MinValue;   // when the bar's current render happened
    private readonly long[] _slotDwellStartMs = new long[5];  // first progress per slot since render
    private int _slotsVisitedMask;                 // slots whose dwell began at least once since render

    public KeyboardWindow(IStruggleTelemetrySink? sink = null, IDwellDetectorRegistry? dwellRegistry = null)
    {
        InitializeComponent();
        Array.Fill(_slotDwellStartMs, long.MinValue);
        _sink = sink;
        _dwellRegistry = dwellRegistry;
        _predictionDwellDetector = new DwellDetector(cooldownMs: 0,
            context: DwellContext.KeyboardPrediction, sink: sink);
        SizeChanged += (_, _) => RepositionOnScreen();
        _predictionDwellDetector.TargetActivated += OnPredictionDwelled;
        _predictionDwellDetector.TargetProgress += OnPredictionProgress;
        // Prediction dwell times come from KeyboardPredictionDwellMs, so the validated span is the
        // keyboard guard family.
        _dwellRegistry?.Register(this, DwellRoutingRegistration.ForDetector(
            _predictionDwellDetector, DwellContext.KeyboardPrediction,
            AdaptationDwellRanges.KeyboardMinDwellMs, AdaptationDwellRanges.KeyboardMaxDwellMs));
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
        _viewModel.E1Geometry = GetE1Geometry;

        // Re-entry replaces the emoji manager (see the _viewModel guard above): unregister the old
        // one first, or its registry entry would outlive it holding a strong reference to a dead
        // category-tab detector.
        if (_emojiManager != null)
            _dwellRegistry?.Unregister(_emojiManager);

        _emojiManager = new EmojiGridManager(
            KeyRows,
            CategoryBar,
            RegisterKeyVisual,
            () => _keySize,
            ClearKeyState,
            _sink,
            _dwellRegistry);
        // The flag reaction in OnViewModelPropertyChanged owns the exit, so the ABC key and a hide
        // (which resets the flag) flow through the same path. Calling ExitEmojiMode() here as well
        // would rebuild the text grid twice.
        _emojiManager.ExitRequested += () => _viewModel.IsEmojiMode = false;
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

    /// <summary>
    /// Sets the toolbar occupancy the yield consumes (spec B3 #6). Extent is the toolbar
    /// window's ActualHeight in DIP — both windows position in DIP via PlaceAtEdge; do NOT
    /// pass the coordinator's cached physical-px rect.
    /// </summary>
    public void SetToolbarOccupancy(bool occupied, string edge, double extentDip)
    {
        _toolbarOccupied = occupied;
        _toolbarEdge = occupied ? edge : "none";
        _toolbarExtentDip = extentDip;
        RepositionOnScreen();
    }

    public void SetEmojiData(string emojiJsonPath, string recentEmojiPath)
    {
        _emojiManager?.SetEmojiData(emojiJsonPath, recentEmojiPath);
    }

    public void SetSoundService(KeyboardSoundService svc) => _soundService = svc;
    public void SetSettings(EyeCtrlSettings settings)
    {
        _settings = settings;
        // Visibility and the slot count are settings, so an applied change re-lays out the bar
        // (a deliberate action; typing never changes either).
        UpdatePredictions();
    }
    public void SetSkinTone(int skinTone) => _emojiManager?.SetSkinTone(skinTone);

    private void RepositionOnScreen()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        // One read: the yield clamp and the placement must be measured against the SAME
        // work-area snapshot, not two reads of a property that can change mid-call.
        var work = SystemParameters.WorkArea;
        var edge = string.Equals(_screenEdge, "top", StringComparison.OrdinalIgnoreCase) ? "top" : "bottom";
        var (previousLeft, previousTop) = (Left, Top);
        (Left, Top) = OverlayPositioning.PlaceAtEdge(work, edge, ActualWidth, ActualHeight);

        Top += KeyboardYield.VerticalOffset(
            edge, _toolbarOccupied, _toolbarEdge, _toolbarExtentDip,
            Top, ActualHeight, work.Top, work.Bottom);

        // E1: a position change (a Flip, the toolbar yield) silently re-maps a resting gaze onto
        // another key, so the key lines carry the time since the last one.
        if (!(Math.Abs(Left - previousLeft) <= 0.5 && Math.Abs(Top - previousTop) <= 0.5)
            && _viewModel is not null)
            _lastMoveMs = _viewModel.E1.NowMs;
    }

    /// <summary>
    /// What E1's key and abandon lines need from the window: the dock edge, the physical rect and
    /// the time since the window last moved. Called per EVENT (an activation or an abandonment),
    /// never per gaze sample.
    /// </summary>
    private KeyboardE1Geometry GetE1Geometry()
    {
        var edge = string.Equals(_screenEdge, "top", StringComparison.OrdinalIgnoreCase) ? "top" : "bottom";
        var sinceMove = _lastMoveMs == long.MinValue || _viewModel is null
            ? -1 : _viewModel.E1.NowMs - _lastMoveMs;
        return TryGetScreenRect(this, out var rect)
            ? new KeyboardE1Geometry(edge, (int)Math.Round(rect.X), (int)Math.Round(rect.Y),
                (int)Math.Round(rect.Width), (int)Math.Round(rect.Height), sinceMove)
            : new KeyboardE1Geometry(edge, 0, 0, 0, 0, sinceMove);
    }

    private void RebuildKeys()
    {
        if (_layout == null || _viewModel == null) return;
        if (!_layout.SubLayouts.TryGetValue(_viewModel.ActiveSubLayoutId, out var subLayout)) return;

        ClearKeyState();
        KeyRows.Children.Clear();

        // The prediction bar matches the widest row exactly: the key Border is boxWidth plus a
        // 2-DIP margin on each side. Computed from the layout so it is exact before the first
        // arrange; the bar never derives its size from a suggestion.
        _keyGridWidth = 0;
        foreach (var row in subLayout.Rows)
        {
            double rowWidth = 0;
            foreach (var key in row)
                rowWidth += _keySize * key.Width + (key.Width > 1 ? (key.Width - 1) * 4 : 0) + 4;
            _keyGridWidth = Math.Max(_keyGridWidth, rowWidth);
        }

        for (int rowIdx = 0; rowIdx < subLayout.Rows.Count; rowIdx++)
        {
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };

            for (int keyIdx = 0; keyIdx < subLayout.Rows[rowIdx].Count; keyIdx++)
            {
                var key = subLayout.Rows[rowIdx][keyIdx];
                // Special-action keys (backspace, enter, modifiers, etc.) get a STABLE id from their
                // SpecialAction, so they carry the same TargetId across layouts/sub-layouts (the backspace
                // key sits at different row/col per sub-layout, so key_{row}_{col} would be unstable).
                //
                // This convention is SAFETY-CRITICAL now, not telemetry: KeyboardSpecialActions.IsAppEscaping
                // classifies a key by this id, and KeyboardViewModel holds the escaping keys' dwell floor
                // under it. Collapsing the id to a positional form would un-class every escaping key and
                // silently evaporate that floor. The old justification — struggle-detection's Backspace
                // corroboration matching by id — is deliberately DISABLED (AppBootstrapper passes
                // backspaceTargetId: null), so do not read its absence as "nothing depends on the id".
                // Pinned by KeyboardGateProbeTests.SpecialActionKeys_RegisterUnderTheirSpecialActionId.
                // SpecialAction values are unique within a sub-layout (verified per layout), so the id stays
                // unique. Regular keys (no SpecialAction) keep the positional id.
                var id = !string.IsNullOrEmpty(key.SpecialAction) ? key.SpecialAction : $"key_{rowIdx}_{keyIdx}";

                // The box the label must fit: the same width the Border below takes. The
                // symbol-caption fit limit is stated against this number.
                var boxWidth = _keySize * key.Width + (key.Width > 1 ? (key.Width - 1) * 4 : 0);

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
                    //
                    // A symbol caption (⬆ ⇧ ⌫ ⌴ ↵ ⇥ ✂ ↶ ↷ ← ↑ → ↓ 😀) takes the T14 rule
                    // (user's pick, 2026-10-07): x1.4 in the theme's medium weight, fit-limited
                    // to the box. Resolve returns the base font untouched for every other key.
                    FontSize = SymbolCaptionFonts.Resolve(
                        key,
                        Math.Max(_keySize * ContainerRatio.Small, Legibility.MinimumGlyphFontSize),
                        boxWidth),
                    FontWeight = SymbolCaptionRule.IsSymbolCaption(key) ? Tokens.FontWeightMedium : FontWeights.Normal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                // A label frame (round 3: the word-delete erase arrow; round 6: the wide
                // Caps/Shift/Enter arrows) replaces the label's visuals: the label stays the UIA
                // anchor at Opacity 0 and the frame is drawn as geometry around it. Everything
                // that acts on the label still works.
                FrameworkElement captionContent = label;
                if (key.LabelFrame == EraseLabelFrame.EraseLeft)
                    captionContent = EraseLabelFrame.Build(label, _keySize, boxWidth);
                else if (ArrowLabelFrame.IsArrow(key.LabelFrame))
                    captionContent = ArrowLabelFrame.Build(key.LabelFrame!, label, _keySize);

                var keyBrush = new SolidColorBrush(GetKeyBaseColor(key));
                var container = new Border
                {
                    Width = boxWidth,
                    Height = _keySize,
                    Margin = new Thickness(2),
                    Background = keyBrush,
                    CornerRadius = Tokens.StandardCornerRadius
                };
                _keyBrushes[id] = keyBrush;

                System.Windows.Automation.AutomationProperties.SetAutomationId(label, $"Keyboard_Key_{id}");
                // A symbol caption is unpronounceable to a screen reader, so the layout supplies
                // its name ("⌫" -> "Rücktaste"). Set ONLY when supplied: an explicit name freezes
                // a shift-variant key at its resting label, while an unnamed label keeps the
                // peer's live text (ß announces ẞ under Caps).
                if (key.AccessibleName is not null)
                    System.Windows.Automation.AutomationProperties.SetName(label, key.AccessibleName);

                RegisterKeyVisual(id, key, container, label, captionContent);
                rowPanel.Children.Add(container);
            }

            KeyRows.Children.Add(rowPanel);
        }

        // RebuildKeys draws STATIC labels and default key colours (impl review r2 N1/N3):
        // every rebuild path must repaint modifier state afterwards, or a Caps/Shift-on user
        // sees stale visuals — an unlit Caps key they will dwell to "turn on" (toggling it
        // off), a ß that reads lowercase while typing ẞ. Covers LoadLayout (the VM's SetLayout
        // raise repaints, then this rebuild erases it), emoji exit, and sub-layout swaps.
        UpdateModifierVisuals();

        // The bar's width tracks the active sub-layout's grid; rebuild it here so a sub-layout
        // switch cannot leave the window sized for the previous row widths.
        UpdatePredictions();
    }

    public void UpdateGaze(PhysicalPoint screenPoint, long timestampMicroseconds)
    {
        if (_viewModel == null) return;

        UpdateKeyScreenPositions();

        // One predicate, three surfaces. The prediction bar's hit areas are circles of each cell's
        // half-diagonal, which bulge DOWN into the key grid (up to two key rows for a sentence
        // completion), so running both detectors on the same point let a reach for a key complete
        // a suggestion and insert a word the user never dwelled. The emoji category bar has the
        // same shape, one surface later: its tabs' hit areas are the same half-diagonal circles,
        // and the bar sits directly above the grid, so the top band of the first emoji row lies
        // inside the tab circles above it. Without the verdict a reach for a top-row emoji
        // completes the category tab, whose handler rebuilds the grid and empties the key
        // registry under the live dwell. Ticket 3 partitioned prediction from keys on this
        // predicate; the category bar is the surface it did not reach. The partition is the
        // key-area rect, not a per-key distance, and every side receives the same verdict.
        //
        // Both detectors run the same configured dwell from the same sample, so a category
        // activation from the grid is not a race that sometimes loses: the category update is the
        // earlier statement and takes every tie. That is why the shape here is exclusion — a
        // reset after the category fires cannot un-fire it, and deferring the rebuild would hand
        // the user both the category change and the emoji they were reaching for.
        //
        // Boundary dither — a gaze alternating across the rect edge — resets each surface in turn
        // and so types nothing: a narrow dead zone, safe-direction, deliberately not engineered
        // around.
        var gazeInsideKeyArea = TryGetScreenRect(KeyRows, out var keyArea)
            && keyArea.Contains(screenPoint.X, screenPoint.Y);

        if (_viewModel.IsEmojiMode)
        {
            if (!gazeInsideKeyArea)
                _emojiManager?.UpdateCategoryTargets(screenPoint, timestampMicroseconds);
            else
                // Reset, and only reset — same reasoning as the prediction branch below: skipping
                // the update without it would credit the on-grid interval to the tab on the next
                // off-grid sample while that excursion was under the detector's gap clamp.
                _emojiManager?.ResetCategoryState();
        }
        else if (!gazeInsideKeyArea)
        {
            UpdatePredictionTargetPositions();
            _predictionDwellDetector.Update(screenPoint, timestampMicroseconds);
        }
        else
        {
            // Reset, and only reset. Position re-registration is deliberately NOT run here, so a
            // target cannot be re-added with a stale radius while the gaze is on the keys; the
            // first sample back outside refreshes positions (add or update-centre) before it
            // accumulates. That is the reset-then-add order: the reset always precedes any later
            // add. ResetAllState is quiet when repeated (it raises progress 0 only for a target
            // whose IsGazeInside is set), so running it per inside sample emits once, on entry,
            // and the target's outline clears instead of freezing mid-progress.
            _predictionDwellDetector.ResetAllState();
        }

        _viewModel.ProcessKeyGaze(_keyScreenPositions, screenPoint, gazeInsideKeyArea, timestampMicroseconds,
            _keyScreenBoxes);
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
            case nameof(KeyboardViewModel.IsEmojiMode):
                // The flag can change WITHOUT an activation: hiding resets it (T1b), and
                // ActiveSubLayoutId's own reset is frequently a no-op while emoji mode held the
                // underlying sublayout — so without this case nothing rebuilds the text grid and
                // the window sits on the emoji surface while the VM reports text mode. The window
                // owns the emoji surface, so it mirrors the flag. The CategoryBar check keeps this
                // a no-op in text mode, where ExitEmojiMode would rebuild an already-text grid.
                if (_viewModel?.IsEmojiMode == false && CategoryBar.Visibility == Visibility.Visible)
                    ExitEmojiMode();
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

        if (lowerAction == "deleteword")
        {
            _viewModel.HandleDeleteWord();
            UpdatePredictions();
            return;
        }

        if (lowerAction == "addword")
        {
            // UpdatePredictions is the visible half of the contract: the learned word appears in
            // the bar immediately, so the key never dwells into silence (the TrackedEye lesson).
            _viewModel.HandleAddWord();
            UpdatePredictions();
            return;
        }

        if (lowerAction == "enter")
        {
            // E1 mirror check: snapshot and start BEFORE the keystroke goes out; it never waits.
            _viewModel.BeginMirrorCheck("enter");
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
            _viewModel.E1.RecordSpace(_viewModel.LastKeySeq, _renderedPredictions.Count == 0);
            return;
        }

        if (lowerAction.StartsWith("switchsub:"))
        {
            _viewModel.SwitchSubLayout(action[10..]);
            return;
        }

        // "shortcut:<chord>" keys (the Fn clipboard row). The chord rides the original casing,
        // not lowerAction, so "Ctrl+C" parses with the modifier spelled as the presets spell it.
        if (lowerAction.StartsWith("shortcut:"))
        {
            _viewModel.HandleShortcut(action[9..]);
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
        RebuildKeys();
    }

    /// <summary>
    /// Each key's centre AND box, from the same <c>container</c> Border and the same
    /// <c>PointToScreen</c> transform, so both are physical pixels at the real DPI. The box is the
    /// two-corner <see cref="TryGetScreenRect"/>: the Border's own extent, i.e. the visible key
    /// without its Margin, so the 4 DIP gutters stay gaps and keep the nearest-centre rule.
    /// </summary>
    private void UpdateKeyScreenPositions()
    {
        foreach (var (id, (container, _)) in _keyVisuals)
        {
            if (OverlayPositioning.TryGetScreenCenter(container, out var pos))
                _keyScreenPositions[id] = pos;
            if (TryGetScreenRect(container, out var box))
                _keyScreenBoxes[id] = box;
        }
    }

    /// <summary>
    /// The element's on-screen rectangle in physical pixels, both corners through
    /// <see cref="UIElement.PointToScreen"/>. The DIP extents go IN as arguments to the second
    /// corner, never ADDED to a <c>PointToScreen</c> result: that mixed-unit rectangle was the
    /// key-grid defect — at the user's 200% scale it accepted only the grid's top-left quadrant,
    /// leaving 30 of the 40 shipped qwerty keys (and every sub-layout escape key) unreachable.
    /// <c>DpiHelper</c> does not drive <c>PointToScreen</c>; the visual tree's own transform does,
    /// so no manual scaling belongs here. Two-corner precedent in another project:
    /// <c>EyeCtrl.Sdk.Wpf/WpfElementAdapter.TryGetScreenRect</c>.
    ///
    /// <para>
    /// Serves the key-grid gate (see <see cref="UpdateGaze"/>, where it keeps the nearest-key
    /// algorithm from conflicting with prediction-bar and category-bar dwell targets, and
    /// withholds prediction samples while the gaze is on the keys) and the prediction cells'
    /// placement. False when the element is not laid out (zero size) or not in a live presentation
    /// source — the condition the original single-corner form swallowed via its catch, never a
    /// partial rect — and a placement pass must publish nothing from such an element and must not
    /// latch on a pass where any element failed.
    /// </para>
    /// </summary>
    /// <summary>
    /// The keyboard's on-screen bounds in physical pixels, INCLUDING the prediction bar, the category
    /// bar and the key grid — everything a gaze landing there would interact with. False before the
    /// window has been laid out, when there is no rect to publish. Ticket 8's offer chrome rule
    /// consumes this as plain data: no activation button is offered over EyeCtrl's own controls.
    /// </summary>
    public bool TryGetChromeBounds(out PhysicalRect bounds) => TryGetScreenRect(this, out bounds);

    private static bool TryGetScreenRect(FrameworkElement element, out PhysicalRect rect)
    {
        rect = default;
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        try
        {
            var topLeft = element.PointToScreen(new Point(0, 0));
            var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
            rect = new PhysicalRect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void UpdatePredictionTargetPositions()
    {
        // Only the FILLED slots are dwellable. Placeholder cells sit after them and register
        // nothing; iterating the rendered list (not the children) also means a pass can latch
        // with an empty bar instead of being blocked by a child that is not a Border.
        var filled = Math.Min(_renderedPredictions.Count, PredictionBar.Children.Count);
        if (filled == 0) return;

        // Ported from ToolbarWindow's FacesAreArranged/latch shape: guard BEFORE publishing, and
        // latch only when EVERY cell placed. The latch used to fire on a pass that happened,
        // including one where every iteration bailed — the radius-0 targets from unarranged cells
        // and the five targets stacked at screen (0,0) from a source-less pass were permanent,
        // because the else-branch only rebases geometry it was given and can never repair it.
        bool allPlaced = true;
        for (int i = 0; i < filled; i++)
        {
            var predId = $"pred_{i}";
            if (PredictionBar.Children[i] is not Border btn)
            {
                allPlaced = false;
                continue;
            }

            if (!TryGetScreenRect(btn, out var cellRect))
            {
                allPlaced = false;
                continue;
            }

            if (!_predictionTargetsInitialized)
                // The cell's own rectangle. The circumscribed circle this replaces was ~4x the
                // cell's area and its overhang reached into the margins and the key grid.
                _predictionDwellDetector.AddTarget(new DwellTarget(predId, cellRect, _predictionDwellMs));
            else
                // RebaseTarget, NOT UpdateTargetCenter: the latter writes only Center, and
                // HitTest ignores Center once Bounds is set — a silent no-op here that would
                // freeze the hit area where the bar first rendered.
                //
                // RebaseTarget re-applies this registration's stored geometry map, and
                // GeometryRouter always applies one (MakeMap never returns null) — identity with
                // adaptation off, the shipped default, so the rect stays exact here; with
                // adaptation on the routed margin rides the new rect by design.
                _predictionDwellDetector.RebaseTarget(predId, cellRect);
        }

        if (allPlaced)
            _predictionTargetsInitialized = true;
    }

    public void UpdatePredictions()
    {
        var slotCount = Math.Clamp(_settings?.Keyboard.MaxSuggestions ?? 5, 1, 5);
        var suggestions = _viewModel?.GetSuggestionsWithContext() ?? [];

        _predictionDwellDetector.ClearTargets();
        _predictionTargetsInitialized = false;
        _predictionOutlines.Clear();
        PredictionBar.Children.Clear();

        // Capture exactly what gets rendered: OnPredictionDwelled indexes THIS list. Slots past
        // this count are quiet placeholders with no dwell target.
        _renderedPredictions = suggestions.Take(slotCount).ToList();

        // E1: a new render starts the render-to-dwell clock and the slots-visited count over.
        _barRenderedMs = _viewModel?.E1.NowMs ?? long.MinValue;
        Array.Fill(_slotDwellStartMs, long.MinValue);
        _slotsVisitedMask = 0;

        if (PredictionBar is System.Windows.Controls.Primitives.UniformGrid grid)
            grid.Columns = slotCount;
        PredictionBar.Width = _keyGridWidth > 0 ? _keyGridWidth : double.NaN;
        PredictionBar.Visibility = (_settings?.Keyboard.PredictionEnabled ?? true)
            && _viewModel?.IsEmojiMode != true
                ? Visibility.Visible
                : Visibility.Collapsed;

        // Equal slots spanning the key grid exactly: the bar is as wide as the widest row, so
        // no suggestion can ever grow the window, and its height is one key regardless of count.
        var slotWidth = _keyGridWidth > 0 ? _keyGridWidth / slotCount : 0;
        var labelMaxWidth = Math.Max(0, slotWidth - 38); // minus 3+3 margin and 16+16 padding

        for (int i = 0; i < slotCount; i++)
        {
            PredictionBar.Children.Add(i < _renderedPredictions.Count
                ? CreatePredictionCell(i, _renderedPredictions[i], labelMaxWidth)
                : CreateEmptyPredictionCell(i));
        }
    }

    private Border CreatePredictionCell(int index, PredictionResult result, double labelMaxWidth)
    {
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
            HorizontalAlignment = HorizontalAlignment.Center,
            // The slot is a fixed share of the key grid, so a long word trims inside it. The
            // cap is on the label rather than the Border because the badge variant nests this
            // in a horizontal StackPanel, which measures its children with infinite width.
            MaxWidth = labelMaxWidth,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(predLabel, $"Keyboard_Prediction_{index}");

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
            Height = _keySize,
            Margin = new Thickness(3),
            Padding = new Thickness(16, 0, 16, 0),
            Background = brush,
            CornerRadius = Tokens.StandardCornerRadius,
            ClipToBounds = true
        };
        _predictionOutlines.Add(DwellOutlineOverlay.Attach(btn, labelOrStack));
        return btn;
    }

    private Border CreateEmptyPredictionCell(int index)
    {
        var cell = new Border
        {
            Height = _keySize,
            Margin = new Thickness(3),
            Background = new SolidColorBrush(PredictionBarColor),
            CornerRadius = Tokens.StandardCornerRadius,
            ClipToBounds = true
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(cell, $"Keyboard_PredictionEmpty_{index}");
        return cell;
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

        // E1: when did this slot's dwell begin, and which slots has the gaze dwelt on since the
        // render? A few field writes per progress callback; nothing is logged here.
        if (index >= 0 && index < _slotDwellStartMs.Length)
        {
            if (progress <= 0)
                _slotDwellStartMs[index] = long.MinValue;
            else if (_slotDwellStartMs[index] == long.MinValue && _viewModel is not null)
            {
                _slotDwellStartMs[index] = _viewModel.E1.NowMs;
                _slotsVisitedMask |= 1 << index;
            }
        }

        if (index < 0 || index >= _predictionOutlines.Count) return;

        // Same reasoning as the keys: the wash here ran at full strength too, so a suggestion's
        // label collapsed as the user read it — and predictions sit directly above the keys.
        _predictionOutlines[index].SetProgress(progress);
    }

    internal void OnPredictionDwelled(string targetId)
    {
        if (_viewModel == null || !targetId.StartsWith("pred_")) return;

        // Index the RENDERED list — see _renderedPredictions for why a re-fetch here is both a
        // wrong-word hazard and an LM-starvation bug.
        if (int.TryParse(targetId.AsSpan(5), out var index)
            && index >= 0 && index < _renderedPredictions.Count)
        {
            RecordPickE1(index);
            _viewModel.HandlePredictionSelected(_renderedPredictions[index].Word);
            UpdatePredictions();
            _soundService?.PlayKeyClick(); // typing family (D1); AFTER UpdatePredictions so a
                                           // sound failure can never leave the bar stale (S1)
        }
    }

    /// <summary>
    /// E1's pick line: slot, source, LENGTHS (never the word), and the reading fingerprint — the
    /// time from the bar's render to this slot's dwell start, and how many OTHER slots the gaze
    /// dwelt on first. Read before the selection changes CurrentInput.
    /// </summary>
    private void RecordPickE1(int index)
    {
        if (_viewModel is null || !_viewModel.E1.Enabled) return;
        var result = _renderedPredictions[index];
        var dwellStart = index < _slotDwellStartMs.Length ? _slotDwellStartMs[index] : long.MinValue;
        var renderToDwellMs = dwellStart == long.MinValue || _barRenderedMs == long.MinValue
            ? -1 : (int)Math.Max(0, dwellStart - _barRenderedMs);
        var visited = System.Numerics.BitOperations.PopCount((uint)(_slotsVisitedMask & ~(1 << index)));
        _viewModel.E1.RecordPick(index, KeyboardE1Recorder.PickSource(result.Type),
            _viewModel.CurrentInput.Length, result.Word.Length, renderToDwellMs, visited);
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
                // Only keys with a shift variant are written: a key without one has nothing to
                // swap, so the write below has no legitimate effect and exactly one destructive
                // one. Emoji.Wpf.TextBlock.Text HIDES System.Windows.Controls.TextBlock.Text --
                // declared `new`, not an override, so there are two distinct DPs -- and the label
                // slot is typed as the base class, so this write lands on the BASE DP: on an
                // Emoji.Wpf glyph it replaces the EmojiInline with a plain Run and every visible
                // cell renders monochrome after the first activation. It does not recover:
                // re-running the derived setter with the same value never re-fires its callback
                // (the derived DP never changed), and nothing rebuilds the grid after an
                // activation, so the cells stay monochrome until a category or page change.
                // This is not a pointless early return; deleting it is what loses the emoji
                // grid's colour.
                if (string.IsNullOrEmpty(key.ShiftOutput)) continue;

                label.Text = _viewModel.IsShifted ? key.ShiftOutput : key.Label;
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

    /// <summary>
    /// Registers a dwell target and gives it dwell feedback in one act. Every surface that
    /// registers a key visual passes through here — the text grid directly, and the emoji cells,
    /// skin-tone swatches and special buttons through the <see cref="EmojiGridManager"/>
    /// registrar — so "registered as a dwell target" and "has dwell feedback" cannot come apart.
    /// The emoji grid shipped with no feedback for exactly as long as attachment lived at each
    /// call site; a funnel makes that silent omission structurally impossible to repeat.
    /// </summary>
    /// <param name="container">The target's border. Its <c>Child</c> must be unset: the outline
    /// wraps the caption content and installs the wrapper as the Child.</param>
    /// <param name="label">The target's label, kept for the automation name and the id.</param>
    private void RegisterKeyVisual(string id, KeyDefinition keyDef, Border container, TextBlock? label)
        => RegisterKeyVisual(id, keyDef, container, label, label);

    /// <param name="content">The element the outline wraps: the label itself, or the two-line
    /// caption stack whose first line is <paramref name="label"/> (B4).</param>
    private void RegisterKeyVisual(
        string id, KeyDefinition keyDef, Border container, TextBlock? label, FrameworkElement? content)
    {
        _keyOutlines[id] = DwellOutlineOverlay.Attach(container, content);
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
        _keyScreenBoxes.Clear();
    }
}
