namespace EyeCtrl.App.Services;

using System;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging;

/// <summary>
/// The adaptation preview panel's owner (Task 10): an <see cref="AdaptationActivation"/> participant
/// that shows, refreshes and hides <see cref="AdaptationPreviewWindow"/> on the engine's
/// <see cref="AdaptationPreviewVisibilityMessage"/> stream (§3.1/§3.6). It reads every piece of
/// placement geometry itself (P10) — the VM's message carries only the engagement id and Reset's
/// last-reported centre — computes the §3.3 anchor (beyond Reset's outer end, into the screen,
/// work-area-clamped), runs the D17 collision stub at show time (P11), builds content from the pure
/// <see cref="AdaptationPreviewSnapshot"/> with a ≥500 ms refresh throttle on
/// <see cref="EffectiveSettingsProvider.Changed"/>, holds the <c>"adaptation-preview"</c> auto-hide
/// suspension holder while the panel is up (P8), and publishes <see cref="PreviewPanelActiveMessage"/>
/// for the offer coordinator's fifth input (D16 — it never writes the offers flag itself).
/// <para>
/// Lifecycle (P9 + R2-2): resolved lazily on the first <b>activation</b> (constructing the window
/// must not happen while adaptation is disabled); <see cref="Start"/> is idempotent and
/// <see cref="Dispose"/> performs the FULL self-contained teardown — hide, NoPanel for any live
/// engagement, holder release, PreviewPanelActive(false), unsubscribe — never waiting for a VM
/// message, because the disable edge fires the settings-window's and the activation's
/// <c>PropertyChanged</c> handlers in undefined order. Restartable after Dispose.
/// </para>
/// <para>
/// Crash-contained throughout (D14): a comfort feature must never take the session down, and a
/// contained show failure publishes <see cref="PreviewBoundsKind.NoPanel"/> so the VM falls back to
/// Reset's hit area immediately instead of freezing the toolbar for the 500 ms unknown window.
/// </para>
/// </summary>
internal sealed class AdaptationPreviewService : IRecipient<AdaptationPreviewVisibilityMessage>, IDisposable
{
    /// <summary>This service's named auto-hide suspension holder (P8) — the coordinator's set stays
    /// non-empty while the panel is up, regardless of what the settings window holds.</summary>
    public const string AutoHideHolder = "adaptation-preview";

    /// <summary>Minimum spacing between provider-driven content refreshes (§3.3: ≥ 500 ms).</summary>
    internal const long RefreshThrottleMs = 500;

    /// <summary>The gap between the strip's outer end (Reset's last button) and the panel, physical px.</summary>
    internal const double AnchorGapPx = 16;

    /// <summary>The panel size assumed before the first layout reports one — the collision check's
    /// stand-in estimate on a first show (Task 11 owns the real inventory; SizeToContent owns the
    /// real size, reported back via BoundsChanged).</summary>
    internal const double NominalWidthPx = 360;
    internal const double NominalHeightPx = 260;

    private readonly EyeCtrlSettings _settings;
    private readonly EffectiveSettingsProvider _provider;
    private readonly Func<IToolbarOverlay> _toolbar;
    private readonly Func<IPreviewPanel> _panel;
    private readonly IOverlayCollision _collision;
    private readonly ToolbarLifecycleBridge _suspension;
    private readonly IMessenger _messenger;
    private readonly Func<PhysicalRect> _workArea;
    private readonly Func<long> _nowMs;
    private readonly ILogger<AdaptationPreviewService>? _logger;

    private IPreviewPanel? _panelInstance;
    private bool _started;
    private int _engagementId = -1;
    private bool _panelShown;
    private bool _activePublished;
    private long _lastRefreshMs = long.MinValue;
    private double _lastWidthPx = NominalWidthPx;
    private double _lastHeightPx = NominalHeightPx;
    private PhysicalPoint _lastAnchor;

