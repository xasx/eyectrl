namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;

/// <summary>
/// Context-driven right-edge button bar. Owns one DwellDetector (via the base) and a
/// declarative button set supplied per interaction context. Each button occupies a fixed
/// vertical slot (anchor.Y + VerticalOffset); buttons never move when the set changes.
/// </summary>
public partial class EdgeButtonBarViewModel : DwellOverlayViewModelBase
{
    private readonly Dictionary<string, EdgeButtonDefinition> _definitions = new();
    private readonly Dictionary<string, PhysicalPoint> _seedCenters = new();
    private PhysicalPoint _edgeAnchor;

    public ObservableCollection<EdgeButtonState> Buttons { get; } = new();

    /// <summary>
    /// Raised once per logical mutation of the visible button set (<see cref="ShowButtons"/>,
    /// <see cref="AddButton"/>, <see cref="RemoveButton"/>, <see cref="Hide"/>). Consumers
    /// (e.g. the WPF overlay) should rebuild against this signal instead of subscribing to
    /// <see cref="Buttons"/>' <c>CollectionChanged</c>, which fires per-Add/Remove and would
    /// cause an O(N) rebuild inside a single <c>ShowButtons</c> call (one rebuild per added
    /// button, each clearing + recreating the canvas).
    /// </summary>
    public event Action? ButtonSetChanged;

    public EdgeButtonBarViewModel(EyeCtrlSettings settings, IMessenger messenger, IStruggleTelemetrySink? sink = null)
        : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.EdgeButtonBar, sink)
    {
    }

    public void ShowButtons(IReadOnlyList<EdgeButtonDefinition> definitions, PhysicalPoint edgeAnchor)
    {
        _edgeAnchor = edgeAnchor;
        DwellDetector.ClearTargets();
        DwellDetector.ResetCooldown(); // fresh context: don't inherit a prior activation's cooldown
        _definitions.Clear();
        _seedCenters.Clear();
        Buttons.Clear();

        foreach (var def in definitions)
            AddTarget(def);

        IsVisible = true;
        ButtonSetChanged?.Invoke();
    }

    /// <summary>
    /// Adds a button using the anchor cached by the last <see cref="ShowButtons"/> call.
    /// One-arg on purpose: a caller passing a *different* anchor here would misalign the new
    /// button relative to the survivors — exactly what the fixed-slot model prevents.
    /// </summary>
    public void AddButton(EdgeButtonDefinition definition)
    {
        AddTarget(definition);
        ButtonSetChanged?.Invoke();
    }

    public void RemoveButton(string id)
    {
        if (!_definitions.Remove(id)) return;
        _seedCenters.Remove(id);
        DwellDetector.RemoveTarget(id);
        var state = Buttons.FirstOrDefault(s => s.Id == id);
        if (state is not null) Buttons.Remove(state);
        ButtonSetChanged?.Invoke();
    }

    public void Hide()
    {
        DwellDetector.ClearTargets();
        _definitions.Clear();
        _seedCenters.Clear();
        Buttons.Clear();
        IsVisible = false;
        ButtonSetChanged?.Invoke();
    }

    protected override void OnTargetActivated(string targetId)
    {
        if (_definitions.TryGetValue(targetId, out var def))
            def.Activate(Messenger);
    }

    protected override void OnTargetProgress(string targetId, double progress)
    {
        var state = Buttons.FirstOrDefault(s => s.Id == targetId);
        if (state is not null)
            state.DwellProgress = progress;
    }

    private void AddTarget(EdgeButtonDefinition def)
    {
        _definitions[def.Id] = def;
        var center = _edgeAnchor + new PhysicalPoint(0, def.VerticalOffset);
        _seedCenters[def.Id] = center;
        var radiusPhysical = DpiHelper.LogicalToPhysicalX(def.RadiusLogical);
        DwellDetector.AddTarget(new DwellTarget(def.Id, center, radiusPhysical, def.DwellMs));
        Buttons.Add(new EdgeButtonState(def.Id, def.Symbol, def.RadiusLogical));
    }

    // ---- test hooks (used only by EdgeButtonBarViewModelTests) ----
    internal void SimulateTargetActivated(string id) => OnTargetActivated(id);
    internal void SimulateTargetProgress(string id, double progress) => OnTargetProgress(id, progress);
    internal PhysicalPoint GetTargetCenter(string id) => _seedCenters[id];
    internal IReadOnlyDictionary<string, PhysicalPoint> SeedCenters => _seedCenters;
}
