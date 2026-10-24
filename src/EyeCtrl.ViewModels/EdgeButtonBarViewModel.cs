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
///
/// <para>
/// The bar is SHARED, and it is a safety surface: in a drag, scroll or repeat mode it is the only
/// gaze-reachable way out. So it also tracks <em>who</em> shows it (<see cref="EdgeBarOwner"/>):
/// a claim saves the displaced owner's full set and anchor, a release by the current owner puts
/// the previous claim back, and a release by a non-owner is a no-op (a deferred hide from a
/// context that has already been displaced must not tear down its successor). A context that
/// releases while it is <em>displaced</em> withdraws its saved claim without touching the visible
/// set, so a closed context can never be resurrected by the next release.
/// </para>
/// </summary>
public partial class EdgeButtonBarViewModel : DwellOverlayViewModelBase
{
    // The re-arm debounce: gaze must stay outside the pending button's effective region for this
    // long before the gate lifts. 150 ms — the MaxCreditedSampleGapUs precedent. Owner-vetoable.
    private const long RearmDebounceMicroseconds = 150_000;

    private readonly Dictionary<string, EdgeButtonDefinition> _definitions = new();
    private readonly Dictionary<string, PhysicalPoint> _seedCenters = new();

    // The claim stack, top = the set currently on screen. One entry per live owner.
    private readonly List<EdgeBarClaim> _claims = new();

    private PhysicalPoint _edgeAnchor;
    private string? _pendingRearmId;
    private long? _pendingExitStartUs;

    public ObservableCollection<EdgeButtonState> Buttons { get; } = new();

    /// <summary>
    /// The owner whose buttons are on screen, or null while the bar is empty/hidden. Diagnostic and
    /// test seam; the ownership tests read it to prove a displaced owner is (or is not) restored.
    /// </summary>
    public EdgeBarOwner? CurrentOwner => _claims.Count == 0 ? null : _claims[^1].Owner;

    /// <summary>One owner's saved show: the whole set plus the anchor it was placed against.</summary>
    private sealed class EdgeBarClaim
    {
        internal EdgeBarClaim(
            EdgeBarOwner owner, IReadOnlyList<EdgeButtonDefinition> definitions, PhysicalPoint anchor)
        {
            Owner = owner;
            Definitions = new List<EdgeButtonDefinition>(definitions);
            Anchor = anchor;
        }

        internal EdgeBarOwner Owner { get; }

        /// <summary>Mutable so <see cref="AddButton"/>/<see cref="RemoveButton"/> keep the saved
        /// set equal to what the owner showed, across a displacement and restore.</summary>
        internal List<EdgeButtonDefinition> Definitions { get; }

        internal PhysicalPoint Anchor { get; }
    }

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
        : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.EdgeButtonBar, sink, dwellRegistry, settings.Dwell)
    {
    }

    /// <summary>
    /// Replaces the bar's contents WITHOUT claiming it. Kept as the raw view-model primitive (the
    /// window's parameterless overload and the component-level tests drive it); every production
    /// context goes through the ownership-aware overload instead.
    /// </summary>
    public void ShowButtons(IReadOnlyList<EdgeButtonDefinition> definitions, PhysicalPoint edgeAnchor)
    {
        _claims.Clear();
        ApplySet(definitions, edgeAnchor);
    }

    /// <summary>
    /// Shows <paramref name="definitions"/> on the bar and claims it for <paramref name="owner"/>.
    /// The previous owner's set (definitions and anchor) is saved, and is restored when
    /// <paramref name="owner"/> later releases the bar. A repeat claim by the same owner (the
    /// keyboard's flip rebuild) replaces its own claim instead of stacking a duplicate.
    /// </summary>
    public void ShowButtons(
        IReadOnlyList<EdgeButtonDefinition> definitions, PhysicalPoint edgeAnchor, EdgeBarOwner owner)
    {
        // Validate the WHOLE set before the claim is pushed or anything is cleared: a later throw
        // (or an invalid definition) must leave both the stack and the screen as they were.
        foreach (var def in definitions)
            Validate(def);

        _claims.RemoveAll(c => c.Owner == owner);
        _claims.Add(new EdgeBarClaim(owner, definitions, edgeAnchor));
        ApplySet(definitions, edgeAnchor);
    }

    /// <summary>
    /// Adds a button using the anchor cached by the last <see cref="ShowButtons"/> call.
    /// One-arg on purpose: a caller passing a *different* anchor here would misalign the new
    /// button relative to the survivors — exactly what the fixed-slot model prevents.
    /// </summary>
    public void AddButton(EdgeButtonDefinition definition)
    {
        AddTarget(definition);
        // Keep the current owner's saved set in step: a displaced-and-restored drag must come
        // back with the Back button it added after the claim.
        if (_claims.Count > 0)
            _claims[^1].Definitions.Add(definition);
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
        if (_claims.Count > 0)
            _claims[^1].Definitions.RemoveAll(d => d.Id == id);
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
        if (_claims.Count > 0)
        {
            var index = _claims[^1].Definitions.FindIndex(d => d.Id == id);
            if (index >= 0) _claims[^1].Definitions[index] = definition;
        }
        ButtonSetChanged?.Invoke();
    }

    /// <summary>
    /// Raw teardown: clears the screen AND every saved claim. Kept for the window's parameterless
    /// overload and the component-level tests; production contexts release through
    /// <see cref="Hide(EdgeBarOwner)"/> so a displaced owner's set can come back.
    /// </summary>
    public void Hide()
    {
        _claims.Clear();
        ClearSet();
    }

    /// <summary>
    /// Releases <paramref name="owner"/>'s claim. If it is the visible (top) claim, the previous
    /// owner's saved set and anchor are restored, or the bar is cleared when no claim remains.
    /// A release by an owner that is no longer visible is a no-op on screen — but it still drops
    /// that owner's saved claim, so a context that has already been displaced (and has since
    /// closed) is never restored by the next release. An owner with no claim at all is a no-op.
    /// </summary>
    public void Hide(EdgeBarOwner owner)
    {
        var index = _claims.FindLastIndex(c => c.Owner == owner);
        if (index < 0) return;

        _claims.RemoveAt(index);
        if (index < _claims.Count) return; // a displaced claim withdrew; the visible set is unchanged

        if (_claims.Count == 0)
        {
            ClearSet();
            return;
        }

        var restored = _claims[^1];
        ApplySet(restored.Definitions, restored.Anchor);
    }

    private void ApplySet(IReadOnlyList<EdgeButtonDefinition> definitions, PhysicalPoint edgeAnchor)
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

    private void ClearSet()
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

    protected override void OnTargetPauseChanged(string targetId, bool paused)
    {
        var state = Buttons.FirstOrDefault(s => s.Id == targetId);
        if (state is not null)
            state.IsDwellPaused = paused;
    }

    /// <summary>
    /// Feeds the sample to the detector, with the re-arm gate in front: while a re-arm is pending
    /// AND the pending target is the nearest hitting target — the very sample the detector would
    /// award to it — the sample is delivered as a filter that matches nothing, so no target can
    /// accumulate. The pending target has no anchor under the gate (its activation cleared it), so
    /// it builds nothing. Another target the gaze held a moment ago is released, not kept: the
    /// detector decides the "gaze is inside another target" release on the UNFILTERED hit, so the
    /// gated modifier under the gaze still drops a neighbour's hold (A1 fixup-02 M1). A hold only
    /// survives a gated run over truly empty space, for at most the pause grace.
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
