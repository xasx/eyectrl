namespace EyeCtrl.App.Overlays;

using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EyeCtrl.App.Services;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

/// <summary>
/// The modes menu panel (D18/D24/D29): toolbar-style DwellArcButtons — the exact control the
/// toolbar band uses, at the toolbar's own button size, with the ring's mouse glyphs and
/// self-drawn dwell arcs. The field round's text rows were far below gaze accuracy and their
/// hand-computed targets drifted from the rendered layout; this reuses the proven pattern
/// instead. Placement is top-centre under the toolbar; the window reports each button's real
/// physical-pixel centre back to the VM (no dwell target exists before that — the ToolbarWindow
/// (0,0) lesson).
/// </summary>
public partial class ModesMenuWindow : OverlayWindow, IModesMenuOverlay
{
    private const double BelowToolbarMarginLogical = 14;
    private const double ButtonGapLogical = 12;

    /// <summary>
    /// The clearance, in DIP, every drawn label's ink box must keep from the progress arc's inner
    /// edge. The same +2 rule the shared <c>ArcLabelFit</c> test helper asserts — kept as its own
    /// constant so lowering this one cannot silently weaken that rendered check.
    /// </summary>
    private const double RequiredLabelClearanceDip = 2.0;

    private ModesMenuViewModel? _vm;
    private double _toolbarBandHeightLogical = 140;
    private double _buttonSizeLogical = 120;
    private readonly Dictionary<string, DwellArcButton> _buttons = new();
    private readonly List<(ModesMenuButtonViewModel Button, PropertyChangedEventHandler Handler)> _progressSubs = new();

    public ModesMenuWindow()
    {
        IsClickThrough = true;
        InitializeComponent();
    }

    /// <summary>Binds the VM and remembers the toolbar metrics for placement and button sizing.</summary>
    public void Configure(ModesMenuViewModel vm, double toolbarSizeLogical)
    {
        DetachProgressSubs();
        _vm = vm;
        DataContext = vm;
        _buttonSizeLogical = toolbarSizeLogical;
        _toolbarBandHeightLogical = toolbarSizeLogical + BelowToolbarMarginLogical;
    }

    /// <summary>Places the panel top-centre under the toolbar and reports each button's dwell target.</summary>
    public void ShowAtTopCentre()
    {
        if (_vm is null) return;

        var logicalW = System.Windows.SystemParameters.PrimaryScreenWidth;
        var positions = BuildButtonsAt(
            _buttonSizeLogical,
            (logicalW - PanelWidthLogical(_buttonSizeLogical)) / 2.0,
            _toolbarBandHeightLogical);

        Show();
        _vm.SetRowScreenPositions(positions);
    }

    /// <summary>
    /// The headless half of <see cref="ShowAtTopCentre"/> (critique M3): lays the panel out for
    /// <paramref name="sizeLogical"/> at the given window origin and rebuilds the buttons, without
    /// <c>Show()</c>. Returns the row targets it computed; production publishes them through the VM
    /// only after the window is shown. The fit tests build real buttons through this seam on an STA
    /// thread, so they measure what the window draws rather than a re-implementation of it.
    /// </summary>
    internal IReadOnlyList<(string Id, PhysicalPoint Centre, double Radius)> BuildButtonsAt(
        double sizeLogical, double windowLeft, double windowTop)
    {
        _buttonSizeLogical = sizeLogical;
        Width = PanelWidthLogical(sizeLogical);
        Height = sizeLogical + 2 * ButtonGapLogical;
        Left = windowLeft;
        Top = windowTop;

        RebuildButtons();
        return _vm is null
            ? Array.Empty<(string, PhysicalPoint, double)>()
            : ComputeButtonCentres(_vm.RowDefinitions, windowLeft, windowTop, sizeLogical);
    }

    private double PanelWidthLogical(double sizeLogical)
        => _vm is null
            ? 0
            : _vm.RowDefinitions.Count * sizeLogical + (_vm.RowDefinitions.Count + 1) * ButtonGapLogical;