    /// <param name="settings">The live settings tree: the toolbar's dock state and edge (P10), and
    /// the adaptation/dwell/cursor settings the snapshot builds from.</param>
    /// <param name="provider">The composition layer the snapshot projects; its <c>Changed</c> drives
    /// the throttled refresh.</param>
    /// <param name="toolbar">Lazy: resolves the live toolbar overlay (and with it the overlay
    /// windows) only when a show actually needs its geometry — never at construction.</param>
    /// <param name="panel">Lazy: resolves the preview window. THIS is the P9 seam — constructing it
    /// is deferred to the first <see cref="Start"/>, which only the first enable edge reaches.</param>
    /// <param name="collision">The D17 stub (always clear); Task 11 swaps the implementation.</param>
    /// <param name="suspension">The late-bound toolbar-lifecycle coordinator carrying the
    /// named-holder suspension API (P8).</param>
    /// <param name="messenger">The app messenger — the engine's visibility stream in, bounds and
    /// panel-active publishes out.</param>
    /// <param name="workArea">The current work area in physical px, for the §3.3 clamp.</param>
    /// <param name="nowMs">Monotonic clock for the refresh throttle; injectable for headless tests.</param>
    /// <param name="logger">Optional logger.</param>
    public AdaptationPreviewService(
        EyeCtrlSettings settings,
        EffectiveSettingsProvider provider,
        Func<IToolbarOverlay> toolbar,
        Func<IPreviewPanel> panel,
        IOverlayCollision collision,
        ToolbarLifecycleBridge suspension,
        IMessenger messenger,
        Func<PhysicalRect> workArea,
        Func<long>? nowMs = null,
        ILogger<AdaptationPreviewService>? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _toolbar = toolbar ?? throw new ArgumentNullException(nameof(toolbar));
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _collision = collision ?? throw new ArgumentNullException(nameof(collision));
        _suspension = suspension ?? throw new ArgumentNullException(nameof(suspension));
        _messenger = messenger ?? throw new ArgumentNullException(nameof(messenger));
        _workArea = workArea ?? throw new ArgumentNullException(nameof(workArea));
        _nowMs = nowMs ?? (static () => Environment.TickCount64);
        _logger = logger;
    }

    /// <summary>
    /// Joins the adaptation runtime: resolves the preview window (first enable edge only — P9),
    /// force-creates its HWND so the first show is not paying window creation inside the 500 ms
    /// fallback budget, and subscribes the engine's visibility stream and the provider's
    /// <c>Changed</c>. Idempotent.</summary>
    public void Start()
    {
        if (_started)
            return;
        _started = true;

        _panelInstance = _panel();
        if (_panelInstance is System.Windows.Window window)
        {
            // The plan's first-show-latency optimization, not a correctness dependency: the 500 ms
            // unknown-bounds timeout already covers a window that somehow never appears (D14).
            _ = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        }
        _panelInstance.BoundsChanged += OnPanelBounds;
        _messenger.Register(this);
        _provider.Changed += OnProviderChanged;
        _logger?.LogInformation("Adaptation preview service started");
    }

    /// <summary>The engine's show/hide stream (VM → service, P10: the message carries no geometry).</summary>
    public void Receive(AdaptationPreviewVisibilityMessage message)
    {
        if (!_started)
            return;
        if (message.Visible)
            ShowFor(message.EngagementId, message.ResetCenter);
        else
            HideFor(message.EngagementId);
    }

