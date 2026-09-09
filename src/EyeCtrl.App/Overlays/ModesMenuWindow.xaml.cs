namespace EyeCtrl.App.Overlays;

using System.Windows;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

/// <summary>
/// The modes menu panel (rework D18): small, top-centre, just below the toolbar band, toggled by
/// the toolbar's Modes button. Click-through like the other pure-gaze overlays — dwell is the
/// only interaction. The window is authoritative for placement and reports the row's real
/// physical-pixel centre back to <see cref="ModesMenuViewModel.SetRowScreenPosition"/>; until
/// that arrives the VM holds no dwell target (the ToolbarWindow (0,0) lesson).
/// </summary>
public partial class ModesMenuWindow : OverlayWindow, IModesMenuOverlay
{
    // Below the toolbar band. The toolbar's Size setting is passed via Configure; this margin
    // keeps the panel clear of the band's edge.
    private const double BelowToolbarMarginLogical = 14;

    private ModesMenuViewModel? _vm;

    public ModesMenuWindow()
    {
        IsClickThrough = true;
        InitializeComponent();
    }

    /// <summary>Binds the VM and remembers the toolbar band height for placement.</summary>
    public void Configure(ModesMenuViewModel vm, double toolbarSizeLogical)
    {
        _vm = vm;
        DataContext = vm;
        // The window is sized to its fixed content; placement happens on every show.
        Width = 380;
        Height = 104;
        _toolbarBandHeightLogical = toolbarSizeLogical + BelowToolbarMarginLogical;
    }

    private double _toolbarBandHeightLogical = 140;

    /// <summary>Places the panel top-centre under the toolbar and reports the row's dwell target.</summary>
    public void ShowAtTopCentre()
    {
        var logicalW = System.Windows.SystemParameters.PrimaryScreenWidth;
        Left = (logicalW - Width) / 2.0;
        Top = _toolbarBandHeightLogical;
        Show();

        if (_vm is null) return;

        // Row centre in physical pixels — the position the dwell target must carry. Done after
        // Show so ActualWidth/ActualHeight are real.
        var rowCentreLogicalX = Left + 16 + RepeatRow.ActualWidth / 2.0;
        var rowCentreLogicalY = Top + 12 + RepeatRow.ActualHeight / 2.0;
        var centre = new PhysicalPoint(
            DpiHelper.LogicalToPhysicalX(rowCentreLogicalX),
            DpiHelper.LogicalToPhysicalY(rowCentreLogicalY));
        var radius = DpiHelper.LogicalToPhysicalX(RepeatRow.ActualHeight / 2.0);
        _vm.SetRowScreenPosition(centre, radius);
    }

    public void HideMenu() => Hide();
}
