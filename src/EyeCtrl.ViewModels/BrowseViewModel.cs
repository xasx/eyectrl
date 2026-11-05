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
/// the dwell itself keeps accumulating while paused — chrome navigation and close are
/// window-internal (no OS input) and remain the paused user's way to drive or leave the browser —
/// while the EFFECTS that reach beyond the window are gated via <see cref="LastSampleWasPaused"/>:
/// a page-element activation (an OS click into the foreground) and the address bar (OS keyboard
/// focus) do not fire on a paused sample.
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

    /// <summary>Whether the coordinator suspended dwell accumulation (ScrollMode is sliding the
    /// page under the gaze — nothing may complete while elements move under a resting gaze).</summary>
    private bool _scrollSuspended;

    /// <summary>Raised when a page element's dwell completed: the coordinator performs the
    /// activation click at the element's mapped center. Not raised for a paused sample.</summary>
    public event Action<string, PhysicalRect>? WebElementActivated;

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
        DwellProgressChanged?.Invoke(null, 0);
        RaiseTargetsChanged();
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
            DwellDetector.AddTarget(new DwellTarget(id, rect, _settings.Dwell.ToolbarButtonDwellMs));
        }

        foreach (var stale in _chromeIds.Where(id => !chromeTargets.Any(t => t.Id == id)).ToList())
        {
            _chromeIds.Remove(stale);
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

        if (IsVisible && SampleMayDwell(message) && !_scrollSuspended)
            DwellDetector.Update(message.Position, message.TimestampMicroseconds);
    }

    protected override void OnTargetActivated(string targetId)
    {
        if (targetId.StartsWith(ChromePrefix, StringComparison.Ordinal))
        {
            // The address bar takes OS keyboard focus — a paused dwell must not fire it. The
            // navigation and close buttons are window-internal (no OS input) and stay live: they
            // are how a paused user drives and leaves the browser.
            if (targetId == ChromeAddressId && LastSampleWasPaused)
                return;
            ChromeActivated?.Invoke(targetId);
            return;
        }

        // A page element's activation is an OS click into the foreground — suppressed while
        // paused, like every non-safety effect on a DwellActiveWhilePaused surface.
        if (LastSampleWasPaused)
            return;

        if (_webRects.TryGetValue(targetId, out var rect))
            WebElementActivated?.Invoke(targetId, rect);
    }

    protected override void OnTargetProgress(string targetId, double progress) =>
        DwellProgressChanged?.Invoke(targetId, progress);

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
