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
        var count = _vm.RowDefinitions.Count;
        var panelW = count * _buttonSizeLogical + (count + 1) * ButtonGapLogical;
        var panelH = _buttonSizeLogical + 2 * ButtonGapLogical;
        Width = panelW;
        Height = panelH;
        Left = (logicalW - panelW) / 2.0;
        Top = _toolbarBandHeightLogical;

        RebuildButtons();
        Show();
        _vm.SetRowScreenPositions(ComputeButtonCentres(_vm.RowDefinitions, Left, Top, _buttonSizeLogical));
    }

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
            var button = DwellArcButton.Create(centreLogical, radius, symbol: null, icon: icon, label: label);
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
