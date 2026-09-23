namespace EyeCtrl.App.Views;

using System.IO;
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
    // Swatches render at DwellColorPicker.SwatchWidth x SwatchHeight with WrapPanel gutters sized so the
    // standard control radius (100 px hit circle) fits without overlapping neighbours — see SwatchTargetGeometryTests.
    internal const double ColorSwatchRadius = ControlBtnRadius;

    // ── The labelled-row geometry (name on the left, control on the right) ──

    /// <summary>
    /// The MINIMUM height of the slot a labelled row gives its CONTROL, in DIP — the row pitch.
    ///
    /// A slot, not the row: only the two-column form is 150 tall overall, because there the name
    /// sits BESIDE the control and shares its box. The stacked form (narrow columns) puts the
    /// name above and gives the control its own 150 DIP slot underneath, so that row measures the
    /// name's height PLUS this — 193.92 for a one-line name at the 416 DIP narrowest column. It
    /// has to: a 100 DIP control under a 43.92 DIP name cannot have its centre at 75 inside a 150
    /// DIP box, and the centre is the whole point (see below). What both forms therefore share is
    /// the thing the pitch is actually about — 75 DIP from the control's circle to each edge of
    /// its own slot, hence 150 between any two adjacent controls' circles.
    ///
    /// This is hit geometry, not styling. <c>DwellControl.UpdatePosition</c> registers a target
    /// at its control's CENTRE with radius <see cref="ControlBtnRadius"/>, so the circles of two
    /// vertically adjacent controls are separated by the row pitch minus one diameter — nothing
    /// else. At a 100 DIP pitch two 100 DIP circles are exactly tangent: no gap, no tolerance,
    /// and a one-pixel gaze drift lands on the neighbour. At 150 they are 50 DIP apart, at or
    /// above the 40 DIP the colour-swatch grid leaves
    /// (<c>DwellColorPicker.SwatchHeight</c> + 2 x <c>SwatchVerticalMargin</c> - 2 x
    /// <see cref="ColorSwatchRadius"/>), which is the only adjacent-target pitch in the
    /// application already pinned by a geometry test (<c>SwatchTargetGeometryTests</c>).
    ///
    /// A MINIMUM rather than a fixed height on purpose: a description that wraps to further
    /// lines grows the row instead of being clipped, and <c>SettingsAutoPagination</c> measures
    /// the grown row so pagination follows it.
    /// </summary>
    internal const double LabelledRowMinHeight = 150;

    /// <summary>
    /// Width of a labelled row's NAME column, in DIP.
    ///
    /// Sized against the widest control that CANNOT give ground: the slider's stepper row, 404
    /// DIP measured (<c>SettingsContentFitTests.TheSliderRow_FitsTheContentColumn_OnANarrowDisplay</c>
    /// reports that number), a horizontal <c>StackPanel</c> that clips rather than wraps. The
    /// picker's and combo box's <c>WrapPanel</c>s can want more than 404 on one line — four
    /// swatches are 544 — but they wrap into whatever column they are given, so they do not set
    /// this width. 380 leaves the name a column of comparable width beside the slider.
    /// </summary>
    internal const double RowNameColumnWidth = 380;

    /// <summary>
    /// The narrowest content column, in DIP, at which a labelled row may put its name and its
    /// control side by side. Below it the row STACKS the two.
    ///
    /// 380 (<see cref="RowNameColumnWidth"/>) + 404 (the slider's stepper row, the widest control
    /// that clips rather than wraps) + a 24 DIP gutter. There is no narrower honest threshold: at
    /// the 416 DIP narrowest supported content column the slider alone measures 404, so no name
    /// column of any width fits beside it.
    ///
    /// Stacking is not a cosmetic fallback. <c>DwellSlider</c>'s stepper row is a horizontal
    /// <c>StackPanel</c>, which CLIPS rather than wraps, and composites are deliberately left
    /// uncapped by <see cref="ConstrainToContentWidth"/> — so a two-column row forced into a
    /// column too narrow for it would draw its stepper buttons past the card's edge with their
    /// hit circles still registered at the same centres: a live dwell target over blank space.
    /// </summary>
    internal const double TwoColumnMinimumWidth = RowNameColumnWidth + 404 + 24;

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

    /// <summary>
    /// The pagination button's height, and one of the two terms of
    /// <see cref="PaginationReserveHeight"/> that the content height budget subtracts.
    ///
    /// <c>internal</c> for the same reason as <see cref="TabContainerHeight"/> and
    /// <see cref="WindowSizeRatio"/>: <c>HandChunkedPageFitTests</c> re-derives the content
    /// height budget from outside the window (which cannot be constructed — eight non-optional
    /// dependencies), and every term of that budget has to come from production rather than a
    /// copy. Restating 112 there would let the guard keep passing against a budget the
    /// application does not have, which is the whole failure mode it exists to catch.
    /// </summary>
    internal const double PaginationButtonHeight = 80;

    /// <summary>
    /// Uniform (horizontal and vertical) margin <c>MakePagButton</c> applies to each button;
    /// <see cref="PaginationReserveHeight"/> counts 2 * this. <c>internal</c> for the same
    /// reason as <see cref="PaginationButtonHeight"/>.
    /// </summary>
    internal const double PaginationButtonMargin = 4;
    private const double ModalButtonHeight = 100;

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
    private readonly UserDictionaryService? _dictionaryService;
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
    private readonly EyeCarePageSource _eyeCareSource;
    private readonly AdaptationPageSource _adaptationSource;
    private readonly DiagnosticsPageSource _diagnosticsSource;
    private readonly AdvancedPageSource _advancedSource;

    private readonly Dictionary<string, Border> _tabButtons = new();
    private readonly Dictionary<string, Border> _pagButtons = new();
    private readonly Dictionary<string, Border> _modalButtons = new();

    // Sidebar tab-list pagination (the sidebar cannot scroll): the current page's tab ids
    // (registered while shown), the pager buttons, and the page arithmetic state.
    private readonly List<string> _renderedTabIds = new();
    private readonly Dictionary<string, Border> _tabPagerButtons = new();
    private readonly Dictionary<string, SolidColorBrush> _tabPagerBrushes = new();
    private int _tabsPerPage = 1;
    private int _tabPageCount = 1;
    private int _tabPageIndex;

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

    /// <summary>
    /// The sidebar's tabs: id, sidebar glyph, human label, and the one-line description the page
    /// header shows under that label. <c>Description</c> is not decoration — the header is the
    /// only place a tab says what it is for, and the tab glyphs alone are ambiguous.
    /// </summary>
    // INTENTIONALLY NOT EXPOSED in the settings UI (audit this list when adding settings):
    //  - ManualSelection.Enabled (driven by SelectionMode; the Selection tab owns the mode)
    //  - Keyboard ML/model settings (CustomModels etc. — managed by the model registry; Models tab)
    //  - Supervision — its UI is SupervisorSetupWindow in the supervisor process; the App
    //    cannot persist that section (SettingsWriter.Supervisor owns it)
    // Everything else in EyeCtrlSettings.EnumerateSubSettings() should have a tab/control here.
    private readonly (string Id, string Icon, string Label, string Description)[] _tabDefs;
    private readonly Dictionary<string, ISettingsPageSource> _sources = new();

    public SettingsWindow(
        SettingsWindowViewModel viewModel,
        EyeCtrlSettings settings,
        SettingsService settingsService,
        ModelRegistryService registryService,
        ModelManagementService modelManagementService,
        IMessenger messenger,
        CalibrationService calibrationService,
        AppRestartCoordinator restartCoordinator,
        UserDictionaryService? dictionaryService = null)
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
        _dictionaryService = dictionaryService;
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

        // Initialize tab definitions
        _tabDefs =
        [
            // Descriptions describe what each tab ACTUALLY holds, read off the page sources in
            // src/EyeCtrl.App/Views/Settings/Sources — not what the tab's name suggests.
            ("Cursor",   "◎", "Cursor",
                "How raw gaze is filtered, and how the cursor ring is drawn."),
            ("Dwell",    "⏱", "Dwell Times",
                "How long gaze must rest on a target before it activates."),
            ("Selection", "⊕", "Selection",
                "Picking the exact target: mode, magnifier, highlight and snapping."),
            ("Actions",  "⊙", "Action Menu",
                "Which actions appear in the ring that opens after an activation."),
            ("Layout",   "⊞", "Layout",
                "Where the action menu and magnifier open: at your gaze, or at the screen centre."),
            ("Keyboard", "⌨", "Keyboard",
                "On-screen keyboard size, layout and word prediction."),
            ("Models",    "⬡", "Models",
                "Language models used for word prediction."),
            ("Toolbar",  "▣", "Toolbar",
                "Where the utility bar docks, and how big its buttons are."),
            ("Calibration", "⌖", "Calibration",
                "Correcting the offset between your gaze and the cursor."),
            ("EyeCare", "◑", "Eye Care",
                "Break reminders, gaze exercises and fatigue escalation."),
            ("Adaptation", "⇄", "Adaptation",
                "Auto-tuning of dwell and targeting when you struggle."),
            ("General",  "⚙", "General",
                "Startup, theme, restart, and resetting settings."),
            ("QuickActions", "⚡", "Quick Actions",
                "The pop-up grid of one-dwell shortcuts, and how it is summoned."),
            ("Advanced", "⚒", "Advanced",
                "Expert tuning: backdrop, drag, scroll and external dwell."),
            ("Diagnostics", "◷", "Diagnostics",
                "Measurement instruments: raw-gaze recording and UI timing. Both off by default.")
        ];

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
            // The models source subscribed to the singleton management service's progress
            // event; the window is recreated per open, so leaving the subscription live
            // would accumulate one handler per settings visit.
            _modelsSource.Detach();
            _viewModel.Dispose();
        };

        // Spec pipeline: page sources build declarative specs; RenderSpec renders them. The context
        // bag carries the view-delegate seam (ShowModal etc.) so sources stay WPF-free.
        _ctx = new SettingsPageContext(
            _settings, _viewModel, _registryService, _modelManagementService,
            _calibrationService, _messenger, _restartCoordinator, _dictionaryService,
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
        _eyeCareSource = new EyeCarePageSource(_ctx);
        _adaptationSource = new AdaptationPageSource(_ctx, onOpenAdaptationLog: OpenAdaptationLogFile);
        _diagnosticsSource = new DiagnosticsPageSource(_ctx, onOpenRecordingFolder: OpenGazeRecordingFolder);
        _advancedSource = new AdvancedPageSource(_ctx);

        // One entry per tab, same ids as `_tabDefs`: MaybeRepaginateByHeight looks the
        // current tab's source up here to ask whether it opts into height re-pagination.
        _sources["Cursor"] = _cursorSource;
        _sources["Dwell"] = _dwellSource;
        _sources["Selection"] = _selectionSource;
        _sources["Actions"] = _actionMenuSource;
        _sources["Layout"] = _layoutSource;
        _sources["Keyboard"] = _keyboardSource;
        _sources["Models"] = _modelsSource;
        _sources["Toolbar"] = _toolbarSource;
        _sources["Calibration"] = _calibrationSource;
        _sources["General"] = _generalSource;
        _sources["QuickActions"] = _qaSource;
        _sources["EyeCare"] = _eyeCareSource;
        _sources["Adaptation"] = _adaptationSource;
        _sources["Diagnostics"] = _diagnosticsSource;
        _sources["Advanced"] = _advancedSource;

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
                    UpdateTabPaginationVisuals();
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
                    // Every source resets its view state (QuickActions' picker, Models' manage
                    // view), so re-entering a tab lands on its list view. Interface DIM: sources
                    // without view state no-op.
                    foreach (var source in _sources.Values)
                        source.ResetView();
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
                            // version of this comment claimed a pre-branch 231.11 and blamed
                            // the icon alone — no configuration measures 231.11, and the label was
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

            // Store border + brush for every tab; only the current page's borders are attached
            // and dwell-registered (RenderTabPage). Detached borders are inert — their position
            // updates throw and are swallowed, and their targets are unregistered.
            _tabButtons[id] = border;
            _tabBrushes[id] = tabBrush;
        }

        // How many tabs one sidebar page holds at this window height, then land on the page
        // showing the active tab (page 0 when nothing is active yet — the ctor's ShowTab
        // follows and cannot change the page: "Cursor" IS page 0).
        _tabsPerPage = SettingsTabPagination.TabsPerPage(
            SidebarTabBudget, TabContainerHeight + 2 * 4);
        _tabPageCount = SettingsTabPagination.PageCount(_tabDefs.Length, _tabsPerPage);
        var activeIndex = Array.FindIndex(_tabDefs, t => t.Id == _viewModel.ActiveTabId);
        _tabPageIndex = activeIndex < 0 ? 0 : SettingsTabPagination.PageOf(activeIndex, _tabsPerPage);

        BuildTabPagePager();
        RenderTabPage();

        // Register close button with ViewModel
        _viewModel.Register("close", DwellCategory.Tab, DpiHelper.LogicalToPhysicalX(TabBtnRadius), _dwellMs, () => { });
    }

    /// <summary>
    /// Attaches — and dwell-registers — only the current tab page's buttons. Tabs left on
    /// other pages are detached AND unregistered: a hidden tab with a live hit circle is a
    /// gaze trap (a dwell "succeeds" on invisible chrome whose position update silently fails).
    /// </summary>
    private void RenderTabPage()
    {
        foreach (var id in _renderedTabIds)
            _viewModel.Unregister(id, DwellCategory.Tab);
        _renderedTabIds.Clear();
        TabButtonPanel.Children.Clear();

        var (start, count) = SettingsTabPagination.PageSlice(_tabDefs.Length, _tabsPerPage, _tabPageIndex);
        for (var i = start; i < start + count; i++)
        {
            var (id, _, _, _) = _tabDefs[i];
            TabButtonPanel.Children.Add(_tabButtons[id]);
            _viewModel.Register(id, DwellCategory.Tab, DpiHelper.LogicalToPhysicalX(TabBtnRadius), _dwellMs, () => { });
            _renderedTabIds.Add(id);
        }

        UpdateActiveTabHighlight();
        UpdateTabPagePagerState();
    }

    private void SwitchTabPage(int page)
    {
        var clamped = Math.Clamp(page, 0, _tabPageCount - 1);
        if (clamped == _tabPageIndex) return;
        _tabPageIndex = clamped;
        RenderTabPage();
    }

    private void BuildTabPagePager()
    {
        if (_tabPageCount <= 1) return;   // every tab fits one page: no pager at all

        TabPagerPanel.Children.Add(MakeTabPagerButton("tab_prev", "▲", -1));
        TabPagerPanel.Children.Add(MakeTabPagerButton("tab_next", "▼", +1));
    }

    /// <summary>
    /// The shared skeleton of the window's round dwell buttons (tab pager, content pager,
    /// modal choices): mutable base brush, rounded border, centered strong label, mouse
    /// fallback, OPTIONAL dwell registration (see <paramref name="onActivated"/>), and
    /// storage into the caller's button/brush maps. Geometry, palette, category, hit
    /// radius and both activation paths are the caller's.
    ///
    /// FontSize is deliberately NOT parameterized: RawGeometryLiteralTests requires every
    /// FontSize value to reference the token surface, and a parameter name references no
    /// token. Each adapter assigns it inline at its call site — the same precedent as
    /// DragTrailOverlay's IconSize, which repeats its expression rather than reading a local.
    /// </summary>
    private Border MakeChromeButton(
        string id, string text, double width, double height, Thickness margin,
        Color fill, Brush textBrush,
        DwellCategory category, double radiusLogical,
        Action onMouseDown, Action? onActivated,
        Dictionary<string, Border> buttons, Dictionary<string, SolidColorBrush> brushes)
    {
        var brush = new SolidColorBrush(fill);
        var btn = new Border
        {
            Width = width,
            Height = height,
            Margin = margin,
            CornerRadius = Tokens.StandardCornerRadius,
            Background = brush,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                Foreground = textBrush,
                FontWeight = Tokens.FontWeightStrong,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        };

        btn.MouseLeftButtonDown += (_, _) => onMouseDown();
        buttons[id] = btn;
        brushes[id] = brush;
        // Registration is opt-in: MakeModalButton's CALLER registers (UpdateModalVisibility
        // captures the palette's blend factor alongside), so the modal adapter passes null —
        // a double Register would duplicate dwell-detector state on a gaze-critical path.
        // The two pager factories register here, as they always did.
        if (onActivated is not null)
            _viewModel.Register(id, category, DpiHelper.LogicalToPhysicalX(radiusLogical), _dwellMs, onActivated);
        return btn;
    }

    // Dwell and mouse converge on the same switch; the dwell path routes through the
    // registered action (SettingsWindowViewModel special-cases these ids before ShowTab).
    private Border MakeTabPagerButton(string id, string glyph, int direction)
    {
        var btn = MakeChromeButton(
            id, glyph, PaginationButtonWidth, PaginationButtonHeight,
            // 14, not the content pager's 4: two 100-DIP buttons registering PagBtnRadius
            // (60-DIP radius, 120-DIP hit circles) need ≥ 120 DIP between centres to keep the
            // circles clear of each other; 14 + 100 + 14 = 128.
            new Thickness(14, 4, 14, 4),
            BtnBase, Tokens.BrushTextPrimary,
            DwellCategory.Tab, PagBtnRadius,
            () => SwitchTabPage(_tabPageIndex + direction),
            () => SwitchTabPage(_tabPageIndex + direction),
            _tabPagerButtons, _tabPagerBrushes);
        ((TextBlock)btn.Child).FontSize = PaginationButtonHeight * ContainerRatio.Large;
        return btn;
    }

    /// <summary>
    /// Boundary state for the tab pager: dimmed arrow at either end (matching the content
    /// pager's opacity idiom — the target stays registered, a dwell there is just a no-op).
    /// </summary>
    private void UpdateTabPagePagerState()
    {
        if (_tabPageCount <= 1) return;
        _tabPagerButtons["tab_prev"].Opacity = _tabPageIndex <= 0 ? 0.3 : 1.0;
        _tabPagerButtons["tab_next"].Opacity = _tabPageIndex >= _tabPageCount - 1 ? 0.3 : 1.0;
    }

    // Same blend as the content pagination buttons (UpdatePaginationVisuals); kept in its own
    // dict because tab-pager targets live in the Tab category, not Pagination.
    private void UpdateTabPaginationVisuals()
    {
        BlendProgress(_viewModel.TabProgress, _tabPagerBrushes, BtnBase, 0.4);
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

    // §10 "log review": gaze-reachable access to the adaptation log. Selects the file when it exists,
    // otherwise opens its folder (first run / log disabled). Never throws into the settings UI.
    private void OpenAdaptationLogFile()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl", "adaptation-log.jsonl");
            if (File.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else
                System.Diagnostics.Process.Start("explorer.exe", Path.GetDirectoryName(path)!);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to open the adaptation log location.");
        }
    }

    // Same shape, for the raw-gaze recording. The operator has to get at this file to analyze it, and
    // asking a gaze-only user to navigate to %APPDATA% by hand is asking for the whole measurement.
    private void OpenGazeRecordingFolder()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl", "gaze-recording.csv");
            if (File.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else
                System.Diagnostics.Process.Start("explorer.exe", Path.GetDirectoryName(path)!);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to open the gaze recording location.");
        }
    }

    // ────── Pagination ──────

    private void RebuildContent()
    {
        _viewModel.ClearCategory(DwellCategory.Control);
        ContentPanel.Children.Clear();

        UpdateHeader();

        _pages = BuildPages();
        _pages = MaybeRepaginateByHeight(_pages);
        _viewModel.Settings.TotalPages = _pages.Count;
        _viewModel.Settings.CurrentPage = Math.Clamp(_viewModel.Settings.CurrentPage, 0, Math.Max(0, _viewModel.Settings.TotalPages - 1));

        RenderCurrentPage();
        RenderPagination();
    }

    /// <summary>
    /// Points the page header at the active tab. Silent if the id is unknown — the header is
    /// chrome, and a missing description must never stop the page rendering.
    ///
    /// <c>Array.Find</c> over a value-tuple array returns a DEFAULT tuple when nothing matches,
    /// not null, so both string fields come back null and the <c>??</c> is what keeps the two
    /// TextBlocks off a null Text. Reads <c>Settings.CurrentTabName</c> — the same property
    /// <see cref="BuildPages"/> and <see cref="MaybeRepaginateByHeight"/> select on, so the
    /// header can never name a different tab than the one being built.
    /// </summary>
    private void UpdateHeader()
    {
        var def = Array.Find(_tabDefs, t => t.Id == _viewModel.Settings.CurrentTabName);
        HeaderTitle.Text = def.Label ?? string.Empty;
        HeaderDescription.Text = def.Description ?? string.Empty;
    }

    /// <summary>
    /// Re-splits the current tab's rows into height-fitting pages when the tab's source
    /// opts in. Declines (passes pages through) on degenerate sizing inputs, mirroring
    /// ContentAvailableWidth.
    /// </summary>
    private IReadOnlyList<PageSpec> MaybeRepaginateByHeight(IReadOnlyList<PageSpec> pages)
    {
        if (!_sources.TryGetValue(_viewModel.Settings.CurrentTabName, out var source)
            || !source.PrefersAutoPagination)
            return pages;

        var width = ContentAvailableWidth;
        var height = ContentAvailableHeight;
        if (!(width > 0) || double.IsInfinity(width) || !(height > 0) || double.IsInfinity(height))
            return pages;

        var rows = pages.SelectMany(p => p.Rows).ToList();
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        return SettingsAutoPagination.ChunkByHeight(
            rows, row => SettingsAutoPagination.MeasureRowHeight(row, width, r, _dwellMs), height);
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
        var btn = MakeChromeButton(
            id, text, PaginationButtonWidth, PaginationButtonHeight, new Thickness(PaginationButtonMargin),
            BtnBase, Tokens.BrushTextPrimary,
            DwellCategory.Pagination, PagBtnRadius,
            () => { if (id == "pag_prev") _viewModel.Settings.PreviousPage(); else _viewModel.Settings.NextPage(); },
            () => { },
            _pagButtons, _pagBrushes);
        ((TextBlock)btn.Child).FontSize = PaginationButtonHeight * ContainerRatio.Large;
        return btn;
    }

    // ────── Page builders per tab ──────

    private IReadOnlyList<PageSpec> BuildPages() =>
        _sources.TryGetValue(_viewModel.Settings.CurrentTabName, out var source)
            ? source.Build()
            : Array.Empty<PageSpec>();

    // ────── Shared helpers ──────

    /// <summary>
    /// How much horizontal room a control dropped into <c>ContentPanel</c> actually gets.
    ///
    /// Read off the live elements — the sidebar column declared by <c>RootGrid</c>,
    /// <c>ContentArea</c>'s <c>Margin="16"</c> and <c>ContentCard</c>'s <c>Padding="16,8"</c>
    /// (the card the rows sit on; <c>ContentPanel</c> itself carries no margin any more, the
    /// card's padding replaced it) — so the arithmetic cannot fall out of step with the XAML
    /// that decides it. Restating any of the three here is how the sidebar's 220 -> 240 widening
    /// silently ate 20 DIP of content column with nothing noticing.
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
                - ContentCard.Padding.Left - ContentCard.Padding.Right;
        }
    }

    /// <summary>
    /// The pagination row is reserved UNCONDITIONALLY, whether or not the bar renders
    /// (a single-page tab hides it): otherwise page count would oscillate as the bar
    /// appears and disappears. Same pre-layout philosophy as ContentAvailableWidth —
    /// reads <c>Height</c>, never <c>ActualHeight</c> (content is built in the ctor,
    /// before the first layout pass), and reads every inset off the live element rather than
    /// restating it: the page header's own <c>Height</c> and <c>Margin</c>,
    /// <c>ContentArea</c>'s margin, and <c>ContentCard</c>'s padding.
    ///
    /// The header is a term here, not chrome the budget can ignore: it takes its block off
    /// EVERY content page at once, exactly as the sidebar's widening took width off every
    /// content column at once. Omitting it would over-report the budget by the header's whole
    /// block and let a page overflow the content cell — the rows below the fold being
    /// unreachable for a user whose only input is gaze. Guarded by
    /// <c>SettingsContentFitTests.TheHeightBudget_StillCountsThePageHeader</c> and by the
    /// remaining-room guard beside it.
    /// </summary>
    private double ContentAvailableHeight
    {
        get
        {
            if (double.IsNaN(Height) || double.IsInfinity(Height) || Height <= 0)
                return double.PositiveInfinity;
            return Height
                - HeaderPanel.Height - HeaderPanel.Margin.Top - HeaderPanel.Margin.Bottom
                - ContentArea.Margin.Top - ContentArea.Margin.Bottom
                - ContentCard.Padding.Top - ContentCard.Padding.Bottom
                - PaginationReserveHeight;
        }
    }

    private double PaginationReserveHeight =>
        PaginationButtonHeight + 2 * PaginationButtonMargin + PaginationPanel.Margin.Top + PaginationPanel.Margin.Bottom;

    /// <summary>
    /// The tab pager row's height, reserved unconditionally (the row collapses while every tab
    /// fits one page, but the budget math counts it anyway) — same philosophy as
    /// <see cref="PaginationReserveHeight"/>: a conditional reserve makes the tab-page count
    /// oscillate as the pager appears and disappears.
    /// </summary>
    private double TabPagerReserveHeight =>
        PaginationButtonHeight + 2 * PaginationButtonMargin + TabPagerPanel.Margin.Top + TabPagerPanel.Margin.Bottom;

    /// <summary>
    /// How much vertical room the tab list has: the window height minus the close button's
    /// block, the pager row's reserve, and the tab panel's own margins. Read off the live
    /// elements (like <see cref="ContentAvailableWidth"/>) so the arithmetic cannot drift
    /// from the XAML that decides it; the chunker clamps degenerate values to one tab per page.
    /// </summary>
    private double SidebarTabBudget =>
        Height
        - CloseButton.Height - CloseButton.Margin.Top - CloseButton.Margin.Bottom
        - TabPagerReserveHeight
        - TabButtonPanel.Margin.Top - TabButtonPanel.Margin.Bottom;

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
        ButtonKind.Danger => (DeleteBase, Tokens.BrushSurfaceDark),
        _                 => (BtnBase,   null),
    };

    /// <summary>
    /// Constructs the control for one row spec — the single construction path shared by
    /// rendering (via <see cref="RenderSpec"/>) and by
    /// <see cref="SettingsAutoPagination"/>'s measurement, so the two can never drift.
    /// Returns a <see cref="DwellControl"/>, <see cref="IDwellComposite"/> or
    /// <see cref="DwellLabel"/> depending on the spec.
    /// The spec delegates (<c>Get</c>/<c>Set</c>/<c>Text</c>) are now invoked twice per
    /// rebuild — once to measure, once to render — so their side-effect-freedom is
    /// load-bearing.
    ///
    /// <para>
    /// The labelled arms assemble their whole ROW here — name column, control column, card
    /// minimum height — and that placement is the load-bearing part. <c>SettingsAutoPagination</c>
    /// measures a row by calling this method to build the real control and measuring it on a
    /// detached panel, so anything that changes rendered height but lives only in
    /// <see cref="RenderSpec"/> makes every measured height a lie and every page count wrong.
    /// </para>
    /// </summary>
    /// <param name="row">The row spec to build.</param>
    /// <param name="registrar">Where the built control registers its dwell targets.</param>
    /// <param name="radiusPhysical">Hit-test radius in physical screen pixels.</param>
    /// <param name="dwellMs">Dwell duration in milliseconds.</param>
    /// <param name="availableWidth">
    /// Room in the content column, from <c>ContentAvailableWidth</c> when rendering and from the
    /// measurement budget when paginating. Deliberately REQUIRED, with no default: a call site
    /// that forgot to thread the real width through would otherwise silently get a two-column row
    /// on a column too narrow for one.
    /// </param>
    internal static object CreateControl(
        SettingsRowSpec row, IDwellRegistrar registrar, double radiusPhysical, int dwellMs,
        double availableWidth)
    {
        var cat = DwellCategory.Control;
        switch (row)
        {
            case SliderSpec s:
                return LabelledRow(s, availableWidth, new DwellSlider(registrar, s.Id, s.Label,
                    s.Min, s.Max, s.Step, s.Decimals, s.Get, s.Set, cat, radiusPhysical, dwellMs,
                    showLabel: false));
            case ToggleSpec t:
                return LabelledRow(t, availableWidth, new DwellToggle(registrar, t.Id, t.Label,
                    t.Get, t.Set, cat, radiusPhysical, dwellMs, showLabel: false));
            case ComboBoxSpec c:
                return LabelledRow(c, availableWidth, new DwellComboBox(registrar, c.Id, c.Label,
                    c.Display, c.Code ?? c.Display, c.Get, c.Set, cat, radiusPhysical, dwellMs,
                    showLabel: false));
            case ColorPickerSpec cp:
                return LabelledRow(cp, availableWidth, new DwellColorPicker(registrar, cp.Id,
                    cp.Label, cp.Get, cp.Set, cat,
                    DpiHelper.LogicalToPhysicalX(cp.RadiusLogical), dwellMs, showLabel: false));
            case ButtonSpec b:
                var (fill, textBrush) = FillFor(b.Kind);
                var button = new DwellButton(registrar, b.Id, b.Label, b.OnActivate, fill, cat,
                    radiusPhysical, dwellMs, width: b.Width, height: b.Height, textBrush: textBrush);
                // A standalone settings row, not a tile in someone else's panel. Pin it to the
                // column's left axis — Stretch + the explicit Width above CENTRES in WPF, which
                // drew these buttons at x=130 while every other row kind (toggles, headings,
                // slider/combo rows) starts at x=0, two axes on one mixed page.
                //
                // And give it the SAME BOX every other row kind gets: the button is CENTRED in a
                // LabelledRowMinHeight box, exactly as a labelled row centres its 100 DIP control
                // in one. So its hit circle lands 75 DIP from the row top wherever a labelled
                // row's does, and every pitch on a mixed page is exactly 150. Hit geometry, not
                // rhythm.
                //
                // The asymmetric (4,0,4,24) this replaced measured 124 and put its circle at 50,
                // so a labelled row followed by a button row left the two centres only
                // (150-75) + 50 = 125 DIP apart — 25 DIP of clearance between two 100 DIP
                // circles, against the 40 DIP the colour-swatch grid leaves
                // (SwatchTargetGeometryTests). A stacked slider followed by a button was worse
                // still, 106 apart. That gap sat directly below the last setting on EVERY tab,
                // because every tab ends with a "Reset ..." ButtonSpec — a destructive action one
                // gaze drift from the control above it.
                //
                // The inset is DERIVED from b.Height rather than written as a literal 25, and
                // that is load-bearing: the settings pages ship three button heights, not one.
                // A flat 25 per side gives the 100 DIP buttons the intended 150/75 but leaves the
                // 90 DIP type-picker buttons at 140/70 and the 70 DIP Delete and Back buttons at
                // 120/60 — and "qa_del" sits directly under a toggle row, which a flat 25 would
                // leave 35 DIP of clearance, BELOW the swatch floor this pitch is measured
                // against. Centring in the box is the only form that holds for every height.
                //
                // Max(0, ...) only guards a hypothetical taller-than-pitch button: the tallest
                // today is 100. Such a button would keep its own height and simply set the pitch.
                //
                // DwellButton's own (4,0,4,0) default stays — toolbar/pagination/modal/composite
                // tiles keep the layout they were designed against; only the settings-row path
                // gets the row box. Nothing about the button's width, alignment, fill or hit
                // radius changes here.
                //
                // Gated by SettingsRowPitchTests, which measures ARRANGED centre-to-centre
                // distance across adjacent rows of DIFFERENT kinds, over every button height the
                // page sources actually declare.
                var buttonInset = Math.Max(0, (LabelledRowMinHeight - b.Height) / 2);
                button.Root.HorizontalAlignment = HorizontalAlignment.Left;
                button.Root.Margin = new Thickness(4, buttonInset, 4, buttonInset);
                return button;
            case LabelSpec l:
                return new DwellLabel(l.Text(), wrap: l.Wrap);
            default:
                // Keep the null-CONDITIONAL. `?.` short-circuits the whole member chain, so a null
                // row yields null rather than throwing — swapping it for `row!.GetType().Name`
                // would replace this diagnostic with the NullReferenceException it looks like it is
                // avoiding. The `??` is the actual fix: without it a null row produced
                // "Unknown settings row spec: " and trailed off, naming nothing.
                throw new InvalidOperationException(
                    $"Unknown settings row spec: {row?.GetType().Name ?? "<null>"}");
        }
    }

    /// <summary>
    /// Wraps a heading-less composite in the row that owns its name, its description and the
    /// row pitch. Two-column only where the column is wide enough for both halves; see
    /// <see cref="TwoColumnMinimumWidth"/> for why a narrow column stacks instead.
    /// </summary>
    /// <param name="spec">The row spec, for its name and optional description.</param>
    /// <param name="availableWidth">Room in the content column, in DIP.</param>
    /// <param name="inner">The composite built with <c>showLabel: false</c>.</param>
    /// <returns>The assembled row, itself an <see cref="IDwellComposite"/>.</returns>
    private static LabelledSettingsRow LabelledRow(
        SettingsRowSpec spec, double availableWidth, IDwellComposite inner) =>
        new(spec.Label, spec.Description, inner,
            twoColumn: availableWidth >= TwoColumnMinimumWidth);

    /// <summary>
    /// One settings row: the setting's NAME (and optional description) beside the control that
    /// changes it, at the <see cref="LabelledRowMinHeight"/> pitch.
    ///
    /// <para>
    /// An <see cref="IDwellComposite"/> so it drops straight into the existing three-way switch
    /// in <see cref="RenderSpec"/> and in <c>SettingsAutoPagination.MeasureRows</c> — neither
    /// learns a fourth case, and both keep going through <see cref="CreateControl"/>, so measured
    /// height and rendered height stay the same number.
    /// </para>
    /// <para>
    /// The row registers no dwell target of its own: a name is not one. Everything gaze-related
    /// belongs to <see cref="Children"/>, which is the inner composite's own cached list.
    /// </para>
    /// </summary>
    private sealed class LabelledSettingsRow : IDwellComposite
    {
        private readonly IDwellComposite _inner;
        private readonly Panel _controlHost;
        private readonly FrameworkElement _root;

        /// <summary>Builds the row.</summary>
        /// <param name="name">The setting's name, shown in the left column.</param>
        /// <param name="description">
        /// One line of explanation, or null/blank for none. A blank one draws NO TextBlock: an
        /// empty one still takes a line box and would push every row in the application taller
        /// for nothing.
        /// </param>
        /// <param name="inner">The control, built with its own heading suppressed.</param>
        /// <param name="twoColumn">
        /// True to put the name beside the control, false to stack the name above it.
        /// </param>
        internal LabelledSettingsRow(
            string name, string? description, IDwellComposite inner, bool twoColumn)
        {
            _inner = inner;

            var nameBlock = new TextBlock
            {
                Text = name,
                Foreground = Tokens.BrushTextPrimary,
                FontSize = Tokens.FontSizeTitle,
                FontWeight = Tokens.FontWeightMedium,
                TextWrapping = TextWrapping.Wrap
            };

            var nameColumn = new StackPanel();
            nameColumn.Children.Add(nameBlock);

            if (!string.IsNullOrWhiteSpace(description))
            {
                // BrushTextSecondary, not BrushTextDim. At Tokens.FontSizeSubtitle and normal
                // weight this is not WCAG "large text", so it owes 4.5:1 — and TextDim on the
                // card's SurfaceMedium measures 4.37 in Midnight, which fails. TextSecondary is
                // the dimmest token that clears 4.5 in every registered theme (5.63 / 6.79 /
                // 15.04). Pinned by SettingsRowDescriptionContrastTests over ThemeRegistry.All.
                nameColumn.Children.Add(new TextBlock
                {
                    Text = description,
                    Foreground = Tokens.BrushTextSecondary,
                    FontSize = Tokens.FontSizeSubtitle,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0)
                });
            }

            // A single-cell Grid, NOT a horizontal StackPanel, and the reason is hit geometry
            // rather than taste. A horizontal StackPanel measures its children with INFINITE
            // width, and two of the four composites lay their controls out in a WrapPanel that
            // wraps only when it is given a finite one. Measured inside a horizontal StackPanel:
            // the colour picker's four swatches stayed on one line and the last one's right edge
            // landed at 536.00 DIP in the 416 DIP narrowest column, and a six-option combo box's
            // last option reached 1101.13 — each with its dwell circle registered at a centre
            // outside the card, over pixels the user cannot see. A Grid hands its child the width
            // it was itself given, so those panels wrap (and the taller row is absorbed by
            // height-based pagination, which measures through this same path). The host holds
            // exactly one child in every case, so nothing else wanted a StackPanel.
            _controlHost = new Grid
            {
                VerticalAlignment = VerticalAlignment.Center,
                // Right in the two-column form, so the control ends on the card's right edge and
                // every row's control lines up. Left when stacked, so a stacked row keeps the same
                // left axis every other row kind (buttons, headings) sits on.
                HorizontalAlignment = twoColumn
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Left
            };

            if (twoColumn)
            {
                var grid = new Grid { MinHeight = LabelledRowMinHeight };
                grid.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = new GridLength(RowNameColumnWidth) });
                grid.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                nameColumn.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(nameColumn, 0);
                Grid.SetColumn(_controlHost, 1);
                grid.Children.Add(nameColumn);
                grid.Children.Add(_controlHost);
                _root = grid;
            }
            else
            {
                // The CONTROL gets its own LabelledRowMinHeight slot, below the name — so the
                // stacked row is TALLER than one pitch, on purpose, and the hit-circle clearance
                // the pitch buys does not depend on how wide the display happens to be.
                //
                // A shared LabelledRowMinHeight box (which this was, a StackPanel carrying that
                // MinHeight) could not work, and the arithmetic is the whole reason: the name
                // block measures 43.92 DIP at the 416 DIP narrowest column, so a 100 DIP control
                // stacked under it ran 43.92 -> 143.92 and registered its circle 93.92 down a 150
                // DIP row — 6.08 DIP of row beneath a 50 DIP radius, where half the swatch grid's
                // 40 DIP floor is 20. A 100 DIP control under a name simply cannot be centred at
                // 75 inside a 150 DIP box; the box has to be the name's height PLUS a pitch.
                // Measured, that put a stacked slider above a "Reset ..." button at a 131.08 DIP
                // pitch (31.08 clearance) where the two-column form gets 150/50.
                //
                // Row 0 is Auto because the name WRAPS: a two-line name grows the row rather than
                // eating the control's slot. Row 1 carries the minimum, and the control is CENTRED
                // in it — that centring is the whole point, since a circle 75 DIP from each edge of
                // its own slot is what makes every adjacent pitch 150.
                //
                // What performs the centring is worth stating precisely, because it is not only
                // _controlHost's VerticalAlignment.Center (set above, for both forms). The
                // composites plate their control at an explicit Height (DwellSlider's PlateHeight
                // is 100), and WPF centres a Stretch-aligned child that is smaller than its slot —
                // so DELETING the Center changes nothing measurable: the circle stays 118.92 DIP
                // down a 193.92 DIP row at 416, and the whole pitch suite stays green. Setting the
                // host to Top does move it, to 93.92, dropping a stacked slider above a 70 DIP
                // button to 25 DIP of clearance. Keep the Center: it states the intent that the
                // inner plate's explicit height currently happens to satisfy. The invariant is
                // pinned on ARRANGED geometry, not on this property, by
                // SettingsRowPitchTests.AStackedRow_CentresItsControlInASlotDeepEnoughForHalfTheFloor.
                //
                // The outer Grid deliberately carries NO MinHeight of its own. The row's height is
                // now nameHeight + max(LabelledRowMinHeight, controlHeight); an outer minimum
                // would be redundant at best, and at worst reads as the row's pitch, which it is
                // not.
                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(
                    new RowDefinition { MinHeight = LabelledRowMinHeight });
                nameColumn.VerticalAlignment = VerticalAlignment.Top;
                nameColumn.Margin = new Thickness(0, 0, 0, 12);
                Grid.SetRow(nameColumn, 0);
                Grid.SetRow(_controlHost, 1);
                grid.Children.Add(nameColumn);
                grid.Children.Add(_controlHost);
                _root = grid;
            }
        }

        /// <inheritdoc/>
        public void Attach(Panel parent)
        {
            // Inner first: the row must measure with its content already in place.
            _inner.Attach(_controlHost);
            parent.Children.Add(_root);
        }

        /// <inheritdoc/>
        public void Detach(Panel parent)
        {
            parent.Children.Remove(_root);
            _inner.Detach(_controlHost);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Forwards the inner composite's already-cached list, so this allocates nothing.
        /// The overlay's per-frame loop reads this property once per composite per gaze frame and
        /// INDEXES the result; building a fresh collection per get would allocate at gaze rate.
        /// The declared type is <see cref="IReadOnlyList{T}"/> because that is what
        /// <see cref="IDwellComposite"/> declares — narrowing it to <c>IEnumerable</c> is a
        /// CS0738 that has broken this build before (repaired by dd1baf5c).
        /// </remarks>
        public IReadOnlyList<DwellControl> Children => _inner.Children;
    }

    /// <summary>
    /// Maps one declarative <paramref name="row"/> onto its <see cref="DwellControl"/> and places it
    /// via the <see cref="Add(DwellControl)"/>/<see cref="Add(IDwellComposite)"/>/<see cref="Add(DwellLabel)"/>
    /// overloads, constructing the control through <see cref="CreateControl"/> — the shared
    /// construction path. The single WPF-coupled point of the spec pipeline; the per-tab
    /// sources are pure.
    /// </summary>
    private void RenderSpec(SettingsRowSpec row)
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        switch (CreateControl(row, _viewModel, r, _dwellMs, ContentAvailableWidth))
        {
            case DwellControl control: Add(control); break;
            case IDwellComposite composite: Add(composite); break;
            case DwellLabel label: Add(label); break;
            default:
                throw new InvalidOperationException(
                    $"Unhandled control type for row spec: {row?.GetType().Name}");
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
    // it controls. DwellSlider's row stays the widest at ~316 DIP, but the colour picker's swatch row is
    // 4 × (SwatchWidth + 2 * SwatchHorizontalMargin) = 448 DIP, past the 416 DIP narrowest
    // content column — deliberate: a WrapPanel wraps (never clips) to a second row there,
    // and the extra height is absorbed by height-based pagination. Width overflow in a
    // composite is therefore "wrap", never "clip".
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
        TryUpdatePosition(CloseButton, "close", DwellCategory.Tab);

        // Update tab button positions (only the current tab page's borders are attached;
        // detached ones throw and are skipped) and the tab pager's
        UpdateButtonPositions(_tabButtons, DwellCategory.Tab);
        UpdateButtonPositions(_tabPagerButtons, DwellCategory.Tab);

        // Update pagination button positions
        UpdateButtonPositions(_pagButtons, DwellCategory.Pagination);

        // Update modal button positions if modal is visible
        if (_viewModel.IsModalVisible)
        {
            UpdateButtonPositions(_modalButtons, DwellCategory.Modal);
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
                // INDEXED, not foreach: this runs once per gaze frame, and a foreach over an
                // interface-typed sequence boxes an enumerator every pass — one allocation per
                // composite per frame. Children is an IReadOnlyList precisely so this loop can
                // index it; do not "simplify" it back to a foreach.
                var children = comp.Children;
                for (int i = 0; i < children.Count; i++)
                {
                    var child = children[i];
                    child.UpdatePosition();
                    child.UpdateVisual();
                }
            }
        }
    }

    /// <summary>
    /// Feeds each attached border's screen-space centre to the ViewModel under
    /// <paramref name="category"/>. Borders not in the visual tree throw and are skipped.
    /// </summary>
    /// <remarks>
    /// Takes the category, NOT an <c>Action</c> callback, on purpose. This runs once per gaze
    /// frame and all four call sites passed the same shape —
    /// <c>(id, pos) =&gt; _viewModel.UpdatePosition(id, someCategory, pos)</c> — which captures
    /// <c>this</c>, so Roslyn cannot cache it in a static field the way it does a non-capturing
    /// lambda: each call site allocated a fresh delegate every frame, and a Gen0 collection
    /// suspends the gaze thread. The callback bought no flexibility a category does not, so this
    /// removes the allocation and the indirection together. Do not reintroduce the parameter.
    /// </remarks>
    private void UpdateButtonPositions(Dictionary<string, Border> borders, DwellCategory category)
    {
        foreach (var (id, border) in borders)
        {
            TryUpdatePosition(border, id, category);
        }
    }

    /// <summary>
    /// Blends each target's dwell progress into its mutable brush, toward Accent, at
    /// <paramref name="factor"/>. Shared shape of the tab-pager and content-pager feedback.
    /// The loop variable is deliberately named <c>progress</c>: DwellWashCensusTests'
    /// AccentWash regex matches <c>Blend(... Tokens.Accent, progress</c> and must keep
    /// finding these two sites.
    /// </summary>
    private static void BlendProgress(
        Dictionary<string, double> progressById,
        Dictionary<string, SolidColorBrush> brushes,
        Color baseColor,
        double factor)
    {
        foreach (var (id, progress) in progressById)
            if (brushes.TryGetValue(id, out var brush))
                brush.Color = ColorBlend.Blend(baseColor, Tokens.Accent, progress * factor);
    }

    /// <summary>
    /// Pushes one border's screen-space centre to the ViewModel under <paramref name="category"/>.
    /// Swallows the not-in-visual-tree throw, same contract as <see cref="UpdateButtonPositions"/>.
    /// </summary>
    private void TryUpdatePosition(Border border, string id, DwellCategory category)
    {
        try
        {
            var pos = border.PointToScreen(
                new Point(border.ActualWidth / 2, border.ActualHeight / 2));
            _viewModel.UpdatePosition(id, category, new PhysicalPoint(pos.X, pos.Y));
        }
        catch (InvalidOperationException)
        {
            // Border not yet rendered or not in visual tree
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
        BlendProgress(_viewModel.PagProgress, _pagBrushes, BtnBase, 0.4);
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
        var border = MakeChromeButton(
            id, text, wide ? 460 : 170, ModalButtonHeight,
            wide ? new Thickness(0, 8, 0, 8) : new Thickness(12, 0, 12, 0),
            bg, foreground,
            // category/radius are inert on this call — onActivated is null, so no Register
            // happens here; UpdateModalVisibility owns the modal registration, as today.
            DwellCategory.Modal, ModalBtnRadius,
            () => _viewModel.SimulateActivated(id, DwellCategory.Modal),
            null,
            _modalButtons, _modalBrushes);
        ((TextBlock)border.Child).FontSize = Tokens.FontSizeTitle;
        _modalBaseColors[id] = bg;
        return border;
    }
}
