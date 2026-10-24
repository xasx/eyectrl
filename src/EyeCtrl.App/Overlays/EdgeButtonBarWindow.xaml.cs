namespace EyeCtrl.App.Overlays;

using System.ComponentModel;
using System.Windows;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Icons;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

/// <summary>
/// The concrete edge-button bar window. Implements <see cref="IEdgeButtonBar"/> so consumers depend
/// on the five members they use rather than on a WPF <c>Window</c> — see that interface for why the
/// difference mattered.
/// </summary>
public partial class EdgeButtonBarWindow : OverlayWindow, IEdgeButtonBar
{
    // Shared with ScreenGeometry's right-edge anchor so the window's border lines up on screen.
    private const double ButtonMargin = ScreenGeometry.EdgeButtonWindowMarginLogical;

    private readonly Dictionary<string, DwellArcButton> _buttons = new();
    private readonly List<(EdgeButtonState State, PropertyChangedEventHandler Handler)> _progressSubs = new();
    private double _originX, _originY;

    public EdgeButtonBarWindow()
    {
        IsClickThrough = true;
        ExcludeFromScreenCapture = true; // the action-menu preview must never bake the edge bars in
        InitializeComponent();
    }

    /// <inheritdoc />
    public EdgeButtonBarViewModel? EdgeBarVm => DataContext as EdgeButtonBarViewModel;

    /// <summary>
    /// The buttons the last <see cref="SyncButtons"/> drew, keyed by id — the same test seam
    /// <c>ModesMenuWindow.DrawnButtons</c> provides, so the fit and UIA checks measure the real
    /// control instances rather than rebuilding them.
    /// </summary>
    internal IReadOnlyDictionary<string, DwellArcButton> DrawnButtons => _buttons;

    public void ShowButtons(
        IReadOnlyList<EdgeButtonDefinition> definitions, PhysicalPoint edgeAnchor, EdgeBarOwner owner)
        => EdgeBarVm?.ShowButtons(definitions, edgeAnchor, owner);
    public void AddButton(EdgeButtonDefinition definition) => EdgeBarVm?.AddButton(definition);
    public void RemoveButton(string id) => EdgeBarVm?.RemoveButton(id);
    public void Hide(EdgeBarOwner owner) => EdgeBarVm?.Hide(owner);

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
            // Solo is the icon-only edge surface (ContainerRatio.Solo). A labelled definition —
            // the modifier bar's Shift/Ctrl/Alt — keeps Standard, whose Medium ratio is the size
            // those keycap+label faces were measured at.
            var hasIcon = definition?.Icon is not null;
            var btn = DwellArcButton.Create(
                new Point(c.X - _originX, c.Y - _originY), state.RadiusLogical,
                symbol: hasIcon ? null : state.Symbol,
                icon: definition?.Icon is { } iconKey ? ActionIcons.For(iconKey) : null,
                label: definition?.Label,
                iconSize: hasIcon && definition?.Label is null
                    ? DwellArcButton.IconSize.Solo
                    : DwellArcButton.IconSize.Standard,
                accessibleName: definition?.AccessibleName ?? definition?.Label);
            btn.UpdateProgress(state.DwellProgress);
            btn.SetPaused(state.IsDwellPaused);
            btn.AddTo(ButtonCanvas);
            _buttons[state.Id] = btn;

            PropertyChangedEventHandler handler = (_, e) =>
            {
                if (e.PropertyName == nameof(EdgeButtonState.DwellProgress)
                    && _buttons.TryGetValue(state.Id, out var b))
                    b.UpdateProgress(state.DwellProgress);
                else if (e.PropertyName == nameof(EdgeButtonState.IsDwellPaused)
                    && _buttons.TryGetValue(state.Id, out var heldButton))
                    heldButton.SetPaused(state.IsDwellPaused);
            };
            state.PropertyChanged += handler;
            _progressSubs.Add((state, handler));
        }

        // A new button set has just landed, so this window has to end up in FRONT of whatever else
        // was raised in the same beat. Nothing upstream guarantees that, for two independent
        // reasons:
        //
        //   1. OverlayManager shows this window only from the view model's IsVisible
        //      PropertyChanged (Initialize, the ButtonSetChanged/PropertyChanged pair). A context
        //      that takes the bar over while it is ALREADY showing — the action menu parks its own
        //      Close/Help here, and manual selection takes it back on the ring's Back step — writes
        //      IsVisible = true over true. [ObservableProperty] short-circuits an equal value, the
        //      handler never runs, and no Show() happens.
        //   2. A Show() would not have been enough anyway. See RaiseToTop's own doc comment on
        //      OverlayWindow: "a second Show() is a no-op that would not re-raise it".
        //
        // Manual selection is where that cost the user something. OverlayVmBindingCoordinator
        // raises the fullscreen glass backdrop from a handler subscribed one line BEFORE the one
        // that calls ShowButtons, so Close and Recapture — two of the three gaze-driven ways out of
        // that modal view — were laid out correctly, armed for dwell correctly, and invisible
        // underneath an acrylic sheet. Window.IsVisible stayed true throughout, which is why the
        // whole suite stayed green; EdgeButtonBarZOrderTests reads the Win32 Z-order instead.
        //
        // This belongs here rather than at the manual-selection call site because every context
        // sharing this bar reaches ShowButtons the same way — drag cancel, scroll stop, repeat stop
        // — and each of them can be handed the bar while it is already up.
        //
        // Reached only when there are buttons to show: the empty-set early return above is the path
        // Hide() always takes, so tearing the bar down never raises an empty window over whatever
        // replaces it.
        RaiseToTop();
    }
}