    /// <summary>
    /// The full self-contained teardown (R2-2): never waits for a VM visibility-false — the disable
    /// edge may fire before (or instead of) the engine's absent-report, and without this the
    /// auto-hide holder and the offers suspension would stay held until restart. Restartable: the
    /// next <see cref="Start"/> re-subscribes and the panel can show again.</summary>
    public void Dispose()
    {
        if (!_started)
            return;
        _started = false;

        // Unsubscribe first: a message or Changed firing mid-teardown must find the service inert.
        _messenger.UnregisterAll(this);
        _provider.Changed -= OnProviderChanged;

        int liveEngagement = _engagementId;
        _engagementId = -1;
        _panelShown = false;

        try
        {
            if (_panelInstance is not null)
            {
                _panelInstance.BoundsChanged -= OnPanelBounds;
                _panelInstance.HidePanel();
            }
        }
        catch (Exception ex)
        {
            LogTeardownFailure("hide the panel", ex);
        }
        _panelInstance = null;

        ReleaseSuspension();
        if (_activePublished)
        {
            _activePublished = false;
            PublishActive(false);
        }

        // NoPanel for any live engagement so the VM's union falls back to the hit area at once —
        // including an engagement whose show failed earlier (its NoPanel already arrived; a second
        // is idempotent on the engine side, which keys the union on the engagement id).
        if (liveEngagement >= 0)
        {
            try
            {
                _messenger.Send(new AdaptationPreviewBoundsMessage(liveEngagement, PreviewBoundsKind.NoPanel, null));
            }
            catch (Exception ex)
            {
                LogTeardownFailure("publish teardown bounds", ex);
            }
        }
        _logger?.LogInformation("Adaptation preview service stopped");
    }

    private void ShowFor(int engagementId, PhysicalPoint resetCenter)
    {
        // The engagement is recorded FIRST: whatever happens below, the answer the VM receives
        // (Rect or NoPanel) must name THIS engagement.
        _engagementId = engagementId;
        _lastAnchor = resetCenter;
        try
        {
            var toolbarSettings = _settings.Toolbar;
            if (!toolbarSettings.IsDocked)
            {
                // P10: a floating toolbar has no strip end to anchor beyond — no panel, silent
                // confirm (D13's own named case). The engine's 500 ms timeout never runs.
                PublishNoPanel();
                return;
            }

            var toolbar = _toolbar();
            if (!toolbar.IsOnScreen)
            {
                // No visible strip ⇒ no Reset to anchor to; the same silent confirm.
                PublishNoPanel();
                return;
            }

            var bounds = toolbar.GetPhysicalBounds();
            var candidate = ComputePanelPlacement(
                resetCenter, toolbarSettings.DockedEdge, bounds, _lastWidthPx, _lastHeightPx, _workArea());
            if (!_collision.IsClear(candidate))
            {
                // P11: a show-time collision is NoPanel IMMEDIATELY — no 500 ms unknown wait; the
                // silent confirm is a complete safe mode (D17).
                PublishNoPanel();
                return;
            }

            var snapshot = AdaptationPreviewSnapshot.Build(
                _settings.Adaptation, _provider, _settings.Dwell, _settings.Cursor);

            // Shown is set BEFORE the call: the panel reports its bounds synchronously out of
            // ShowPanel (the real window publishes post-show), and that report must publish Rect —
            // not be dropped as a stale one. A throw below resets it in the contained-failure path.
            _panelShown = true;
            Panel().ShowPanel(snapshot, new PhysicalPoint(candidate.X, candidate.Y), toolbarSettings.DockedEdge);

            _activePublished = true;
            // Armed so the first Changed after a show refreshes immediately (the show built content
            // at t0; a fill that moved at t0+10ms is stale content), while CONSECUTIVE refreshes
            // stay ≥ RefreshThrottleMs apart. now - throttle rather than long.MinValue, whose
            // subtraction overflows and would swallow that first refresh.
            _lastRefreshMs = _nowMs() - RefreshThrottleMs;
            HoldSuspension();
            PublishActive(true);
        }
        catch (Exception ex)
        {
            // D14: contained show failure ⇒ NoPanel now, so the VM falls back instead of waiting
            // out the unknown-bounds timeout with the toolbar frozen.
            ContainedFailure("show", ex);
        }
    }

    private void HideFor(int engagementId)
    {
        if (engagementId != _engagementId)
            return; // a stale hide from an engagement already ended — not ours to answer

        _engagementId = -1;
        _panelShown = false;
        try
        {
            _panelInstance?.HidePanel();
        }
        catch (Exception ex)
        {
            LogTeardownFailure("hide the panel (user left)", ex);
        }
        ReleaseSuspension();
        if (_activePublished)
        {
            _activePublished = false;
            PublishActive(false);
        }
        // P2: NO bounds publish for the user's leave — the engine hid the panel itself and owns
        // the union from here.
    }

