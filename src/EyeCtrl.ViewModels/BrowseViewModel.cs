namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Browse;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The gaze browser's dwell surface: chrome buttons (back / forward / home / reload / close /
/// address) plus the page's interactive elements, enumerated by the injected bridge script and
/// reconciled through <see cref="WebElementSetReconciler"/>. WPF-free like every ViewModel: the
/// View (BrowseWindow) draws the cue and the coordinator maps the activation.
/// <para>
/// Pause semantics follow the task-switcher rule (<see cref="DwellOverlayViewModelBase.DwellActiveWhilePaused"/>):
/// the dwell itself keeps accumulating while paused — but only the way OUT stays effectful
/// (external review M11 + the base rule that reserves the opt-in for it): while paused, Close
/// works, while Back/Forward/Home/Reload (a reload can drop form input), the address bar (OS
/// keyboard focus) and page-element activation (an OS click into the foreground) do not fire
/// on a paused sample. The dwell-completion cue (<see cref="Messages.DwellActivationMessage"/>)
/// publishes only where an effect really ran — a suppressed activation is silent (review M10).
/// </para>
/// <para>
/// The coordinator installs <see cref="MayDwellAt"/> (external review I3): the browser must not
/// accumulate dwell on what lies beneath another EyeCtrl surface — the summonable keyboard, the
/// raised toolbar, the ring. The whole window's bounds also feed the note gate's occluder
/// (<see cref="WindowOccluderRects"/>, external review I4): an opaque browser occludes notes
/// across its whole face.
/// </para>
/// </summary>
public partial class BrowseViewModel : DwellOverlayViewModelBase
{
    /// <summary>The chrome row's dwell target id namespace, distinct from the page's
    /// <see cref="WebElementInfo.IdPrefix"/>-prefixed element ids.</summary>
    public const string ChromePrefix = "chrome_";

    /// <summary>The chrome row's dwell target ids, in row order.</summary>
    public const string ChromeBackId = ChromePrefix + "back";
    public const string ChromeForwardId = ChromePrefix + "forward";
    public const string ChromeHomeId = ChromePrefix + "home";
    public const string ChromeReloadId = ChromePrefix + "reload";
    public const string ChromeCloseId = ChromePrefix + "close";
    public const string ChromeAddressId = ChromePrefix + "address";

    /// <summary>
    /// Top-left displacement (either axis, physical px) above which a page element counts as
    /// moved rather than drifted: its dwell target is re-registered fresh, discarding accumulated
    /// dwell — an element scrolling under a resting gaze must not complete a dwell it no longer
    /// sits on. Forwarded to <see cref="WebElementSetReconciler.Plan"/>.
    /// </summary>
    public const double MoveResetThresholdPx = 24.0;

    private readonly EyeCtrlSettings _settings;
    private readonly SynchronizationContext? _syncContext;

    /// <summary>The page snapshot the detector's web targets currently reflect; null when the
    /// window holds no page elements (before the first snapshot, after a navigation, after hide).</summary>
    private WebElementSnapshot? _lastSnapshot;

    /// <summary>The registered web targets' rects, for the activation event's payload.</summary>
    private readonly Dictionary<string, PhysicalRect> _webRects = new(StringComparer.Ordinal);

    /// <summary>The chrome ids currently registered, so <see cref="SetChromeTargets"/> can
    /// unregister chrome that a re-show's layout dropped.</summary>
    private readonly HashSet<string> _chromeIds = new(StringComparer.Ordinal);

    /// <summary>The chrome targets' live rects, for the re-arm exit check (r2 I2).</summary>
    private readonly Dictionary<string, PhysicalRect> _chromeRects = new(StringComparer.Ordinal);