    /// <summary>
    /// Per-button target centres in physical px. Pure arithmetic so a geometry test can pin it:
    /// one (size + gap) slot per button after a gap + half-size inset (design D29; supersedes the
    /// row-centre helper from review round 3 — same distinctness contract, horizontal layout).
    /// </summary>
    internal static IReadOnlyList<(string Id, PhysicalPoint Centre, double Radius)> ComputeButtonCentres(
        IReadOnlyList<(string Id, InteractionAction Action, string Label)> definitions,
        double windowLeft, double windowTop, double buttonSizeLogical)
    {
        var result = new List<(string, PhysicalPoint, double)>();
        for (var i = 0; i < definitions.Count; i++)
        {
            var centreLogicalX = windowLeft + ButtonGapLogical + i * (buttonSizeLogical + ButtonGapLogical)
                                 + buttonSizeLogical / 2;
            var centreLogicalY = windowTop + ButtonGapLogical + buttonSizeLogical / 2;
            result.Add((definitions[i].Id,
                new PhysicalPoint(
                    DpiHelper.LogicalToPhysicalX(centreLogicalX),
                    DpiHelper.LogicalToPhysicalY(centreLogicalY)),
                DpiHelper.LogicalToPhysicalX(buttonSizeLogical / 2)));
        }
        return result;
    }

    private void RebuildButtons()
    {
        DetachProgressSubs();
        ButtonCanvas.Children.Clear();
        _buttons.Clear();
        if (_vm is null) return;

        var radius = _buttonSizeLogical / 2;
        var showLabels = LabelsFit(_buttonSizeLogical);
        for (var i = 0; i < _vm.RowDefinitions.Count; i++)
        {
            var (id, action, label) = _vm.RowDefinitions[i];
            var centreLogical = new Point(
                ButtonGapLogical + i * (_buttonSizeLogical + ButtonGapLogical) + radius,
                ButtonGapLogical + radius);
            var icon = action switch
            {
                Core.Models.InteractionAction.LeftClick => ActionIcons.For(Core.Models.ActionIcon.LeftClick),
                Core.Models.InteractionAction.RightClick => ActionIcons.For(Core.Models.ActionIcon.RightClick),
                Core.Models.InteractionAction.DoubleClick => ActionIcons.For(Core.Models.ActionIcon.DoubleClick),
                _ => ActionIcons.For(Core.Models.ActionIcon.MiddleClick),
            };
            var button = DwellArcButton.Create(
                centreLogical, radius, symbol: null, icon: icon, label: showLabels ? label : null);
            button.AddTo(ButtonCanvas);
            _buttons[id] = button;
        }

        // Arc progress: VM per-button DwellProgress → DwellArcButton.UpdateProgress.
        foreach (var vmButton in _vm.Buttons)
        {
            var handler = new PropertyChangedEventHandler((_, e) =>
            {
                if (e.PropertyName == nameof(ModesMenuButtonViewModel.DwellProgress)
                    && _buttons.TryGetValue(vmButton.Id, out var arc))
                    arc.UpdateProgress(vmButton.DwellProgress);
            });
            vmButton.PropertyChanged += handler;
            _progressSubs.Add((vmButton, handler));
        }
    }

    /// <summary>
    /// All-or-none (Option A, user decision 2026-10-05): labels are drawn only when EVERY row's
    /// label clears the arc at the drawn size, so the menu never mixes labelled and icon-only
    /// discs. The measurement is production-static
    /// (<see cref="DwellArcButton.LabelInkClearance"/>); the tests check the rendered result
    /// independently, so a measuring bug cannot certify itself.
    /// </summary>
    private bool LabelsFit(double sizeLogical)
    {
        if (_vm is null) return false;

        foreach (var (_, _, label) in _vm.RowDefinitions)
        {
            if (DwellArcButton.LabelInkClearance(sizeLogical, label, stackedUnderIcon: true)
                < RequiredLabelClearanceDip)
                return false;
        }

        return true;
    }

    /// <summary>
    /// The buttons the last rebuild drew, keyed by row id — test seam for the fit and geometry
    /// checks, which measure these real control instances rather than rebuilding them.
    /// </summary>
    internal IReadOnlyDictionary<string, DwellArcButton> DrawnButtons => _buttons;

    private void DetachProgressSubs()
    {
        foreach (var (button, handler) in _progressSubs)
            button.PropertyChanged -= handler;
        _progressSubs.Clear();
    }

    public void HideMenu()
    {
        DetachProgressSubs();
        Hide();
    }
}