    private void OnProviderChanged()
    {
        if (!_started || !_panelShown)
            return;
        long now = _nowMs();
        if (now - _lastRefreshMs < RefreshThrottleMs)
            return;
        _lastRefreshMs = now;

        try
        {
            var toolbarSettings = _settings.Toolbar;
            if (!toolbarSettings.IsDocked)
            {
                HideAsPolicy();
                return;
            }
            var toolbar = _toolbar();
            if (!toolbar.IsOnScreen)
            {
                HideAsPolicy();
                return;
            }
            var bounds = toolbar.GetPhysicalBounds();
            var candidate = ComputePanelPlacement(
                _lastAnchor, toolbarSettings.DockedEdge, bounds, _lastWidthPx, _lastHeightPx, _workArea());
            if (!_collision.IsClear(candidate))
            {
                HideAsPolicy();
                return;
            }

            var snapshot = AdaptationPreviewSnapshot.Build(
                _settings.Adaptation, _provider, _settings.Dwell, _settings.Cursor);
            // (Shown is already true here — a refresh only runs with the panel up. The synchronous
            // bounds report out of ShowPanel below therefore publishes Rect as it should.)
            Panel().ShowPanel(snapshot, new PhysicalPoint(candidate.X, candidate.Y), toolbarSettings.DockedEdge);
        }
        catch (Exception ex)
        {
            // A failed refresh takes the panel down as a contained failure — D14's immediate
            // fallback is strictly safer than a half-refreshed panel.
            ContainedFailure("refresh", ex);
        }
    }

    /// <summary>The D14 contained-failure path shared by show and refresh: panel down, suspension
    /// released, offers input released, NoPanel published — immediately.</summary>
    private void ContainedFailure(string phase, Exception ex)
    {
        _logger?.LogError(ex, "Adaptation preview {Phase} failed; publishing no-panel bounds (D14)", phase);
        bool wasActive = _activePublished;
        _panelShown = false;
        _activePublished = false;
        try
        {
            _panelInstance?.HidePanel();
        }
        catch (Exception inner)
        {
            LogTeardownFailure($"hide the panel after the failed {phase}", inner);
        }
        ReleaseSuspension();
        if (wasActive)
            PublishActive(false);
        PublishNoPanel();
    }

    /// <summary>The panel must come down mid-read but the engine has not ended the engagement:
    /// HiddenKeepLast keeps the last rect in the VM's union so the leave debounce completes
    /// normally (the suppression-hold precedent, §3.5).</summary>
    private void HideAsPolicy()
    {
        _panelShown = false;
        try
        {
            _panelInstance?.HidePanel();
        }
        catch (Exception ex)
        {
            LogTeardownFailure("hide the panel (policy)", ex);
        }
        ReleaseSuspension();
        if (_activePublished)
        {
            _activePublished = false;
            PublishActive(false);
        }
        PublishBounds(PreviewBoundsKind.HiddenKeepLast, null);
    }

    private void OnPanelBounds(PhysicalRect? rect)
    {
        if (rect is null)
            return; // "bounds gone" from the panel; the absence KIND is this service's call (P2)
        if (!_panelShown || _engagementId < 0)
            return; // a stale SizeChanged after the hide — the engagement is over

        _lastWidthPx = rect.Value.Width;
        _lastHeightPx = rect.Value.Height;
        PublishBounds(PreviewBoundsKind.Rect, rect);
    }