    /// <summary>
    /// The target that just fired, withheld from the detector until the gaze LEAVES it
    /// (review r2 I2 — the toolbar Pause rule applied to every browse target). Without it, a
    /// resting gaze re-fires every dwell+cooldown (~1.1 s): on the address box that re-ran
    /// SelectAll and the next key replaced everything typed; on a page it re-clicked Send.
    /// Cleared when a sample lands outside the target's rect, or when the target goes away.
    /// </summary>
    private string? _reArmLatch;

    /// <summary>Whether the coordinator suspended dwell accumulation (ScrollMode is sliding the
    /// page under the gaze — nothing may complete while elements move under a resting gaze).</summary>
    private bool _scrollSuspended;

    /// <summary>
    /// The whole window's physical bounds while up — the note gate's occluder source (external
    /// review I4). Empty before the coordinator reports and after hide; the coordinator falls
    /// back to the work area until the window is measured. The D17 inventory keeps the dwell
    /// rects (<see cref="LiveTargetRects"/>) — the finer set is what collision checks want.
    /// </summary>
    internal IReadOnlyList<PhysicalRect> WindowOccluderRects { get; private set; } = Array.Empty<PhysicalRect>();

    /// <summary>
    /// The per-sample gate the coordinator installs (external review I3): when another EyeCtrl
    /// surface (the keyboard, the toolbar, the ring) is the window under the gaze point, the
    /// sample must not accumulate here — a completing page target would click INTO that
    /// surface. Null (headless tests) leaves the sample ungated; the production coordinator
    /// always installs the check.
    /// </summary>
    public Func<PhysicalPoint, bool>? MayDwellAt { get; set; }

    /// <summary>The last sample that passed every gate — the point the completing dwell's
    /// click falls back to when the element's clipped center sits under another surface
    /// (review r2 M2).</summary>
    private PhysicalPoint _lastGatedGazePoint;

    /// <summary>Raised when a page element's dwell completed: the coordinator performs the
    /// activation click — at the element's clipped center, or at the completing gaze point
    /// when the center sits under another EyeCtrl surface (review r2 M2). Not raised for a
    /// paused sample.</summary>
    public event Action<string, PhysicalRect, PhysicalPoint>? WebElementActivated;

    /// <summary>Raised when a chrome button's dwell completed. Navigation/close run while paused
    /// (window-internal); the address bar does not (OS keyboard focus — suppressed while paused).</summary>
    public event Action<string>? ChromeActivated;

    /// <summary>A target's dwell progress changed (0–1): the coordinator relays it into the page
    /// for the JS-drawn cue. <c>null</c> id clears the cue (the hide path's final call).</summary>
    public event Action<string?, double>? DwellProgressChanged;

