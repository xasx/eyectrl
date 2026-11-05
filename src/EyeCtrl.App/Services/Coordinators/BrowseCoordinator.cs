namespace EyeCtrl.App.Services.Coordinators;

using System.Net;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Browse;
using EyeCtrl.Core.Browse;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Serilog;

/// <summary>
/// The gaze browser's summon/wiring coordinator — the KeyboardCoordinator pattern applied to
/// the <c>BrowseShowRequestedMessage</c> the T2 ring action sends: show the window over the
/// work area, bind the browse dwell surface, drive the web source the SURFACE created (external
/// review C2: exactly one host per summon — the window owns it and parents its control, so the
/// coordinator can never navigate an orphan), wire both event directions, and keep the visible
/// toolbar above the browser window in the topmost band.
/// <para>
/// The page→detector bridge is bounded and authenticated here (external review C3): the parser
/// only accepts envelopes carrying the surface host's per-summon secret (C3c), the host caps the
/// element count (C3b), and every element rect is clipped to the web view's on-screen bounds —
/// the click point is the clipped rect's center (C3a). A page therefore cannot register a dwell
/// target outside the browser, however it lies about its own DOM.
/// </para>
/// <para>
/// Browse stays deliberately non-modal (no ModalInteractionState flag): the gaze keyboard must
/// remain summonable over the browser for typing. Scroll suspension rides
/// <see cref="InteractionManager.StateChanged"/> — marshalled like ScrollModeController's
/// handler, because the state is written on whichever thread the edge ran on (the gaze thread
/// included) while the detector's Update is UI-thread-only.
/// </para>
/// </summary>
internal sealed class BrowseCoordinator : CoordinatorBase
{
    private readonly Func<IBrowseWindowSurface> _createSurface;

    private BrowseViewModel? _vm;
    private IBrowseWindowSurface? _surface;
    private IBrowseWebSource? _webSource;
    private string _bridgeSecret = string.Empty;
    private bool _startPageLoaded;
    private bool _chromeRectsRegistered;
    private IReadOnlyList<(string Id, PhysicalRect Rect)> _chromeRects = Array.Empty<(string, PhysicalRect)>();
    private bool _backPossible = true;
    private bool _forwardPossible = true;

    public BrowseCoordinator(Func<IBrowseWindowSurface> createSurface)
    {
        _createSurface = createSurface ?? throw new ArgumentNullException(nameof(createSurface));
    }

    protected override void OnInitialize()
    {
        var context = Context!;
        var messenger = context.Messenger;

        // The ring's Browse action (the KeyboardCoordinator pattern, ledger #1-#2 semantics:
        // idempotent show, never hides).
        messenger.Register<BrowseCoordinator, BrowseShowRequestedMessage>(this, (_, _) =>
        {
            context.Dispatcher.BeginInvoke(() => Summon());
        });

        // Scroll suspension: ScrollMode slides the page under a resting gaze — nothing may
        // complete while elements move. The state is written on whichever thread ran the edge
        // (the gaze thread included, ScrollModeController.cs's comment); DwellDetector.Update is
        // UI-thread-only, so marshal like ScrollModeController does (inline when already there).
        context.InteractionManager.StateChanged += OnInteractionStateChanged;
    }

    private void OnInteractionStateChanged(InteractionState state)
    {
        var context = Context!;
        if (context.Dispatcher.CheckAccess())
            ApplyScrollSuspension(state);
        else
            context.Dispatcher.BeginInvoke(() => ApplyScrollSuspension(state));
    }

    private void ApplyScrollSuspension(InteractionState state) =>
        _vm?.SetScrollSuspended(state == InteractionState.ScrollMode);

    // ── summon ──

    private void Summon()
    {
        if (_surface is not null)
        {
            // The ring action never hides (the Keyboard show semantics): already up → to the front.
            _surface.ActivateToFront();
            return;
        }

        // The context always carries the DI singleton (App.xaml.cs registers it with the
        // telemetry sink and the detector registry) — a missing one is a wiring bug, and the
        // silent fallback that used to sit here would have built a telemetry-less VM instead
        // of surfacing it.
        _vm = Context!.BrowseVM ?? throw new InvalidOperationException(
            "The coordination context carries no BrowseViewModel — App startup must register it.");

        _vm.Show();

        _surface = _createSurface();
        WireSurface();
        WireViewModel();

        _surface.Show(_vm);

        // The surface's own host (external review C2): created inside Show, parented to the
        // window, and the ONLY web source the coordinator drives.
        _webSource = _surface.WebSource ?? throw new InvalidOperationException(
            "The browse surface did not expose its web source after Show.");
        _bridgeSecret = _webSource.BridgeSecret;
        WireWebSource();

        // External review I3: the browser must not accumulate dwell on what lies beneath
        // another EyeCtrl surface (the summonable keyboard, the raised toolbar, the ring) —
        // the per-sample gate asks the window whether its root HWND is the one under the
        // gaze point. The VM is a singleton reused across summons, so this is re-installed
        // every summon (the closure reads the CURRENT surface field).
        _vm.MayDwellAt = point => _surface is not null && _surface.IsUnderPoint(point);

        TryRegisterChromeRects();
        TryReportWindowBounds();
        _vm.SetScrollSuspended(Context.InteractionManager.CurrentState == InteractionState.ScrollMode);

        // The toolbar is the one sibling that must stay REACHABLE over the browser: it is how
        // the user pauses/unpauses and reaches the ring while browsing. A hidden toolbar needs
        // nothing — its own later Show() enters the topmost band above Browse.
        if (Context.OverlayManager.Toolbar.IsVisible)
            Context.OverlayManager.Toolbar.RaiseToTop();

        EnsureStartPage();
    }

