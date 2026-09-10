namespace EyeCtrl.App.Overlays;

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.App.Services;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

/// <summary>
/// The modes menu panel (D18/D24): small, top-centre, just below the toolbar band, toggled by the
/// toolbar's Modes button. Click-through like the other pure-gaze overlays — dwell is the only
/// interaction. One row per click action; the locked action's row carries a filled marker. The
/// window is authoritative for placement and reports every row's real physical-pixel centre back
/// to <see cref="ModesMenuViewModel.SetRowScreenPositions"/>; until that arrives the VM holds no
/// dwell targets (the ToolbarWindow (0,0) lesson).
/// </summary>
public partial class ModesMenuWindow : OverlayWindow, IModesMenuOverlay
{
    private const double BelowToolbarMarginLogical = 14;
    private const double RowHeightLogical = 44;
    private const double RowSpacingLogical = 8;

    private ModesMenuViewModel? _vm;
    private double _toolbarBandHeightLogical = 140;
    private readonly List<Border> _rows = new();

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
        _toolbarBandHeightLogical = toolbarSizeLogical + BelowToolbarMarginLogical;
        RebuildRows();
    }

    private void RebuildRows()
    {
        if (_vm is null) return;
        RowsPanel.Children.Clear();
        _rows.Clear();

        foreach (var (id, _, label) in _vm.RowDefinitions)
        {
            var marker = new Ellipse
            {
                Width = 12, Height = 12,
                Fill = Tokens.BrushAccent,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 0, 10, 0),
            };
            var row = new Border
            {
                Tag = id,
                Background = Tokens.BrushSurfaceMedium,
                CornerRadius = new System.Windows.CornerRadius(Tokens.CornerRadiusStandard),
                Padding = new Thickness(14, 8, 14, 8),
                Height = RowHeightLogical,
                Margin = new Thickness(0, RowSpacingLogical / 2, 0, RowSpacingLogical / 2),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        marker,
                        new TextBlock
                        {
                            Text = label,
                            Foreground = Tokens.BrushTextPrimary,
                            FontSize = Tokens.FontSizeBodyLarge,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                },
            };
            _rows.Add(row);
            RowsPanel.Children.Add(row);
        }

        Width = 340;
        Height = _rows.Count * (RowHeightLogical + RowSpacingLogical) + 24;
    }

    /// <summary>Places the panel top-centre under the toolbar and reports each row's dwell target.</summary>
    public void ShowAtTopCentre()
    {
        var logicalW = System.Windows.SystemParameters.PrimaryScreenWidth;
        Left = (logicalW - Width) / 2.0;
        Top = _toolbarBandHeightLogical;
        UpdateMarkers();
        Show();

        if (_vm is null) return;

        _vm.SetRowScreenPositions(ComputeRowCentres(Left, Top));
    }

    /// <summary>
    /// Per-row target centres in physical px, in row order. Pure arithmetic so a geometry test
    /// can pin it: panel padding (16,12) then one (row + spacing) slot per row (review PR #139
    /// round 3 finding 2 — the original loop had no index term, so all four rows shared row 0's
    /// centre and the detector's tie-break decided which action a dwell picked).
    /// </summary>
    internal static IReadOnlyList<(string Id, PhysicalPoint Centre, double Radius)> ComputeRowCentres(
        IReadOnlyList<(string Id, InteractionAction Action, string Label)> definitions,
        double windowLeft, double windowTop)
    {
        var result = new List<(string, PhysicalPoint, double)>();
        for (var i = 0; i < definitions.Count; i++)
        {
            var centreLogicalX = windowLeft + 16 + 340 - 32;   // panel width minus horizontal padding
            var centreLogicalY = windowTop + 12 + i * (RowHeightLogical + RowSpacingLogical)
                                 + RowSpacingLogical / 2 + RowHeightLogical / 2;
            result.Add((definitions[i].Id,
                new PhysicalPoint(
                    DpiHelper.LogicalToPhysicalX(centreLogicalX),
                    DpiHelper.LogicalToPhysicalY(centreLogicalY)),
                DpiHelper.LogicalToPhysicalX(RowHeightLogical / 2.0)));
        }
        return result;
    }

    private IReadOnlyList<(string, PhysicalPoint, double)> ComputeRowCentres(
        double windowLeft, double windowTop)
        => ComputeRowCentres(_vm!.RowDefinitions, windowLeft, windowTop);

    /// <summary>Filled marker on the locked action's row, refreshed on every show.</summary>
    private void UpdateMarkers()
    {
        if (_vm is null) return;
        foreach (var row in _rows)
        {
            var id = (string)row.Tag!;
            var def = _vm.RowDefinitions.First(r => r.Id == id);
            var marker = FindMarker(row);
            if (marker is not null)
                marker.Visibility = _vm.ActiveAction == def.Action
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
    }

    private static Ellipse? FindMarker(Border row)
        => (row.Child as StackPanel)?.Children.OfType<Ellipse>().FirstOrDefault();

    public void HideMenu() => Hide();
}
