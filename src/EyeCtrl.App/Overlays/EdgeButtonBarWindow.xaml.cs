namespace EyeCtrl.App.Overlays;

using System.ComponentModel;
using System.Windows;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Icons;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

public partial class EdgeButtonBarWindow : OverlayWindow
{
    // Shared with ScreenGeometry's right-edge anchor so the window's border lines up on screen.
    private const double ButtonMargin = ScreenGeometry.EdgeButtonWindowMarginLogical;

    private readonly Dictionary<string, DwellArcButton> _buttons = new();
    private readonly List<(EdgeButtonState State, PropertyChangedEventHandler Handler)> _progressSubs = new();
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
        // Detach the previous rebuild's progress handlers before clearing, so surviving
        // EdgeButtonState instances (e.g. Drag_cancel while Drag_back is added/removed) don't
        // accumulate duplicate subscriptions across successive SyncButtons calls.
        foreach (var (state, handler) in _progressSubs)
            state.PropertyChanged -= handler;
        _progressSubs.Clear();

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

        // Bounding box from each button's own radius (radii differ per context).
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var state in vm.Buttons)
        {
            var c = logical[state.Id];
            var ext = state.RadiusLogical + ButtonMargin;
            minX = Math.Min(minX, c.X - ext); maxX = Math.Max(maxX, c.X + ext);
            minY = Math.Min(minY, c.Y - ext); maxY = Math.Max(maxY, c.Y + ext);
        }
        _originX = minX; _originY = minY;
        Left = _originX; Top = _originY;
        Width = maxX - minX; Height = maxY - minY;

        foreach (var state in vm.Buttons)
        {
            var c = logical[state.Id];
            // A definition may name a drawn icon and a label instead of a Unicode glyph; the
            // state only carries the glyph, so the definition is the one that knows.
            var definition = vm.DefinitionFor(state.Id);
            var btn = DwellArcButton.Create(
                new Point(c.X - _originX, c.Y - _originY), state.RadiusLogical,
                symbol: definition?.Icon is null ? state.Symbol : null,
                icon: definition?.Icon is { } iconKey ? ActionIcons.For(iconKey) : null,
                label: definition?.Label);
            btn.UpdateProgress(state.DwellProgress);
            btn.AddTo(ButtonCanvas);
            _buttons[state.Id] = btn;

            PropertyChangedEventHandler handler = (_, e) =>
            {
                if (e.PropertyName == nameof(EdgeButtonState.DwellProgress)
                    && _buttons.TryGetValue(state.Id, out var b))
                    b.UpdateProgress(state.DwellProgress);
            };
            state.PropertyChanged += handler;
            _progressSubs.Add((state, handler));
        }
    }
}