    /// <summary>Exactly one public constructor: the DI sink/registry guard
    /// (OverlayTelemetryWiringTests) depends on it. The sink and registry are load-bearing —
    /// struggle telemetry and adaptation dwell routing for the <see cref="DwellContext.Browse"/>
    /// surface ride on them.</summary>
    public BrowseViewModel(
        EyeCtrlSettings settings,
        IMessenger messenger,
        IStruggleTelemetrySink? sink = null,
        IDwellDetectorRegistry? dwellRegistry = null)
        : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.Browse, sink, dwellRegistry, settings.Dwell)
    {
        _settings = settings;
        _syncContext = SynchronizationContext.Current;
    }

    /// <summary>
    /// Deliberately dwellable while paused (the task-switcher rule): chrome navigation and close
    /// are the paused user's only way to drive or leave the browser, and gating the dwell would
    /// strand them. The EFFECTS that reach beyond the window are gated instead via
    /// <see cref="LastSampleWasPaused"/> in <see cref="OnTargetActivated"/> — the page click and
    /// the address bar do not fire on a paused sample.
    /// </summary>
    protected override bool DwellActiveWhilePaused => true;

    /// <summary>
    /// Shows the surface: everything cleared (the coordinator registers the chrome row through
    /// <see cref="SetChromeTargets"/> once the window's layout is known). Idempotent.
    /// </summary>
    public void Show()
    {
        if (IsVisible) return;
        DwellDetector.ClearTargets();
        _lastSnapshot = null;
        _webRects.Clear();
        _chromeIds.Clear();
        _chromeRects.Clear();
        _reArmLatch = null;
        WindowOccluderRects = Array.Empty<PhysicalRect>();
        IsVisible = true;
    }

    /// <summary>
    /// Hides the surface and clears every dwell target (the clear-on-hide path): chrome and web
    /// alike — the window is gone, nothing may keep accumulating. Also clears the page-side
    /// progress cue. Idempotent.
    /// </summary>
    public void Hide()
    {
        IsVisible = false;
        DwellDetector.ClearTargets();
        _lastSnapshot = null;
        _webRects.Clear();
        _chromeIds.Clear();
        _chromeRects.Clear();
        _reArmLatch = null;
        WindowOccluderRects = Array.Empty<PhysicalRect>();
        DwellProgressChanged?.Invoke(null, 0);
        RaiseTargetsChanged();
    }

    /// <summary>
    /// Coordinator-reported window bounds for the note gate's occluder (external review I4).
    /// Re-reported on layout changes; hidden clears it. Guarded on visibility (review r2 M8): a
    /// layout event from the closing window must not re-fill the occluder after Hide emptied
    /// it — that would occlude every note until the next summon.
    /// </summary>
    internal void ReportWindowBounds(PhysicalRect bounds)
    {
        if (!IsVisible)
            return;
        WindowOccluderRects = new[] { bounds };
    }

    /// <summary>
    /// Reconciles a freshly parsed page snapshot against the detector's live web targets and
    /// applies the plan: new elements register, elements moved beyond
    /// <see cref="MoveResetThresholdPx"/> re-register fresh (accumulated dwell discarded), small
    /// drift rebases in place (accumulated dwell preserved), gone elements unregister. Chrome
    /// targets are untouched — their <c>chrome_</c> ids can never collide with the page's
    /// <see cref="WebElementInfo.IdPrefix"/>-prefixed ids.
    /// </summary>
    public void ApplySnapshot(WebElementSnapshot snapshot)
    {
        var plan = WebElementSetReconciler.Plan(_lastSnapshot, snapshot, MoveResetThresholdPx);

        foreach (var remove in plan.Removes)
            DwellDetector.RemoveTarget(remove.Id);
        foreach (var reAdd in plan.ReAdds)
        {
            // The remove+add pair is what discards the accumulated dwell (DwellDetector.AddTarget
            // replaces by id, so the explicit remove first is what makes the restart visible).
            DwellDetector.RemoveTarget(reAdd.Id);
            DwellDetector.AddTarget(new DwellTarget(reAdd.Id, reAdd.RectPhysical, _settings.Dwell.ToolbarButtonDwellMs));
        }

        foreach (var add in plan.Adds)
            DwellDetector.AddTarget(new DwellTarget(add.Id, add.RectPhysical, _settings.Dwell.ToolbarButtonDwellMs));

        foreach (var rebase in plan.Rebases)
            DwellDetector.RebaseTarget(rebase.Id, rebase.RectPhysical);

        _lastSnapshot = snapshot;
        RebuildWebRects(snapshot);
        RaiseTargetsChanged();
    }

    /// <summary>
    /// Registers the chrome row's dwell targets (once per show, from the window's laid-out
    /// geometry). A chrome id from a previous show that the new layout drops is unregistered.
    /// </summary>
    public void SetChromeTargets(IReadOnlyList<(string Id, PhysicalRect Rect)> chromeTargets)
    {
        foreach (var (id, rect) in chromeTargets)
        {
            _chromeIds.Add(id);
            _chromeRects[id] = rect;
            DwellDetector.AddTarget(new DwellTarget(id, rect, _settings.Dwell.ToolbarButtonDwellMs));
        }

        foreach (var stale in _chromeIds.Where(id => !chromeTargets.Any(t => t.Id == id)).ToList())
        {
            _chromeIds.Remove(stale);
            _chromeRects.Remove(stale);
            DwellDetector.RemoveTarget(stale);
        }

        RaiseTargetsChanged();
    }

    /// <summary>
    /// Clears the page's dwell targets (navigation): chrome survives. The next page's snapshot
    /// then reconciles against an empty web set, so every element registers fresh — nothing can
    /// carry a dwell across a navigation.
    /// </summary>
    public void ClearWebTargets()
    {
        if (_lastSnapshot is not null)
        {
            foreach (var element in DeduplicatedById(_lastSnapshot.Elements))
                DwellDetector.RemoveTarget(element.Id);
        }

        _lastSnapshot = null;
        _webRects.Clear();
        RaiseTargetsChanged();
    }

    /// <summary>
    /// Coordinator-owned scroll gate: while suspended, gaze samples feed nothing, so ScrollMode's
    /// page sliding under a resting gaze can never complete an element dwell.
    /// </summary>
    public void SetScrollSuspended(bool suspended) => _scrollSuspended = suspended;

    /// <summary>
    /// The dwell rects gaze is currently evaluated against, while the surface is up — the
    /// detector snapshot (the <c>NoteManager.LiveTargetRects</c> rule: the D17 inventory must
    /// never disagree with what gaze is actually evaluated against, so this reads the detector
    /// rather than re-deriving chrome/page geometry). Empty when hidden. Internal: the App's
    /// GazeConsumerInventory reaches it through the DI singleton.
    /// </summary>
    internal IReadOnlyList<PhysicalRect> LiveTargetRects
    {
        get
        {
            if (!IsVisible)
                return Array.Empty<PhysicalRect>();
            var targets = DwellDetector.Targets;
            var rects = new List<PhysicalRect>(targets.Count);
            foreach (var target in targets)
            {
                if (target.Bounds is { } bounds)
                    rects.Add(bounds);
            }

            return rects;
        }
    }

    /// <inheritdoc />
    public override void Receive(GazeCursorMovedMessage message)
    {
        // The base's contract first: an override that skips the stamp leaves LastSampleWasPaused
        // stale-false and silently disarms the pause suppression in OnTargetActivated.
        NoteSamplePaused(message.IsPaused);

        // Review r2 I2: the just-fired target stays withheld until the gaze LEAVES it (the
        // toolbar Pause rule, applied to every browse target). A sample outside the latched
        // rect — or the latch's target being gone (navigation, layout change) — re-arms it.
        if (_reArmLatch is { } latched
            && (!TryGetLatchedRect(latched, out var latchedRect) || !latchedRect.Contains(message.Position)))
        {
            _reArmLatch = null;
        }

        // External review I3: another EyeCtrl surface (keyboard, toolbar, ring) may sit over
        // the browser — a sample whose point is under THAT surface must not accumulate here,
        // or its completion clicks into the surface on top.
        if (IsVisible && SampleMayDwell(message) && !_scrollSuspended && (MayDwellAt?.Invoke(message.Position) ?? true))
        {
            _lastGatedGazePoint = message.Position;
            DwellDetector.Update(message.Position, message.TimestampMicroseconds,
                _reArmLatch is null ? null : target => target.Id != _reArmLatch);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// External review M10: the base publishes <see cref="Messages.DwellActivationMessage"/> for
    /// every activation, including the ones the pause rules suppress — a suppressed page click
    /// or address focus would still fire the dwell-completion cue. The surface takes the
    /// publishing over and sends it exactly where an effect ran.
    /// </remarks>
    protected override bool AnnounceDwellActivation => false;

    protected override void OnTargetActivated(string targetId)
    {
        if (targetId.StartsWith(ChromePrefix, StringComparison.Ordinal))
        {
            // External review M11 + the base rule: while paused ONLY Close stays effectful — it
            // is the way out. Back/Forward/Home/Reload act on a paused gaze can drop form input
            // (a reload), and the address bar takes OS keyboard focus. Suppressed activations
            // are silent (no dwell-completion cue, review M10).
            if (LastSampleWasPaused && targetId != ChromeCloseId)
                return;
            _reArmLatch = targetId;
            ChromeActivated?.Invoke(targetId);
            Messenger.Send(new DwellActivationMessage());
            return;
        }

        // A page element's activation is an OS click into the foreground — suppressed while
        // paused, like every non-safety effect on a DwellActiveWhilePaused surface. Silent on
        // suppression (review M10).
        if (LastSampleWasPaused)
            return;

        if (_webRects.TryGetValue(targetId, out var rect))
        {
            _reArmLatch = targetId;
            WebElementActivated?.Invoke(targetId, rect, _lastGatedGazePoint);
            Messenger.Send(new DwellActivationMessage());
        }
    }

    protected override void OnTargetProgress(string targetId, double progress)
    {
        // External review M10: a paused page dwell still accumulates (the way-out rule), but
        // drawing its outline in the PAGE while every page effect is suppressed invites a
        // surprise click on unpause — the JS cue stays quiet while paused. The chrome WPF
        // strips keep drawing: window-internal feedback for the one live button.
        if (LastSampleWasPaused && !targetId.StartsWith(ChromePrefix, StringComparison.Ordinal))
            return;

        // External review r2 I2: while the just-fired target is withheld, its progress cue
        // stays quiet too — a re-filling outline over a target that cannot fire misleads.
        if (targetId == _reArmLatch)
            return;

        DwellProgressChanged?.Invoke(targetId, progress);
    }

    /// <summary>Test seam (InternalsVisibleTo): raises <see cref="DwellProgressChanged"/> directly,
    /// so the coordinator's chrome/page cue routing is assertable without driving a dwell.</summary>
    internal void RaiseDwellProgressForTest(string? id, double progress) =>
        DwellProgressChanged?.Invoke(id, progress);

    /// <summary>Rebuilds the id→rect map from the applied snapshot, first occurrence winning —
    /// the same duplicate rule the reconciler used to build the plan, so the map and the
    /// registered geometry cannot disagree.</summary>
    private void RebuildWebRects(WebElementSnapshot snapshot)
    {
        _webRects.Clear();
        foreach (var element in DeduplicatedById(snapshot.Elements))
            _webRects[element.Id] = element.RectPhysical;
    }

    private static List<WebElementInfo> DeduplicatedById(IReadOnlyList<WebElementInfo> elements)
    {
        var result = new List<WebElementInfo>(elements.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in elements)
        {
            if (seen.Add(element.Id))
                result.Add(element);
        }

        return result;
    }

    /// <summary>The latched target's CURRENT rect (web or chrome), for the exit check — a
    /// rebase moves it, and a target that is gone clears the latch (r2 I2).</summary>
    private bool TryGetLatchedRect(string id, out PhysicalRect rect)
    {
        if (_webRects.TryGetValue(id, out rect!))
            return true;
        return _chromeRects.TryGetValue(id, out rect!);
    }

    /// <summary>
    /// Announces a structural target-set change to the D17 inventory. Dispatcher-marshalled like
    /// ExternalDwellService.RaiseTargetsChanged, via the captured UI <see cref="SynchronizationContext"/>
    /// (the CalibrationViewModel precedent — the ViewModels layer is WPF-free, so the context IS
    /// the dispatcher marshal): the WebView2 message handler must not run messenger sends from a
    /// non-UI thread. A null captured context (headless tests) sends synchronously, which is what
    /// makes the one-message-per-mutation pin directly assertable.
    /// </summary>
    private void RaiseTargetsChanged()
    {
        var context = _syncContext;
        if (context is null)
        {
            Messenger.Send(new BrowseTargetsChangedMessage());
            return;
        }

        context.Post(_ => Messenger.Send(new BrowseTargetsChangedMessage()), null);
    }
}
