namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Context-driven right-edge button bar. Owns one DwellDetector (via the base) and a
/// declarative button set supplied per interaction context. Each button occupies a fixed
/// vertical slot (anchor.Y + VerticalOffset); buttons never move when the set changes.
/// </summary>
public partial class EdgeButtonBarViewModel : DwellOverlayViewModelBase
{
    // The re-arm debounce: gaze must stay outside the pending button's effective region for this
    // long before the gate lifts. 150 ms — the MaxCreditedSampleGapUs precedent. Owner-vetoable.
    private const long RearmDebounceMicroseconds = 150_000;

    private readonly Dictionary<string, EdgeButtonDefinition> _definitions = new();
    private readonly Dictionary<string, PhysicalPoint> _seedCenters = new();
    private PhysicalPoint _edgeAnchor;
    private string? _pendingRearmId;
    private long? _pendingExitStartUs;

    public ObservableCollection<EdgeButtonState> Buttons { get; } = new();

    /// <summary>
    /// Raised once per logical mutation of the visible button set (<see cref="ShowButtons"/>,
    /// <see cref="AddButton"/>, <see cref="RemoveButton"/>, <see cref="ReplaceButton"/>,
    /// <see cref="Hide"/>). Consumers
    /// (e.g. the WPF overlay) should rebuild against this signal instead of subscribing to
    /// <see cref="Buttons"/>' <c>CollectionChanged</c>, which fires per-Add/Remove and would
    /// cause an O(N) rebuild inside a single <c>ShowButtons</c> call (one rebuild per added
    /// button, each clearing + recreating the canvas).
    /// </summary>
    public event Action? ButtonSetChanged;

