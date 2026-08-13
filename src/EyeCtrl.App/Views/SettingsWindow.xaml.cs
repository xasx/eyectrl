namespace EyeCtrl.App.Views;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

public partial class SettingsWindow : OverlayWindow
{
    // ────── Constants ──────

    // internal: page sources reference the control-radius standard (every content dwell target but
    // the colour swatches uses it). Not test-coupled today; promoted for the extraction.
    internal const double ControlBtnRadius = 50;
    // internal: CursorPageSource passes the swatch radius; SelectionPageSource passes the control radius.
    internal const double ColorSwatchRadius = 35;
    private const double TabBtnRadius = 85;
    private const double PagBtnRadius = 60;
    private const double ModalBtnRadius = 70;

    // ── Container sizes that glyph sizes derive from ──
    // Re-added after commit 691a494 dropped them as dead. They are NOT dead now: the tab icon
    // and pagination symbol are sized as a ratio of the container that bounds them, so the
    // number must live in exactly one place or the two drift apart again — which is how they
    // came to be dead in the first place. Each is wired to the Border that uses it.

    /// <summary>
    /// The tab Border's height, and the container the tab icon's size is a ratio of.
    ///
    /// <c>internal</c> for the same reason as <c>QuickActionsGridWindow.CloseButtonSize</c>:
    /// SettingsTabLabelFitTests re-measures the tab labels against the sidebar column and must
    /// derive the icon size from this exact const rather than restate it. SettingsWindow itself
    /// cannot be constructed in a test (eight non-optional dependencies), so the test rebuilds the
    /// tab's element tree, and every input it feeds that tree has to come from production.
    /// </summary>
    internal const double TabContainerHeight = 100;
    private const double PaginationButtonWidth = 100;
    private const double PaginationButtonHeight = 80;
    private const double ModalButtonHeight = 80;

    // Window sizing
    //
    // internal for the same reason as TabContainerHeight: SettingsContentFitTests re-derives the
    // content area's width at a hypothetical narrow display and must scale the screen by the very
    // ratio production uses, not by a copy of it.
    internal const double WindowSizeRatio = 0.9; // Settings window size as fraction of screen

    /// <summary>
    /// The width of a quick-action picker button.
    ///
    /// A named const rather than a literal at the call site because
    /// <see cref="SettingsContentFitTests"/> measures this exact button against the content
    /// column: it is the widest control the settings content area ever holds, so it is the one
    /// that clips first. Widening it is a layout decision that has to be re-measured, and a
    /// literal buried in <see cref="QuickActionsPageSource.ChunkPresets"/> hides that.
    /// </summary>
    internal const double PickerButtonWidth = 460;

    /// <summary>
    /// The narrowest a content-area dwell control may ever be squeezed to.
    ///
    /// Not an aesthetic floor — a hit-geometry one. <see cref="DwellControl.UpdatePosition"/>
    /// registers a target as its Root's *centre* plus <see cref="ControlBtnRadius"/> (converted
    /// to physical pixels), so the gaze-activatable region is a circle of that radius about the
    /// centre, independent of how wide the Border is drawn. Narrower than the diameter and the
    /// circle starts sticking out past the button's own visual on both sides: the user would be
    /// activating a control at a point where nothing is drawn. So the constraint below never
    /// shrinks a control past the hit circle it owns, even on an absurdly narrow display.
    /// </summary>
    internal const double MinimumDwellableWidth = ControlBtnRadius * 2;

    // Expression-bodied properties (not fields): Tokens now forwards to the active ITheme
    // (ITheme slice), so these must re-evaluate on access rather than snapshot Midnight at
    // type-init. ModalYes/ModalNo are gone — Task 12's modal generalization moved modal
    // colours into ModalButtonPalette (per-kind, contrast-tested).
    private static Color BtnBase => Tokens.SurfaceMedium;
    private static Color TabActive => Tokens.SurfaceKeySwitch;
    private static Color TabInactive => Tokens.SurfaceKey;
    private static Color ResetBase => Tokens.AccentWarning;
    private static Color CloseBase => Tokens.AccentDanger;
    private static Color DeleteBase => Tokens.AccentWarning;

    // ────── State ──────

    private readonly SettingsWindowViewModel _viewModel;
    private readonly EyeCtrlSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly ModelRegistryService _registryService;
    private readonly ModelManagementService _modelManagementService;
    private readonly IMessenger _messenger;
    private readonly CalibrationService _calibrationService;
    private readonly AppRestartCoordinator _restartCoordinator;
    private readonly int _dwellMs;