    private void WireWebSource()
    {
        _webSource!.ElementMessageReceived += OnElementMessage;
        _webSource.NavigationStarting += OnNavigationStarting;
        _webSource.NavigationCompleted += OnNavigationCompleted;
        _webSource.Ready += OnWebReady;
        _webSource.Failed += OnWebFailed;
        // Review R7: the possibility notifications decide whether Back/Forward stay dwellable —
        // without them the two are dead affordances that complete a dwell and do nothing.
        _webSource.BackPossibilityChanged += OnBackPossibilityChanged;
        _webSource.ForwardPossibilityChanged += OnForwardPossibilityChanged;
    }

    private void WireSurface()
    {
        _surface!.NavigateRequested += OnNavigateRequested;
        _surface.SurfaceClosed += OnSurfaceClosed;
        _surface.LayoutChanged += OnSurfaceLayoutChanged;
    }

    private void WireViewModel()
    {
        _vm!.WebElementActivated += OnWebElementActivated;
        _vm.ChromeActivated += OnChromeActivated;
        _vm.DwellProgressChanged += OnDwellProgressChanged;
    }

    private void OnElementMessage(string json)
    {
        // No honest geometry yet (the window not laid out) — drop the snapshot; the page
        // reposts on load/mutation/scroll, so a lost early snapshot returns.
        if (_surface is null || !_surface.TryGetWebOrigin(out var origin))
            return;
        if (!_surface.TryGetWebBounds(out var webBounds))
            return;

        // Authentication (review C3c): the envelope must carry the host's per-summon secret.
        if (!WebElementMessageParser.TryParse(json, origin, _bridgeSecret, out var snapshot))
            return;

        // Host-side cap (review C3b): the script caps its own snapshots; a page that bypasses
        // the script cannot flood the dwell registry (AddTarget rebuilds the snapshot array,
        // so tens of thousands of elements would also stall the UI thread).
        if (snapshot.Elements.Count > BrowseElementScript.MaxElements)
        {
            Log.Debug("Browse bridge: envelope beyond the element cap dropped");
            return;
        }

        _vm!.ApplySnapshot(ClippedToWebBounds(snapshot, webBounds));
    }

    /// <summary>
    /// Clips every element rect to the web view's on-screen bounds and drops the ones that do
    /// not intersect it (external review C3a — the decisive half of the trust boundary: the
    /// parser's ±100k clamp bounds magnitudes, not locations, and the click at a target's
    /// center is a real OS click). The script already viewport-clips; this is the app-side
    /// re-check a lying page cannot skip.
    /// </summary>
    private static WebElementSnapshot ClippedToWebBounds(WebElementSnapshot snapshot, PhysicalRect webBounds)
    {
        // Always maps: a fully-inside rect clips to itself, so the output equals the input
        // when the page told the truth — and a lying or scrolled page gets its rects pulled
        // back into the web view. Cheap at the bridge's 400-element cap.
        var clipped = new List<WebElementInfo>(snapshot.Elements.Count);
        foreach (var element in snapshot.Elements)
        {
            if (!webBounds.IntersectsWith(element.RectPhysical))
                continue;
            clipped.Add(element with { RectPhysical = Intersect(element.RectPhysical, webBounds) });
        }

        return new WebElementSnapshot(snapshot.Generation, clipped);
    }

