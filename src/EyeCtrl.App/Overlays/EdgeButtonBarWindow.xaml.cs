namespace EyeCtrl.App.Overlays;

using System.Windows;
using EyeCtrl.App.Controls;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

public partial class EdgeButtonBarWindow : OverlayWindow
{
    private const double ButtonMargin = 10;
    private const double ButtonRadiusLogical = 20;

    private readonly Dictionary<string, DwellArcButton> _buttons = new();
    private double _originX, _originY;

    public EdgeButtonBarWindow()
    {
        IsClickThrough = true;
        InitializeComponent();
    }

    public EdgeButtonBarViewModel? EdgeBarVm => DataContext as EdgeButtonBarViewModel;

    public void ShowButtons(IReadOnlyList<EdgeButtonDefinition> definitions, PhysicalPoint edgeAnchor)
        => EdgeBarVm?.ShowButtons(definitions, edgeAnchor);
    public void AddButton(EdgeButtonDefinition definition) => EdgeBarVm?.AddButton(definition);
    public void RemoveButton(string id) => EdgeBarVm?.RemoveButton(id);
    public new void Hide() => EdgeBarVm?.Hide();

    /// <summary>
    /// Rebuild DwellArcButtons whenever the VM's button set changes. Seed centers are
    /// authoritative — the VM computes them arithmetically and the detector targets use them —
    /// so the window places each button at its seed center with NO View->VM feedback loop.
    /// This mirrors <c>ManualSelectionSideButtonWindow.ShowAt</c> (bounding-box window +
    /// window-relative canvas coords), NOT <c>ToolbarWindow</c> (which needs feedback because
    /// a StackPanel determines its layout). The window is sized to a bounding box and each
    /// button is drawn at (seedCenter - windowOrigin) so the button's screen center equals
    /// its seed center exactly.
    /// </summary>
    public void SyncButtons()
    {
        ButtonCanvas.Children.Clear();
        _buttons.Clear();

        var vm = EdgeBarVm;
        if (vm is null) return;

        var logical = vm.SeedCenters.ToDictionary(
            kv => kv.Key,
            kv => new Point(
                DpiHelper.PhysicalToLogicalX(kv.Value.X),
                DpiHelper.PhysicalToLogicalY(kv.Value.Y)));

        if (logical.Count == 0) return;

        double minX = logical.Values.Min(p => p.X), maxX = logical.Values.Max(p => p.X);
        double minY = logical.Values.Min(p => p.Y), maxY = logical.Values.Max(p => p.Y);
        var r = ButtonRadiusLogical + ButtonMargin;
        _originX = minX - r; _originY = minY - r;
        Left = _originX; Top = _originY;
        Width = (maxX - minX) + 2 * r; Height = (maxY - minY) + 2 * r;

        foreach (var state in vm.Buttons)
        {
            var c = logical[state.Id];
            var btn = DwellArcButton.Create(
                new Point(c.X - _originX, c.Y - _originY), ButtonRadiusLogical, state.Symbol);
            btn.UpdateProgress(state.DwellProgress);
            btn.AddTo(ButtonCanvas);
            _buttons[state.Id] = btn;

            state.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EdgeButtonState.DwellProgress)
                    && _buttons.TryGetValue(state.Id, out var b))
                    b.UpdateProgress(state.DwellProgress);
            };
        }
    }
}