    private readonly SettingsPageContext _ctx;
    private readonly CursorPageSource _cursorSource;
    private readonly DwellPageSource _dwellSource;
    private readonly SelectionPageSource _selectionSource;
    private readonly KeyboardPageSource _keyboardSource;
    private readonly ToolbarPageSource _toolbarSource;
    private readonly ActionMenuPageSource _actionMenuSource;
    private readonly LayoutPageSource _layoutSource;
    private readonly GeneralPageSource _generalSource;
    private readonly CalibrationPageSource _calibrationSource;
    private readonly ModelsPageSource _modelsSource;
    private readonly QuickActionsPageSource _qaSource;

    private readonly Dictionary<string, Border> _tabButtons = new();
    private readonly Dictionary<string, Border> _pagButtons = new();
    private readonly Dictionary<string, Border> _modalButtons = new();

    // One mutable brush per button, mutated in place on dwell ticks (mirrors KeyboardWindow._keyBrushes).
    // Never allocated per-tick: UpdateXxxVisuals only sets .Color, so dwell highlight has zero GC churn.
    private readonly Dictionary<string, SolidColorBrush> _tabBrushes = new();
    private readonly Dictionary<string, SolidColorBrush> _pagBrushes = new();
    private readonly Dictionary<string, SolidColorBrush> _modalBrushes = new();
    // Base fill per modal button id, captured at build time so UpdateModalVisuals can
    // blend each button's dwell progress from its own base rather than a single shared one.
    private readonly Dictionary<string, Color> _modalBaseColors = new();
    // Per-id dwell blend factor from ModalButtonPalette. The dark Neutral fill's light label
    // would collapse (to ~2.6:1 at full dwell) at the bright kinds' 0.5 factor; the palette
    // sizes the factor so the label stays legible through the whole dwell. See ModalButtonPalette.
    private readonly Dictionary<string, double> _modalBlendFactors = new();
    private SolidColorBrush? _closeButtonBrush;

    private IReadOnlyList<PageSpec> _pages = Array.Empty<PageSpec>();
    private readonly List<object> _activeItems = new(); // DwellControl | IDwellComposite

    public event Action<EyeCtrlSettings>? SettingsChanged;

    private readonly (string Id, string Icon, string Label, Func<IReadOnlyList<PageSpec>> PageBuilder)[] _tabDefs;
    private readonly Dictionary<string, Func<IReadOnlyList<PageSpec>>> _pageBuilders = new();

    public SettingsWindow(
        SettingsWindowViewModel viewModel,
        EyeCtrlSettings settings,
        SettingsService settingsService,
        ModelRegistryService registryService,
        ModelManagementService modelManagementService,
        IMessenger messenger,
        CalibrationService calibrationService,
        AppRestartCoordinator restartCoordinator)
    {
        InitializeComponent();

        // Replace the XAML-provided frozen background (shared resource BrushAccentDanger) with a
        // mutable brush so UpdateTabVisuals can mutate .Color in place on dwell ticks without
        // allocating. CloseBase == Tokens.AccentDanger, matching the XAML resource.
        _closeButtonBrush = new SolidColorBrush(CloseBase);
        CloseButton.Background = _closeButtonBrush;

        _viewModel = viewModel;
        _settings = settings;
        _settingsService = settingsService;
        _registryService = registryService;
        _modelManagementService = modelManagementService;
        _messenger = messenger;
        _calibrationService = calibrationService;
        _restartCoordinator = restartCoordinator;
        _dwellMs = settings.Dwell.ToolbarButtonDwellMs;

        // An accepted restart must also be visible. The handshake allows the replacement up
        // to 30s to announce itself; the dispatcher stays free, but silence for that long is
        // indistinguishable from a hang, and the in-flight guard drops a second dwell without
        // saying so. Replaced automatically by the refusal notice below if it fails, and
        // taken down with the process if it succeeds.
        //
        // The one button only HIDES the notice — it cannot cancel a restart whose child may
        // already be running, and it must exist: a modal with no dwell target would trap a
        // gaze-only user if the outcome never arrived.
        _messenger.Register<RestartInProgressMessage>(this, (_, _) =>
            Dispatcher.Invoke(() => _viewModel.ShowModal(
                "Restarting EyeCtrl…\n"
                + "Waiting for the new EyeCtrl to start. If it cannot, this one keeps running.\n"
                + "Hiding this notice does not stop the restart.",
                [new ModalChoice("restart_progress_hide", "✕ Hide", ModalChoiceKind.Neutral, null)])));

        // A refused restart must be visible: the user just completed a dwell, and
        // silence reads as the dwell not registering.
        //
        // ONE button, not the Yes/No pair. This is an acknowledgement, not a question —
        // "✕ No" to "the new EyeCtrl did not start in time" means nothing, and for a gaze
        // user every extra target is another chance to mis-dwell on a message that only
        // needs dismissing. The multi-choice modal makes a single button expressible;
        // before it existed this had to borrow the Yes/No form.
        _messenger.Register<RestartUnavailableMessage>(this, (_, m) =>
            Dispatcher.Invoke(() => _viewModel.ShowModal(
                m.Reason,
                [new ModalChoice("restart_failed_ok", "✓ OK", ModalChoiceKind.Affirmative, null)])));

        // Initialize tab definitions with page builders
        _tabDefs =
        [
            ("Cursor",   "◎", "Cursor",      BuildCursorPages),
            ("Dwell",    "⏱", "Dwell Times", BuildDwellPages),
            ("Selection", "⊕", "Selection", BuildSelectionPages),
            ("Actions",  "⊙", "Action Menu", BuildActionMenuPages),
            ("Layout",   "⊞", "Layout",      BuildLayoutPages),
            ("Keyboard", "⌨", "Keyboard",    BuildKeyboardPages),
            ("Models",    "⬡", "Models",      BuildModelsPages),
            ("Toolbar",  "▣", "Toolbar",     BuildToolbarPages),
            ("Calibration", "⌖", "Calibration", BuildCalibrationPages),
            ("General",  "⚙", "General",     BuildGeneralPages),
            ("QuickActions", "⚡", "Quick Actions", BuildQuickActionsPages)
        ];

        // Build dictionary for O(1) page builder lookup
        foreach (var (id, _, _, pageBuilder) in _tabDefs)
        {
            _pageBuilders[id] = pageBuilder;
        }

        // Set DataContext for data binding
        DataContext = _viewModel;

        // Window sizing
        Width = SystemParameters.PrimaryScreenWidth * WindowSizeRatio;
        Height = SystemParameters.PrimaryScreenHeight * WindowSizeRatio;

        // Wire up property change handlers
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Settings.PropertyChanged += OnSettingsViewModelPropertyChanged;

        // Mouse fallback for close button
        CloseButton.MouseLeftButtonDown += (_, _) => Close();

        // Dispose ViewModel on window close
        Closed += (_, _) =>
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Settings.PropertyChanged -= OnSettingsViewModelPropertyChanged;
            _viewModel.Dispose();
        };