    private static PhysicalRect Intersect(PhysicalRect a, PhysicalRect b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return new PhysicalRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private void OnNavigationStarting()
    {
        // Review I1: clear BEFORE the new document loads — NavigationCompleted fires after
        // body.onload, by which time the new page's bridge script has already posted its first
        // snapshot, and clearing there wiped it (a static page stayed target-less). The start
        // page's own initial load passes through harmlessly (nothing to clear).
        _vm?.ClearWebTargets();
    }

    private void OnNavigationCompleted(string url, bool success)
    {
        TryRegisterChromeRects(); // review R5: a late layout gets its chrome row on the next event
        TryReportWindowBounds();
    }

    private void OnWebReady()
    {
        EnsureStartPage();
        TryRegisterChromeRects(); // review R5: the runtime comes up after the window is laid out
    }

    /// <summary>
    /// Registers the chrome row once per summon — after Show, when the layout is known. A failed
    /// first read (the window not measured yet) retries on the web source's Ready and on every
    /// settled navigation (review R5): the chrome row is never permanently undwellable.
    /// </summary>
    private void TryRegisterChromeRects()
    {
        if (_chromeRectsRegistered || _vm is null || _surface is null)
            return;
        if (!_surface.TryGetChromeRects(out var rects))
        {
            Log.Debug("Browse summon: the surface could not report its chrome rects yet; retrying on Ready/navigation");
            return;
        }

        _chromeRects = rects;
        _chromeRectsRegistered = true;
        ApplyChromeTargets();
    }

    /// <summary>
    /// Applies the chrome dwell targets under the history gating (review R7): Back and Forward
    /// are registered only while the web source says they can do something, so neither is a dead
    /// affordance that completes a dwell and does nothing.
    /// </summary>
    private void ApplyChromeTargets()
    {
        if (_vm is null)
            return;
        var targets = _chromeRects
            .Where(r => (r.Id != BrowseViewModel.ChromeBackId || _backPossible)
                        && (r.Id != BrowseViewModel.ChromeForwardId || _forwardPossible))
            .ToList();
        _vm.SetChromeTargets(targets);
    }

    private void OnBackPossibilityChanged(bool possible)
    {
        _backPossible = possible;
        ApplyChromeTargets();
    }

    private void OnForwardPossibilityChanged(bool possible)
    {
        _forwardPossible = possible;
        ApplyChromeTargets();
    }

    /// <summary>
    /// Review M6: the window's on-screen geometry moved — the chrome rects and the web bounds
    /// the coordinator registered at summon are stale until re-read. Both re-reads are
    /// idempotent, so the handler simply re-runs them (SetChromeTargets reconciles the chrome
    /// set itself).
    /// </summary>
    private void OnSurfaceLayoutChanged()
    {
        _chromeRectsRegistered = false;
        TryRegisterChromeRects();
        TryReportWindowBounds();
    }

    /// <summary>
    /// Reports the window's physical bounds to the VM for the note gate's occluder (external
    /// review I4: an opaque browser window occludes notes across its WHOLE face — the dwell
    /// rects left notes live everywhere outside links and buttons). The work-area fallback
    /// covers the pre-layout window: the window sizes itself to the work area, so occluding
    /// notes with it is never wrong while the browser is up.
    /// </summary>
    private void TryReportWindowBounds()
    {
        if (_vm is null)
            return;

        if (_surface is not null && _surface.TryGetWindowBounds(out var bounds))
        {
            _vm.ReportWindowBounds(bounds);
            return;
        }

        var workArea = SystemParameters.WorkArea;
        _vm.ReportWindowBounds(new PhysicalRect(
            workArea.Left, workArea.Top, workArea.Width, workArea.Height));
    }

    /// <summary>
    /// The built-in start page: data-URI-free, no network dependency, no new loose resource —
    /// built per load, localized (review M3: the old text was hard-coded English and told a
    /// gaze-only owner to "press Enter", which for them means the gaze keyboard's Enter).
    /// </summary>
    internal static string BuildStartPageHtml()
    {
        var title = WebUtility.HtmlEncode(StringTable.Current.T("overlays.browse.start.title"));
        var hint = WebUtility.HtmlEncode(StringTable.Current.T("overlays.browse.start.hint"));
        return $$"""

<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<title>{{title}}</title>
<style>
    body { font-family: 'Segoe UI', sans-serif; background: #181818; color: #f0f0f0;
           display: flex; flex-direction: column; align-items: center; justify-content: center;
           height: 100vh; margin: 0; }
    h1 { font-size: 42px; margin-bottom: 8px; }
    p { font-size: 20px; color: #b0b0b0; max-width: 640px; text-align: center; line-height: 1.5; }
</style>
</head>
<body>
<h1>{{title}}</h1>
<p>{{hint}}</p>
</body>
</html>
""";
    }

    private void EnsureStartPage()
    {
        if (_startPageLoaded || _webSource is null)
            return;
        _startPageLoaded = true;
        _webSource.NavigateToString(BuildStartPageHtml());
    }

    /// <summary>
    /// A fatal web-source failure (review R3): the reason is shown in the address bar and STAYS —
    /// the window remains open with the chrome row (whose Close dwell is the exit) while the dead
    /// source is disposed. A missing runtime degrades, it neither crashes startup nor slams the
    /// notice shut before it can be read.
    /// </summary>
    private void OnWebFailed(string reason)
    {
        Log.Error("Browse web source failed: {Reason}", reason);
        _surface?.ShowAddressError(reason);
        DisposeWebSource();
    }

    /// <summary>Disposes the web source and detaches its handlers (shared by the failure path
    /// and the full teardown).</summary>
    private void DisposeWebSource()
    {
        if (_webSource is null)
            return;
        _webSource.ElementMessageReceived -= OnElementMessage;
        _webSource.NavigationCompleted -= OnNavigationCompleted;
        _webSource.Ready -= OnWebReady;
        _webSource.Failed -= OnWebFailed;
        _webSource.BackPossibilityChanged -= OnBackPossibilityChanged;
        _webSource.ForwardPossibilityChanged -= OnForwardPossibilityChanged;
        _webSource.Dispose();
        _webSource = null;
    }

    private void OnNavigateRequested(Uri uri) => _webSource?.Navigate(uri);

    private void OnSurfaceClosed() => Teardown(closeWindow: false);

    private void OnWebElementActivated(string id, PhysicalRect rect) =>
        Context!.InputSimulator.LeftClick(rect.Center);

    private void OnChromeActivated(string id)
    {
        // All six chrome ids route here: window-internal navigation stays live while paused
        // (the VM's DwellActiveWhilePaused rule); the address bar takes OS keyboard focus, which
        // depends on the window's post-Show focusable flip — the gaze keyboard's SendText lands
        // in whatever the OS foreground is, and that must be this window first.
        switch (id)
        {
            case BrowseViewModel.ChromeBackId:
                _webSource?.GoBack();
                break;
            case BrowseViewModel.ChromeForwardId:
                _webSource?.GoForward();
                break;
            case BrowseViewModel.ChromeHomeId:
                // Home always re-navigates: a reload of the start page, not a history hop.
                _startPageLoaded = false;
                EnsureStartPage();
                break;
            case BrowseViewModel.ChromeReloadId:
                _webSource?.Reload();
                break;
            case BrowseViewModel.ChromeCloseId:
                Teardown(closeWindow: true);
                break;
            case BrowseViewModel.ChromeAddressId:
                _surface?.FocusAddressBox();
                // Owner feedback 2026-11-05: dwelling the address box used to leave the gaze
                // keyboard hidden — a second summon dwell before any typing could start. The
                // field takes focus and the keyboard slides in with it (the SHOW message, not
                // the toolbar's toggle: if the keyboard is already up, this must not hide it).
                // The keyboard is a no-activate overlay, so it never steals the OS focus the
                // address box just took, and its SendText lands in this window — the whole
                // focusable-flip contract BrowseWindow was built on.
                Context!.Messenger.Send(new KeyboardShowRequestedMessage());
                break;
        }
    }

    private void OnDwellProgressChanged(string? id, double progress)
    {
        // Chrome targets never reach the page's JS cue (the script's ids map holds web_ ids
        // only): their progress draws WPF-side on the chrome element (review R6).
        if (id is null || id.StartsWith(BrowseViewModel.ChromePrefix, StringComparison.Ordinal))
        {
            _surface?.ShowChromeProgress(id, progress);
            return;
        }

        // System.Text.Json, not string.Format: an id arrives from the page (up to 256 chars of
        // anything), and a hand-built JSON string would let a hostile id break the payload.
        _webSource?.PostToPage(JsonSerializer.Serialize(new { type = "dwell", id, p = progress }));
    }

    // ── teardown ──

    private void Teardown(bool closeWindow)
    {
        if (_surface is null)
            return;

        var surface = _surface;
        _surface = null;

        if (_vm is not null)
        {
            _vm.WebElementActivated -= OnWebElementActivated;
            _vm.ChromeActivated -= OnChromeActivated;
            _vm.DwellProgressChanged -= OnDwellProgressChanged;
            _vm.Hide();
        }

        DisposeWebSource();

        _startPageLoaded = false;
        _chromeRectsRegistered = false;
        _chromeRects = Array.Empty<(string, PhysicalRect)>();
        _backPossible = true;
        _forwardPossible = true;

        if (closeWindow)
            surface.Close(); // fires the window's Closing → SurfaceClosed → Teardown again (no-op: _surface null)
    }

    protected override void OnDispose()
    {
        if (Context is not null)
            Context.InteractionManager.StateChanged -= OnInteractionStateChanged;
        Teardown(closeWindow: true);
    }
}
