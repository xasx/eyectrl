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
            : ComputeButtonCentres(
                _vm.RowDefinitions.Select(row => row.Id).ToList(), windowLeft, windowTop, sizeLogical);
    }

    private double PanelWidthLogical(double sizeLogical)
        => _vm is null
            ? 0
            : _vm.RowDefinitions.Count * sizeLogical + (_vm.RowDefinitions.Count + 1) * ButtonGapLogical;

    /// <summary>
    /// Per-button target centres in physical px, one (size + gap) slot per id after a gap +
    /// half-size inset (design D29; supersedes the row-centre helper from review round 3 — same
    /// distinctness contract, horizontal layout). Pure arithmetic so a geometry test can pin it.
    ///
    /// <para>
    /// Takes the ids alone: the helper reads only a row's id and the count, so it stays
    /// independent of the caller's row shape (the modes VM's tuple carries the action, which
    /// this arithmetic does not need).
    /// </para>
    /// </summary>
    internal static IReadOnlyList<(string Id, PhysicalPoint Centre, double Radius)> ComputeButtonCentres(
        IReadOnlyList<string> definitionIds,
        double windowLeft, double windowTop, double buttonSizeLogical)
    {
        var result = new List<(string, PhysicalPoint, double)>();
        for (var i = 0; i < definitionIds.Count; i++)
        {
            var centreLogicalX = windowLeft + ButtonGapLogical + i * (buttonSizeLogical + ButtonGapLogical)
                                 + buttonSizeLogical / 2;
            var centreLogicalY = windowTop + ButtonGapLogical + buttonSizeLogical / 2;
            result.Add((definitionIds[i],
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
        for (var i = 0; i < _vm.RowDefinitions.Count; i++)
        {
            var (id, action) = _vm.RowDefinitions[i];
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
            // Icon-only by the user's call: labels are gone, Solo fills the freed disc, and the
            // ring's own action name ("Left click", ...) survives as the UIA name.
            var button = DwellArcButton.Create(
                centreLogical, radius, symbol: null, icon: icon,
                iconSize: DwellArcButton.IconSize.Solo,
                accessibleName: RingLabelMap.For(action));
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
