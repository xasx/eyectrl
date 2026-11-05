namespace EyeCtrl.App.Services.Coordinators;

using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Browse;
using EyeCtrl.Core.Browse;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Serilog;

/// <summary>
/// The gaze browser's summon/wiring coordinator — the KeyboardCoordinator pattern applied to
/// the <c>BrowseShowRequestedMessage</c> the T2 ring action sends: show the window over the
/// work area, bind the browse dwell surface, construct the web source ONCE per summon (the
/// runtime is only ever constructed after the user asked for the browser, so a missing Evergreen
/// runtime degrades instead of crashing startup), wire both event directions, and keep the
/// visible toolbar above the browser window in the topmost band.
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
    /// <summary>The built-in start page: data-URI-free, no network dependency, no new loose
    /// resource — loaded via <c>NavigateToString</c> when the runtime comes up.</summary>
    public const string StartPageHtml = """
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<title>EyeCtrl Browser</title>
<style>
    body { font-family: 'Segoe UI', sans-serif; background: #181818; color: #f0f0f0;
           display: flex; flex-direction: column; align-items: center; justify-content: center;
           height: 100vh; margin: 0; }
    h1 { font-size: 42px; margin-bottom: 8px; }
    p { font-size: 20px; color: #b0b0b0; max-width: 640px; text-align: center; line-height: 1.5; }
</style>
</head>
<body>
<h1>EyeCtrl Browser</h1>
<p>Type an address in the field above and press Enter. Links, buttons and fields on the page
become gaze-dwell targets automatically — look at one to click it.</p>
</body>
</html>
""";

    private readonly Func<IBrowseWindowSurface> _createSurface;
    private readonly Func<IBrowseWebSource> _createWebSource;

    private BrowseViewModel? _vm;
    private IBrowseWindowSurface? _surface;
    private IBrowseWebSource? _webSource;
    private bool _startPageLoaded;
    private bool _chromeRectsRegistered;
    private IReadOnlyList<(string Id, PhysicalRect Rect)> _chromeRects = Array.Empty<(string, PhysicalRect)>();
    private bool _backPossible = true;
    private bool _forwardPossible = true;

    public BrowseCoordinator(
        Func<IBrowseWindowSurface> createSurface,
        Func<IBrowseWebSource> createWebSource)
    {
        _createSurface = createSurface ?? throw new ArgumentNullException(nameof(createSurface));
        _createWebSource = createWebSource ?? throw new ArgumentNullException(nameof(createWebSource));
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

        _vm = Context!.BrowseVM ?? new BrowseViewModel(Context.Settings, Context.Messenger);
        _vm.Show();

        _surface = _createSurface();
        _webSource = _createWebSource();

        WireWebSource();
        WireSurface();
        WireViewModel();

        _surface.Show(_vm);
        TryRegisterChromeRects();
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
    }

    private void WireViewModel()
    {
        _vm!.WebElementActivated += OnWebElementActivated;
        _vm.ChromeActivated += OnChromeActivated;
        _vm.DwellProgressChanged += OnDwellProgressChanged;
    }

    private void OnElementMessage(string json)
    {
        // No honest origin yet (the window not laid out) — drop the snapshot; the page reposts
        // on load/mutation/scroll, so a lost early snapshot returns.
        if (_surface is null || !_surface.TryGetWebOrigin(out var origin))
            return;

        if (WebElementMessageParser.TryParse(json, origin, out var snapshot))
            _vm!.ApplySnapshot(snapshot);
    }

    private void OnNavigationCompleted(string url, bool success)
    {
        _vm?.ClearWebTargets();
        TryRegisterChromeRects(); // review R5: a late layout gets its chrome row on the next event
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

    private void EnsureStartPage()
    {
        if (_startPageLoaded || _webSource is null)
            return;
        _startPageLoaded = true;
        _webSource.NavigateToString(StartPageHtml);
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