        // Spec pipeline: page sources build declarative specs; RenderSpec renders them. The context
        // bag carries the view-delegate seam (ShowModal etc.) so sources stay WPF-free.
        _ctx = new SettingsPageContext(
            _settings, _viewModel, _registryService, _modelManagementService,
            _calibrationService, _messenger, _restartCoordinator,
            ShowModal, ShowError, ApplyAndSave, RebuildContent, a => Dispatcher.Invoke(a), Close,
            () => System.Windows.Clipboard.GetText() ?? "");
        _cursorSource = new CursorPageSource(_ctx);
        _dwellSource = new DwellPageSource(_ctx);
        _selectionSource = new SelectionPageSource(_ctx);
        _keyboardSource = new KeyboardPageSource(_ctx);
        _toolbarSource = new ToolbarPageSource(_ctx);
        _actionMenuSource = new ActionMenuPageSource(_ctx);
        _layoutSource = new LayoutPageSource(_ctx);
        _generalSource = new GeneralPageSource(_ctx);
        _calibrationSource = new CalibrationPageSource(_ctx);
        _modelsSource = new ModelsPageSource(_ctx);
        _qaSource = new QuickActionsPageSource(_ctx);

        // Build UI
        BuildTabButtons();
        ShowTab("Cursor");
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(SettingsWindowViewModel.TabProgress):
                    UpdateTabVisuals();
                    break;
                case nameof(SettingsWindowViewModel.PagProgress):
                    UpdatePaginationVisuals();
                    break;
                case nameof(SettingsWindowViewModel.ModalProgress):
                    UpdateModalVisuals();
                    break;
                case nameof(SettingsWindowViewModel.ActiveTabId):
                    UpdateActiveTabHighlight();
                    break;
                case nameof(SettingsWindowViewModel.IsModalVisible):
                    UpdateModalVisibility();
                    break;
            }
        });
    }

    private void OnSettingsViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(SettingsViewModel.CurrentTabName):
                    _qaSource.ResetView();
                    RebuildContent();
                    break;
                case nameof(SettingsViewModel.CurrentPage):
                    RenderCurrentPage();
                    RenderPagination();
                    break;
                case nameof(SettingsViewModel.TotalPages):
                    RenderPagination();
                    break;
            }
        });
    }

    // ────── Sidebar tabs ──────

    private void BuildTabButtons()
    {
        foreach (var (id, icon, label, _) in _tabDefs)
        {
            // Set AutomationId on the label TextBlock — WPF's UIA control view only exposes
            // TextBlock nodes from transparent overlay windows; Border containers are collapsed.
            var iconText = new TextBlock
            {
                Text = icon,
                Foreground = Tokens.BrushTextPrimary,
                FontSize = TabContainerHeight * ContainerRatio.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(iconText, $"Settings_Tab_{id}");

            var tabBrush = new SolidColorBrush(TabInactive);
            var border = new Border
            {
                Height = TabContainerHeight,
                Margin = new Thickness(4),
                CornerRadius = Tokens.StandardCornerRadius,
                Background = tabBrush,
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(16, 0, 16, 0),
                    Children =
                    {
                        iconText,
                        new TextBlock
                        {
                            Text = label,
                            Foreground = Tokens.BrushTextPrimary,
                            FontSize = Tokens.FontSizeTitle,
                            FontWeight = Tokens.FontWeightMedium,
                            VerticalAlignment = VerticalAlignment.Center,
                            // FIXED by widening the sidebar column to 240 DIP
                            // (SettingsWindow.xaml). **This branch caused the spill; it did not
                            // inherit it.** Measured headlessly with the real glyphs, at the
                            // sidebar's 220 DIP, for "Quick Actions":
                            //
                            //   pre-branch  (icon 30, label 22, no FontFamily override)  218.61
                            //   post-branch (icon 32, label Tokens.FontSizeTitle = 24)   233.86
                            //
                            // So it sat 1.39px INSIDE the column before and 13.86px outside
                            // after. The dominant term is this label going 22 -> 24, worth
                            // +12.50px; the icon's 30 -> TabContainerHeight * Medium (32) adds
                            // only +2.75px, and retargeting the family at "Segoe UI Variable
                            // Text, Segoe UI" moves it by under 0.6px either way. An earlier
                            // version of this comment claimed a pre-branch 231.11 and blamed the
                            // icon alone — no configuration measures 231.11, and the label was
                            // the larger half.
                            //
                            // At 240 every label clears the column, the tightest being
                            // "Quick Actions" with 6.14px spare. It still overhangs its own tab
                            // Border (which now ends at x=228) by 5.86px, and that is ACCEPTED:
                            // the text stays fully legible against the sidebar's own fill, and
                            // 240 strictly improves the highlight too — at 220 "Dwell Times" and
                            // "Quick Actions" both overhung a Border ending at x=208.
                            //
                            // A TextTrimming.CharacterEllipsis was tried here and REMOVED because
                            // it is provably inert, measured twice: this label's parent is a
                            // horizontal StackPanel, which measures children with infinite width
                            // in the stacking direction, so the label is never width-constrained
                            // and has nothing to trim against — ActualWidth is 149.92 and the
                            // right edge 233.86 with and without it. Left in place it would read,
                            // at the call site, as if the label truncated. It does not. That is
                            // still true and is why the attribute is still absent.
                            //
                            // The alternative was to make it live: a two-column Grid("Auto,*")
                            // here plus the trimming back, which brings every right edge to
                            // exactly 192.0 — inside the 220 column. It was REJECTED on the
                            // measurement, because 192.0 is the slot edge, not the text's: the
                            // constrained slots are 108.06 / 125.03 / 108.06 DIP against text
                            // needing 131.12 / 140.22 / 149.92, so "Dwell Times", "Action Menu"
                            // and "Quick Actions" would all render ellipsized. Trading three
                            // unreadable tab names for 20px of a paginated, auto-sized content
                            // column is the wrong trade for a user who navigates this window by
                            // gaze. The robustness that fix bought is supplied instead by
                            // SettingsTabLabelFitTests, which re-measures every label against the
                            // column width parsed from the XAML and fails on any future resize.
                        }
                    }
                }
            };

            // Mouse fallback
            border.MouseLeftButtonDown += (_, _) => ShowTab(id);

            // Store border + brush for visual updates
            _tabButtons[id] = border;
            _tabBrushes[id] = tabBrush;
            TabButtonPanel.Children.Add(border);

            // Register with ViewModel for dwell detection
            _viewModel.Register(id, DwellCategory.Tab, DpiHelper.LogicalToPhysicalX(TabBtnRadius), _dwellMs, () => { });
        }

        // Register close button with ViewModel
        _viewModel.Register("close", DwellCategory.Tab, DpiHelper.LogicalToPhysicalX(TabBtnRadius), _dwellMs, () => { });
    }

    // ────── Tab switching ──────

    private void ShowTab(string tabId)
    {
        // Update ViewModel's tab state - property changed handler will rebuild content
        _viewModel.Settings.ShowTab(tabId);
    }

    // ────── Live apply + debounced save ──────

    private void ApplyAndSave()
    {
        SettingsChanged?.Invoke(_settings);
        _settingsService.SaveDebounced(_settings);
    }

    // ────── Pagination ──────

    private void RebuildContent()
    {
        _viewModel.ClearCategory(DwellCategory.Control);
        ContentPanel.Children.Clear();

        _pages = BuildPages();
        _viewModel.Settings.TotalPages = _pages.Count;
        _viewModel.Settings.CurrentPage = Math.Clamp(_viewModel.Settings.CurrentPage, 0, Math.Max(0, _viewModel.Settings.TotalPages - 1));

        RenderCurrentPage();
        RenderPagination();
    }

    private void RenderCurrentPage()
    {
        _viewModel.ClearCategory(DwellCategory.Control);
        ContentPanel.Children.Clear();
        _activeItems.Clear();

        var currentPage = _viewModel.Settings.CurrentPage;
        if (currentPage < _pages.Count)
        {
            foreach (var row in _pages[currentPage].Rows)
                RenderSpec(row);
        }
    }

    private void RenderPagination()
    {
        PaginationPanel.Children.Clear();
        _pagButtons.Clear();
        _pagBrushes.Clear();

        // Use ViewModel's pagination state
        if (_viewModel.Settings.TotalPages <= 1) return;

        var prev = MakePagButton("pag_prev", "◀");
        var indicator = new TextBlock
        {
            Text = $"{_viewModel.Settings.CurrentPage + 1} / {_viewModel.Settings.TotalPages}",
            Foreground = Tokens.BrushTextPrimary,
            FontSize = Tokens.FontSizeTitle,
            FontWeight = Tokens.FontWeightMedium,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20, 0, 20, 0),
            MinWidth = 80,
            TextAlignment = TextAlignment.Center
        };
        var next = MakePagButton("pag_next", "▶");

        // Disable buttons at boundaries
        if (_viewModel.Settings.CurrentPage <= 0) prev.Opacity = 0.3;
        if (_viewModel.Settings.CurrentPage >= _viewModel.Settings.TotalPages - 1) next.Opacity = 0.3;

        PaginationPanel.Children.Add(prev);
        PaginationPanel.Children.Add(indicator);
        PaginationPanel.Children.Add(next);
    }

    private Border MakePagButton(string id, string text)
    {
        var pagBrush = new SolidColorBrush(BtnBase);
        var btn = new Border
        {
            Width = PaginationButtonWidth,
            Height = PaginationButtonHeight,
            Margin = new Thickness(4),
            CornerRadius = Tokens.StandardCornerRadius,
            Background = pagBrush,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                Foreground = Tokens.BrushTextPrimary,
                FontSize = PaginationButtonHeight * ContainerRatio.Large,
                FontWeight = Tokens.FontWeightStrong,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        // Mouse fallback - ViewModel handles pagination
        btn.MouseLeftButtonDown += (_, _) =>
        {
            if (id == "pag_prev")
                _viewModel.Settings.PreviousPage();
            else
                _viewModel.Settings.NextPage();
        };

        _pagButtons[id] = btn;
        _pagBrushes[id] = pagBrush;

        // Register with ViewModel for dwell detection
        _viewModel.Register(id, DwellCategory.Pagination, DpiHelper.LogicalToPhysicalX(PagBtnRadius), _dwellMs, () => { });

        return btn;
    }

    // ────── Page builders per tab ──────

    private IReadOnlyList<PageSpec> BuildPages()
    {
        return _pageBuilders.TryGetValue(_viewModel.Settings.CurrentTabName, out var pageBuilder)
            ? pageBuilder.Invoke()
            : Array.Empty<PageSpec>();
    }

    private IReadOnlyList<PageSpec> BuildCursorPages() => _cursorSource.Build();


    private IReadOnlyList<PageSpec> BuildDwellPages() => _dwellSource.Build();

    private IReadOnlyList<PageSpec> BuildSelectionPages() => _selectionSource.Build();

    private IReadOnlyList<PageSpec> BuildActionMenuPages() => _actionMenuSource.Build();

    private IReadOnlyList<PageSpec> BuildLayoutPages() => _layoutSource.Build();

    private IReadOnlyList<PageSpec> BuildKeyboardPages() => _keyboardSource.Build();

    private IReadOnlyList<PageSpec> BuildToolbarPages() => _toolbarSource.Build();

    private IReadOnlyList<PageSpec> BuildModelsPages() => _modelsSource.Build();

    private IReadOnlyList<PageSpec> BuildGeneralPages() => _generalSource.Build();

    private IReadOnlyList<PageSpec> BuildQuickActionsPages() => _qaSource.Build();

    private IReadOnlyList<PageSpec> BuildCalibrationPages() => _calibrationSource.Build();

    // ────── Shared helpers ──────

    /// <summary>
    /// How much horizontal room a control dropped into <c>ContentPanel</c> actually gets.
    ///
    /// Read off the live elements — the sidebar column declared by <c>RootGrid</c>, and the two
    /// nested <c>Margin="16"</c> insets of <c>ContentArea</c> and <c>ContentPanel</c> — so the
    /// arithmetic cannot fall out of step with the XAML that decides it. Restating any of the
    /// three here is how the sidebar's 220 -> 240 widening silently ate 20 DIP of content column
    /// with nothing noticing.
    ///
    /// <c>Width</c>, not <c>ActualWidth</c>: content is built during construction, before the
    /// window has ever been laid out, so <c>ActualWidth</c> is still 0 at that point.
    /// </summary>
    private double ContentAvailableWidth
    {
        get
        {
            // The window is sized from the primary screen in the constructor and never resized;
            // if that ever stops being true, decline to constrain rather than compute nonsense.
            if (double.IsNaN(Width) || double.IsInfinity(Width) || Width <= 0)
                return double.PositiveInfinity;

            return Width
                - RootGrid.ColumnDefinitions[0].Width.Value
                - ContentArea.Margin.Left - ContentArea.Margin.Right
                - ContentPanel.Margin.Left - ContentPanel.Margin.Right;
        }
    }

    /// <summary>
    /// Caps <paramref name="root"/> so it cannot be drawn wider than the content column can show.
    ///
    /// <c>MaxWidth</c> rather than a rewritten <c>Width</c>: WPF's min/max clamp wins over an
    /// explicit <c>Width</c>, so a control keeps its designed width on any display roomy enough
    /// for it and only gives ground on one that is not. Every fixed width in this file
    /// (<see cref="PickerButtonWidth"/> down to the 300 DIP reset buttons) was chosen against a
    /// wide screen, and at 0.9 x an 800 DIP display the content column is 416 DIP — measured, the
    /// 460 picker button then hangs 52 DIP past the panel and 16 DIP off the window edge.
    ///
    /// **This does not move any hit geometry out from under its visual.** The dwell target is the
    /// Root's centre plus a fixed physical radius, and a symmetric narrowing leaves the centre
    /// where it is; the clamp only ever removes visual that had nothing under it. The
    /// <see cref="MinimumDwellableWidth"/> floor keeps the drawn button at least as wide as the
    /// circle it registers.
    /// </summary>
    /// <param name="root">The control's root Border.</param>
    /// <param name="availableWidth">Room in the content column, from <see cref="ContentAvailableWidth"/>.</param>
    internal static void ConstrainToContentWidth(Border root, double availableWidth)
    {
        if (double.IsNaN(availableWidth) || double.IsInfinity(availableWidth)) return;

        // The Border's own margin sits outside its width, so it has to come off the budget or the
        // control clears the panel by exactly the margin it declared.
        root.MaxWidth = Math.Max(
            MinimumDwellableWidth,
            availableWidth - root.Margin.Left - root.Margin.Right);
    }

    // ────── Spec rendering (the imperative bridge that a future MVVM step deletes) ──────

    /// <summary>
    /// Maps a <see cref="ButtonKind"/> to its (fill, label-brush) pair. Extracted and
    /// <c>internal static</c> so a headless test can pin it: <c>ResetBase</c> and <c>DeleteBase</c>
    /// are BOTH <c>Tokens.AccentWarning</c>, so swapping those two arms is invisible on screen —
    /// only the label-brush differs: Reset/Delete return the surface-dark brush, Default returns
    /// null (which <c>DwellButton</c> then renders as the primary text brush). The test asserts this.
    /// </summary>
    internal static (Color Fill, System.Windows.Media.Brush? Text) FillFor(ButtonKind kind) => kind switch
    {
        ButtonKind.Reset  => (ResetBase, Tokens.BrushSurfaceDark),
        ButtonKind.Delete => (DeleteBase, Tokens.BrushSurfaceDark),
        _                 => (BtnBase,   null),
    };

    /// <summary>
    /// Maps one declarative <paramref name="row"/> onto its <see cref="DwellControl"/> and places it
    /// via the <see cref="Add(DwellControl)"/>/<see cref="Add(IDwellComposite)"/>/<see cref="Add(DwellLabel)"/>
    /// overloads. The single WPF-coupled point of the spec pipeline; the per-tab sources are pure.
    /// </summary>
    private void RenderSpec(SettingsRowSpec row)
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;
        switch (row)
        {
            case SliderSpec s:
                Add(new DwellSlider(_viewModel, s.Id, s.Label, s.Min, s.Max, s.Step, s.Decimals,
                    s.Get, s.Set, cat, r, _dwellMs));
                break;
            case ToggleSpec t:
                Add(new DwellToggle(_viewModel, t.Id, t.Label, t.Get, t.Set, cat, r, _dwellMs));
                break;
            case ComboBoxSpec c:
                Add(new DwellComboBox(_viewModel, c.Id, c.Label, c.Display,
                    c.Code ?? c.Display, c.Get, c.Set, cat, r, _dwellMs));
                break;
            case ColorPickerSpec cp:
                Add(new DwellColorPicker(_viewModel, cp.Id, cp.Label, cp.Get, cp.Set, cat,
                    DpiHelper.LogicalToPhysicalX(cp.RadiusLogical), _dwellMs));
                break;
            case ButtonSpec b:
                var (fill, textBrush) = FillFor(b.Kind);
                Add(new DwellButton(_viewModel, b.Id, b.Label, b.OnActivate, fill, cat, r, _dwellMs,
                    width: b.Width, height: b.Height, textBrush: textBrush));
                break;
            case LabelSpec l:
                Add(new DwellLabel(l.Text()));
                break;
            default:
                throw new InvalidOperationException($"Unknown settings row spec: {row?.GetType().Name}");
        }
    }

    private void Add(DwellControl control)
    {
        ConstrainToContentWidth(control.Root, ContentAvailableWidth);
        control.Attach(ContentPanel);
        _activeItems.Add(control);
    }

    // Composites are deliberately NOT capped, for two reasons. IDwellComposite owns its own panel
    // and exposes no single root to cap — only its Children, whose Roots are laid out inside a row
    // it controls. And they do not need it: the widest is DwellSlider's row, two default-width
    // (100 DIP) buttons around a 140 DIP value block, about 316 DIP all in, which clears the
    // 416 DIP content column of the narrowest display measured in SettingsContentFitTests. If a
    // composite ever grows past that, cap it inside the composite where its layout lives.
    private void Add(IDwellComposite composite)
    {
        composite.Attach(ContentPanel);
        _activeItems.Add(composite);
    }

    private void Add(DwellLabel label)
    {
        label.Attach(ContentPanel);
    }

    // ────── Gaze update ──────

    public void UpdateGaze(PhysicalPoint screenPoint, long timestampMicroseconds)
    {
        // Update button positions for ViewModel's dwell detection
        // ViewModel handles actual dwell updates via GazeCursorMovedMessage
        UpdateAllTargetPositions();
    }

    private void UpdateAllTargetPositions()
    {
        // Update close button position
        try
        {
            var cPos = CloseButton.PointToScreen(
                new Point(CloseButton.ActualWidth / 2, CloseButton.ActualHeight / 2));
            _viewModel.UpdatePosition("close", DwellCategory.Tab, new PhysicalPoint(cPos.X, cPos.Y));
        }
        catch (InvalidOperationException)
        {
            // Window or button not yet rendered or not in visual tree
        }

        // Update tab button positions
        UpdateButtonPositions(_tabButtons, (id, pos) => _viewModel.UpdatePosition(id, DwellCategory.Tab, pos));

        // Update pagination button positions
        UpdateButtonPositions(_pagButtons, (id, pos) => _viewModel.UpdatePosition(id, DwellCategory.Pagination, pos));

        // Update modal button positions if modal is visible
        if (_viewModel.IsModalVisible)
        {
            UpdateButtonPositions(_modalButtons, (id, pos) => _viewModel.UpdatePosition(id, DwellCategory.Modal, pos));
        }

        // Update new-style dwell controls
        foreach (var item in _activeItems)
        {
            if (item is DwellControl single)
            {
                single.UpdatePosition();
                single.UpdateVisual();
            }
            if (item is IDwellComposite comp)
            {
                foreach (var child in comp.Children)
                {
                    child.UpdatePosition();
                    child.UpdateVisual();
                }
            }
        }
    }

    private static void UpdateButtonPositions(
        Dictionary<string, Border> borders,
        Action<string, PhysicalPoint> updateAction)
    {
        foreach (var (id, border) in borders)
        {
            try
            {
                var pos = border.PointToScreen(
                    new Point(border.ActualWidth / 2, border.ActualHeight / 2));
                updateAction(id, new PhysicalPoint(pos.X, pos.Y));
            }
            catch (InvalidOperationException)
            {
                // Border not yet rendered or not in visual tree
            }
        }
    }

    // ────── Dwell visual feedback ──────

    private void UpdateTabVisuals()
    {
        foreach (var (id, progress) in _viewModel.TabProgress)
        {
            if (id == "close")
            {
                // HighlightColor(CloseBase, progress, 0.5) == Blend(CloseBase, Accent, progress * 0.5)
                if (_closeButtonBrush != null)
                    _closeButtonBrush.Color = ColorBlend.Blend(CloseBase, Tokens.Accent, progress * 0.5);
                continue;
            }

            if (_tabBrushes.TryGetValue(id, out var brush) && id != _viewModel.ActiveTabId)
            {
                brush.Color = ColorBlend.Blend(TabInactive, TabActive, progress);
            }
        }
    }

    private void UpdatePaginationVisuals()
    {
        foreach (var (id, progress) in _viewModel.PagProgress)
        {
            if (_pagBrushes.TryGetValue(id, out var brush))
            {
                // HighlightColor(BtnBase, progress, 0.4) == Blend(BtnBase, Accent, progress * 0.4)
                brush.Color = ColorBlend.Blend(BtnBase, Tokens.Accent, progress * 0.4);
            }
        }
    }

    private void UpdateModalVisuals()
    {
        foreach (var (id, progress) in _viewModel.ModalProgress)
        {
            if (_modalBrushes.TryGetValue(id, out var brush)
                && _modalBaseColors.TryGetValue(id, out var baseC)
                && _modalBlendFactors.TryGetValue(id, out var factor))
            {
                // Blend toward Accent by progress * factor. The factor is per-kind from
                // ModalButtonPalette: 0.5 for the bright fills (their dark labels only gain
                // contrast), 0.25 for the dark Neutral fill (whose light label would collapse
                // to ~2.6:1 at full dwell under 0.5). Same Accent target preserves the app-wide
                // "accent means dwell in progress" cue; only the intensity adapts to the fill.
                brush.Color = ColorBlend.Blend(baseC, Tokens.Accent, progress * factor);
            }
        }
    }

    private void UpdateActiveTabHighlight()
    {
        foreach (var (id, brush) in _tabBrushes)
        {
            brush.Color = id == _viewModel.ActiveTabId ? TabActive : TabInactive;
        }
    }

    private void UpdateModalVisibility()
    {
        if (_viewModel.IsModalVisible)
        {
            ModalMessage.Text = _viewModel.ModalMessage;
            ModalButtonPanel.Children.Clear();
            _modalButtons.Clear();
            _modalBrushes.Clear();
            _modalBaseColors.Clear();
            _modalBlendFactors.Clear();

            // Two choices keep the horizontal row they have always had. Three or more
            // stack vertically: side by side they would not fit descriptive labels, and a
            // vertical stack gives a dwell user more separation between targets.
            var choices = _viewModel.ModalChoices;
            var vertical = choices.Count > 2;
            ModalButtonPanel.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;

            foreach (var choice in choices)
            {
                var (background, foreground) = ModalButtonPalette.For(choice.Kind);
                var button = MakeModalButton(choice.Id, choice.Label, background, foreground, vertical);
                // Capture this kind's dwell blend factor alongside the base color MakeModalButton
                // stored, so UpdateModalVisuals blends at the per-kind intensity.
                _modalBlendFactors[choice.Id] = ModalButtonPalette.DwellBlendFactor(choice.Kind);
                ModalButtonPanel.Children.Add(button);

                // Register as a dwell target with DPI-scaled radius. The empty callback is
                // the extension point; the ViewModel runs the choice's own action.
                _viewModel.Register(
                    choice.Id, DwellCategory.Modal,
                    DpiHelper.LogicalToPhysicalX(ModalBtnRadius), _dwellMs, () => { });
            }

            ModalOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            ModalOverlay.Visibility = Visibility.Collapsed;
            _modalButtons.Clear();
            _modalBrushes.Clear();
            _modalBaseColors.Clear();
            _modalBlendFactors.Clear();
        }
    }

    // ────── Modal confirmation ──────

    private void ShowModal(string message, Action onYes)
    {
        // Delegate to ViewModel
        _viewModel.ShowModal(message, onYes);
    }

    private void ShowError(string message)
    {
        ShowModal(message, () => { });
    }

    /// <summary>Builds one modal button.</summary>
    /// <param name="id">Dwell-target id.</param>
    /// <param name="text">Label.</param>
    /// <param name="bg">Fill, from <see cref="ModalButtonPalette"/>.</param>
    /// <param name="foreground">Label brush, paired with the fill for contrast.</param>
    /// <param name="wide">
    /// True for a vertically stacked layout, where buttons are wider (descriptive labels)
    /// and separated by vertical margins instead of horizontal ones.
    /// </param>
    private Border MakeModalButton(string id, string text, Color bg, SolidColorBrush foreground, bool wide)
    {
        var modalBrush = new SolidColorBrush(bg);
        var border = new Border
        {
            Width = wide ? 460 : 170,
            Height = ModalButtonHeight,
            Margin = wide ? new Thickness(0, 8, 0, 8) : new Thickness(12, 0, 12, 0),
            CornerRadius = Tokens.StandardCornerRadius,
            Background = modalBrush,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                Foreground = foreground,
                FontSize = Tokens.FontSizeTitle,
                FontWeight = Tokens.FontWeightStrong,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            }
        };

        // Mouse fallback. Everything routes through the ViewModel now — it owns which
        // choice does what, so the view no longer branches on the id.
        border.MouseLeftButtonDown += (_, _) => _viewModel.SimulateActivated(id, DwellCategory.Modal);

        _modalButtons[id] = border;
        _modalBrushes[id] = modalBrush;
        _modalBaseColors[id] = bg;
        return border;
    }
}