    /// <summary>
    /// The §3.3 anchor, in physical px (pure — headless-testable): the panel's near edge at Reset's
    /// outer end (the strip's far end along its run — Reset is the band's last button), extending
    /// away from the run, its across-edge aligned with the strip's docked-edge side (offset into
    /// the screen), clamped to the work area. A degenerate (unmeasured) toolbar rect falls back to
    /// the Reset centre rather than snapping to the screen origin.
    /// </summary>
    internal static PhysicalRect ComputePanelPlacement(
        PhysicalPoint resetCenter,
        string dockedEdge,
        PhysicalRect toolbar,
        double panelWidthPx,
        double panelHeightPx,
        PhysicalRect workArea)
    {
        bool verticalStrip = string.Equals(dockedEdge, "Left", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(dockedEdge, "Right", StringComparison.OrdinalIgnoreCase);
        bool rightDock = string.Equals(dockedEdge, "Right", StringComparison.OrdinalIgnoreCase);
        bool bottomDock = string.Equals(dockedEdge, "Bottom", StringComparison.OrdinalIgnoreCase);

        double x, y;
        if (verticalStrip)
        {
            y = Degenerate(toolbar.Height)
                ? resetCenter.Y + AnchorGapPx
                : toolbar.Y + toolbar.Height + AnchorGapPx;
            // Into the screen from the docked edge: beside the strip, on its inside.
            x = rightDock ? toolbar.X - panelWidthPx : toolbar.X;
        }
        else
        {
            x = Degenerate(toolbar.Width)
                ? resetCenter.X + AnchorGapPx
                : toolbar.X + toolbar.Width + AnchorGapPx;
            y = bottomDock ? toolbar.Y + toolbar.Height - panelHeightPx : toolbar.Y;
        }

        // Work-area clamp (defence-in-depth; D10's feed-nothing rule is the actual safety).
        // Manual min/max rather than Math.Clamp: a panel wider than the work area must pin to the
        // area's origin, not throw (Clamp requires min ≤ max).
        x = Math.Min(x, workArea.X + Math.Max(workArea.Width - panelWidthPx, 0));
        x = Math.Max(x, workArea.X);
        y = Math.Min(y, workArea.Y + Math.Max(workArea.Height - panelHeightPx, 0));
        y = Math.Max(y, workArea.Y);
        return new PhysicalRect(x, y, panelWidthPx, panelHeightPx);
    }

    private static bool Degenerate(double extent) => extent <= 0;

    private IPreviewPanel Panel() => _panelInstance ?? throw new InvalidOperationException(
        "The preview panel was not resolved; Start() must run before showing.");

    private void HoldSuspension() => WriteSuspension(true);

    private void ReleaseSuspension() => WriteSuspension(false);

    private void WriteSuspension(bool suspended)
    {
        try
        {
            var coordinator = _suspension.Coordinator;
            if (coordinator is null)
            {
                // Startup wiring defect (App attaches the bridge before the first activation can
                // fire): logged, not thrown — the panel is a comfort feature (D14). A missed HOLD
                // only risks auto-hide sliding the strip away mid-read; a missed RELEASE is covered
                // by the holder set's emptiness being the default.
                _logger?.LogWarning(
                    "Adaptation preview could not {Action} auto-hide: no toolbar coordinator attached",
                    suspended ? "suspend" : "resume");
                return;
            }
            coordinator.SetAutoHideSuspended(AutoHideHolder, suspended);
        }
        catch (Exception ex)
        {
            LogTeardownFailure($"{(suspended ? "hold" : "release")} the auto-hide suspension", ex);
        }
    }

    private void PublishNoPanel() => PublishBounds(PreviewBoundsKind.NoPanel, null);

    private void PublishBounds(PreviewBoundsKind kind, PhysicalRect? rect)
    {
        try
        {
            _messenger.Send(new AdaptationPreviewBoundsMessage(_engagementId, kind, rect));
        }
        catch (Exception ex)
        {
            LogTeardownFailure($"publish {kind} bounds", ex);
        }
    }

    private void PublishActive(bool active)
    {
        try
        {
            _messenger.Send(new PreviewPanelActiveMessage(active));
        }
        catch (Exception ex)
        {
            LogTeardownFailure($"publish panel-active({active})", ex);
        }
    }

    private void LogTeardownFailure(string what, Exception ex) =>
        _logger?.LogError(ex, "Adaptation preview teardown step failed: could not {What}", what);
}