    public EdgeButtonBarViewModel(EyeCtrlSettings settings, IMessenger messenger,
        IStruggleTelemetrySink? sink = null, IDwellDetectorRegistry? dwellRegistry = null)
        : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.EdgeButtonBar, sink, dwellRegistry)
    {
    }

    public void ShowButtons(IReadOnlyList<EdgeButtonDefinition> definitions, PhysicalPoint edgeAnchor)
    {
        // Validate the WHOLE set before clearing anything: clearing first and then throwing on a
        // later definition would leave a half-built bar behind the exception.
        foreach (var def in definitions)
            Validate(def);

        _edgeAnchor = edgeAnchor;
        DwellDetector.ClearTargets();
        DwellDetector.ResetCooldown(); // fresh context: don't inherit a prior activation's cooldown
        _pendingRearmId = null;
        _pendingExitStartUs = null;
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
        if (id == _pendingRearmId)
        {
            _pendingRearmId = null;
            _pendingExitStartUs = null;
        }
        var state = Buttons.FirstOrDefault(s => s.Id == id);
        if (state is not null) Buttons.Remove(state);
        ButtonSetChanged?.Invoke();
    }

    /// <summary>
    /// Swaps one button's definition IN PLACE — the icon variant changes, the dwell target and
    /// the cooldown are untouched. Deliberately NOT <see cref="ShowButtons"/>: that resets the
    /// detector's cooldown, which under the synchronous toggle chain re-arms a held gaze at dwell
    /// cadence (spec round-1 blocker). No-op for an unknown id.
    /// </summary>
    public void ReplaceButton(string id, EdgeButtonDefinition definition)
    {
        if (!_definitions.ContainsKey(id)) return;
        Validate(definition);
        _definitions[id] = definition;
        ButtonSetChanged?.Invoke();
    }

    public void Hide()
    {
        DwellDetector.ClearTargets();
        _pendingRearmId = null;
        _pendingExitStartUs = null;
        _definitions.Clear();
        _seedCenters.Clear();
        Buttons.Clear();
        IsVisible = false;
        ButtonSetChanged?.Invoke();
    }

    // Deliberately dwellable while paused (DwellOverlayViewModelBase.DwellActiveWhilePaused):
    // the edge bar's buttons are release affordances (e.g. drag cancel) — they must stay dwellable in any state that could strand the user.
    protected override bool DwellActiveWhilePaused => true;

    protected override void OnTargetActivated(string targetId)
    {
        if (!_definitions.TryGetValue(targetId, out var def)) return;

        if (def.RearmOnGazeExit)
        {
            // Set BEFORE Activate: order-independent under the gate, and a throwing chain leaves
            // the button gated (safe) rather than re-toggleable. Supersedes any other pending.
            _pendingRearmId = targetId;
            _pendingExitStartUs = null;
        }

        def.Activate(Messenger);
    }

    protected override void OnTargetProgress(string targetId, double progress)
    {
        var state = Buttons.FirstOrDefault(s => s.Id == targetId);
        if (state is not null)
            state.DwellProgress = progress;
    }

    /// <summary>
    /// Feeds the sample to the detector, with the re-arm gate in front: while a re-arm is pending
    /// AND the pending target is the nearest hitting target — the very sample the detector would
    /// award to it — the sample is delivered as a filter that matches nothing, so every target is
    /// cleared exactly as an ungated miss would clear it (no partial dwell survives a gated run).
    /// In-gap samples restart the exit clock BEFORE the gate: the interpolator emits held-position
    /// samples for a whole blink, and blink-edge displacement must never complete a D-span of
    /// "valid" exit.
    /// <see cref="DwellOverlayViewModelBase.LastSampleWasPaused"/> is stamped FIRST, for every
    /// message including the skipped ones — the base did that before this override existed, and
    /// restoring it keeps the pause-suppression pattern available to a future edge-bar definition
    /// that sends OS input (external-review r1 suggestion; no current reader).
    /// </summary>
    public override void Receive(GazeCursorMovedMessage message)
    {
        NoteSamplePaused(message.IsPaused);

        if (message.IsInGap)
        {
            _pendingExitStartUs = null;
            return;
        }

        if (!IsVisible || !SampleMayDwell(message)) return;

        var gated = _pendingRearmId is not null
            && DwellDetector.NearestHittingTargetId(message.Position) == _pendingRearmId;
        if (gated)
            DwellDetector.Update(message.Position, message.TimestampMicroseconds, static _ => false);
        else
            DwellDetector.Update(message.Position, message.TimestampMicroseconds);

        UpdateRearmClock(message);
    }

    private void UpdateRearmClock(GazeCursorMovedMessage message)
    {
        if (_pendingRearmId is null) return;

        if (DwellDetector.TargetHit(_pendingRearmId, message.Position))
        {
            _pendingExitStartUs = null;
            return;
        }

        _pendingExitStartUs ??= message.TimestampMicroseconds;
        if (message.TimestampMicroseconds - _pendingExitStartUs.Value >= RearmDebounceMicroseconds)
        {
            _pendingRearmId = null;
            _pendingExitStartUs = null;
        }
    }

    private void AddTarget(EdgeButtonDefinition def)
    {
        Validate(def);
        _definitions[def.Id] = def;
        var center = _edgeAnchor + new PhysicalPoint(0, def.VerticalOffset);
        _seedCenters[def.Id] = center;
        var radiusPhysical = DpiHelper.LogicalToPhysicalX(def.RadiusLogical);
        DwellDetector.AddTarget(new DwellTarget(def.Id, center, radiusPhysical, def.DwellMs));
        Buttons.Add(new EdgeButtonState(def.Id, def.Symbol, def.RadiusLogical));
    }

    /// <summary>
    /// A labelled button must carry a drawn icon. <c>DwellArcButton.Create</c> stacks a label
    /// under an icon; with no icon the label is centred on the button, where a symbol would be
    /// drawn over it — and <c>Create</c> refuses that pair outright. Validated at the one seam
    /// every definition reaches the window through, so a bad definition fails naming its own id
    /// instead of mid-draw.
    ///
    /// <para>
    /// This is not a crash guard: the coordinators that call <c>ShowButtons</c> run on the
    /// dispatcher too, so either throw is fatal in this app. The real protection is the CI sweep
    /// (<c>EdgeButtonDefinitionSweepTests</c>), which builds every definition factory in src; this
    /// check exists so the failure names the offending definition.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="definition"/> has a Label but no Icon.</exception>
    private static void Validate(EdgeButtonDefinition definition)
    {
        if (definition.Label is not null && definition.Icon is null)
            throw new ArgumentException(
                $"Edge button '{definition.Id}' has a Label but no Icon. A labelled button must "
                + "carry a drawn Icon (with an empty Symbol): the label is stacked under the icon "
                + "and would otherwise be drawn over the symbol.",
                nameof(definition));
    }

    /// <summary>
    /// The definition behind a button id, or null. Production, not a test hook: the window needs
    /// it to resolve a definition's drawn icon and label, which EdgeButtonState does not carry.
    /// </summary>
    public EdgeButtonDefinition? DefinitionFor(string id) =>
        _definitions.TryGetValue(id, out var def) ? def : null;

    // ---- test hooks (used only by EdgeButtonBarViewModelTests) ----
    internal void SimulateTargetActivated(string id) => OnTargetActivated(id);
    internal void SimulateTargetProgress(string id, double progress) => OnTargetProgress(id, progress);
    internal PhysicalPoint GetTargetCenter(string id) => _seedCenters[id];

    internal IReadOnlyDictionary<string, PhysicalPoint> SeedCenters => _seedCenters;
}
