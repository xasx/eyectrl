namespace EyeCtrl.App.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using EyeCtrl.App.Browse;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;

/// <summary>
/// The gaze browser's window: a chrome row (Back / Forward / Home / Reload / address / Close)
/// over a WebView2 client area. NOT click-through, and — the one conscious override of
/// <see cref="OverlayWindow"/>'s no-activate rule — FOCUSABLE: the address box must take real
/// OS keyboard focus for physical typing. After <see cref="Show(BrowseViewModel)"/> the window
/// runs the CalibrationWindow precedent
/// <c>SetOverlayInteraction(clickThrough: false, focusable: true); Activate();</c>, because
/// <c>OverlayWindow.OnSourceInitialized</c> hard-codes <c>focusable: false</c> and a call made
/// before Show no-ops (the HWND does not exist yet) — leaving the first run unfocusable.
/// <para>
/// The foreground-weakening cross-check the base's doc comment demands: the ONLY OS-foreground
/// window EyeCtrl injects into while Browse is up is Browse itself (the gaze keyboard's
/// SendText and the element-activation clicks land in the foreground window,
/// Win32InputSimulator's Unicode/click paths), and UIA offers can never target it because
/// ControlFinder drops own-process elements.
/// </para>
/// <para>
/// The window never dwells itself: it REPORTS geometry — the chrome buttons' screen rects and
/// the WebView2 client origin (the T1 parser's injected anchor) via the two-corner
/// <see cref="TryGetScreenRect"/> PointToScreen pattern; DIP extents go in as arguments, never
/// added to a PointToScreen result. The dwell targets live on <see cref="BrowseViewModel"/>.
/// </para>
/// </summary>
public partial class BrowseWindow : OverlayWindow, IBrowseWindowSurface
{
    private readonly Dictionary<string, System.Windows.FrameworkElement> _chromeButtons;
    private readonly Dictionary<string, System.Windows.FrameworkElement> _chromeProgress;
    private WebView2BrowseHost? _webHost;
    private bool _surfaceClosedRaised;

    /// <summary>The web source this window created (lazily, on first
    /// <see cref="Show(BrowseViewModel)"/>); null before that and in headless tests — the
    /// coordinator reaches the runtime only through this, never through the concrete type.</summary>
    public IBrowseWebSource? WebSource => _webHost;

    /// <inheritdoc />
    public event Action<Uri>? NavigateRequested;

    /// <inheritdoc />
    public event Action? SurfaceClosed;

    /// <inheritdoc />
    public event Action? LayoutChanged;

    public BrowseWindow()
    {
        InitializeComponent();

        // All SIX chrome dwell elements: the five buttons plus the address box — without the
        // address box's rect the VM never registers chrome_address and, gaze being the sole
        // input, the user could never reach it to type (review R1).
        _chromeButtons = new Dictionary<string, System.Windows.FrameworkElement>(StringComparer.Ordinal)
        {
            [BrowseViewModel.ChromeBackId] = ChromeBack,
            [BrowseViewModel.ChromeForwardId] = ChromeForward,
            [BrowseViewModel.ChromeHomeId] = ChromeHome,
            [BrowseViewModel.ChromeReloadId] = ChromeReload,
            [BrowseViewModel.ChromeCloseId] = ChromeClose,
            [BrowseViewModel.ChromeAddressId] = AddressBox,
        };

        // The WPF-side dwell cue (review R6): chrome targets never reach the page's JS cue, so
        // their progress draws on the chrome element itself.
        _chromeProgress = new Dictionary<string, System.Windows.FrameworkElement>(StringComparer.Ordinal)
        {
            [BrowseViewModel.ChromeBackId] = ProgressBack,
            [BrowseViewModel.ChromeForwardId] = ProgressForward,
            [BrowseViewModel.ChromeHomeId] = ProgressHome,
            [BrowseViewModel.ChromeReloadId] = ProgressReload,
            [BrowseViewModel.ChromeCloseId] = ProgressClose,
            [BrowseViewModel.ChromeAddressId] = ProgressAddress,
        };

        IsClickThrough = false;

        // Work-area sized, not full-screen: the browser is a window, not an overlay takeover —
        // the taskbar stays reachable and the chrome row reads as a browser's own.
        Width = SystemParameters.WorkArea.Width;
        Height = SystemParameters.WorkArea.Height;
        Left = SystemParameters.WorkArea.Left;
        Top = SystemParameters.WorkArea.Top;

        AddressBox.KeyDown += OnAddressKeyDown;
        Closing += (_, _) => RaiseSurfaceClosed();

        // Review M6 + r2 M11: the chrome rects and the web bounds are geometry read once per
        // summon — a work-area, DPI or display change would leave them stale until close and
        // re-summon. Every geometry move re-raises; the coordinator re-reads. A same-monitor
        // DPI change keeps the DIP size, so neither SizeChanged nor LocationChanged fires.
        SizeChanged += (_, _) => RaiseLayoutChanged();
        LocationChanged += (_, _) => RaiseLayoutChanged();
        DpiChanged += (_, _) => RaiseLayoutChanged();
    }

    /// <summary>Test seam (InternalsVisibleTo): the chrome map itself, so the headless suite can
    /// pin the id→element PAIRING (review M9 — the id list alone cannot catch a remapped
    /// chrome_address).</summary>
    internal IReadOnlyDictionary<string, System.Windows.FrameworkElement> ChromeButtonsForTest => _chromeButtons;

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="vm"/> names the surface whose dwell targets the coordinator registers
    /// against this window's reported geometry; the window itself reaches the VM through
    /// nothing (it reports geometry and draws its own chrome cue), so no reference is kept.
    /// </remarks>
    public void Show(BrowseViewModel vm)
    {
        EnsureWebHost();

        // Deliberately Show() BEFORE the focusable flip — see the class doc (the calibration
        // precedent: an earlier call no-ops on the first run, and OverlayWindow's
        // OnSourceInitialized has already applied focusable:false by the time this returns).
        Show();
        SetOverlayInteraction(clickThrough: false, focusable: true);
        Activate();
    }

    void IBrowseWindowSurface.Close() => Close();

    /// <inheritdoc />
    public void FocusAddressBox()
    {
        // Review r2 I5: WPF focus in an inactive window is logical only — the gaze keyboard
        // types into the OS FOREGROUND window, so the browser must hold it. And review r2 I2:
        // SelectAll runs only when the field was not already keyboard-focused — a re-dwell of
        // the address box must preserve the typed text and the caret, or the next key wipes
        // everything the owner typed.
        SetOverlayInteraction(clickThrough: false, focusable: true);
        Activate();
        if (!AddressBox.IsKeyboardFocused)
        {
            AddressBox.Focus();
            AddressBox.SelectAll();
        }
    }

    /// <inheritdoc />
    public void ActivateToFront()
    {
        SetOverlayInteraction(clickThrough: false, focusable: true);
        Activate();
        RaiseToTop();
    }

    /// <inheritdoc />
    public void InsetForDockedToolbar(double left, double top, double right, double bottom)
    {
        // The ctor sized the window to the work area; the docked toolbar's band is reserved
        // out of it on that edge (r2 I1) — persistent, so the chrome row never jumps when the
        // auto-hidden toolbar slides in.
        Left += left;
        Top += top;
        Width -= left + right;
        Height -= top + bottom;
    }

    /// <inheritdoc />
    public bool TryGetWebOrigin(out PhysicalPoint origin)
    {
        origin = default;
        if (WebHostHost.ActualWidth <= 0 || WebHostHost.ActualHeight <= 0)
            return false;
        try
        {
            var topLeft = WebHostHost.PointToScreen(new Point(0, 0));
            origin = new PhysicalPoint(topLeft.X, topLeft.Y);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public bool TryGetWebBounds(out PhysicalRect bounds) =>
        TryGetScreenRect(WebHostHost, out bounds);

    /// <inheritdoc />
    public bool TryGetWindowBounds(out PhysicalRect bounds)
    {
        // The window itself: same two-corner pattern, on the window's own DIP extents.
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            bounds = default;
            return false;
        }

        try
        {
            var topLeft = PointToScreen(new Point(0, 0));
            var bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
            bounds = new PhysicalRect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
            return true;
        }
        catch (InvalidOperationException)
        {
            bounds = default;
            return false;
        }
    }

    /// <inheritdoc />
    public bool IsUnderPoint(PhysicalPoint screenPoint)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return false;

        var raw = WindowFromPoint(new NativePoint
        {
            X = (int)Math.Round(screenPoint.X, MidpointRounding.AwayFromZero),
            Y = (int)Math.Round(screenPoint.Y, MidpointRounding.AwayFromZero),
        });
        if (raw == IntPtr.Zero)
            return false;

        // The web area lives on the WebView2 control's own child HWND — resolve to the
        // top-level window before comparing (the input-integrity probe's pattern).
        var root = GetAncestor(raw, GaRoot);
        return root == hwnd;
    }

    private void RaiseLayoutChanged() => LayoutChanged?.Invoke();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    private const uint GaRoot = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>Test seam (InternalsVisibleTo): the chrome map's ids, so the headless suite can
    /// pin all six without a shown (measured) window.</summary>
    internal IReadOnlyList<string> ChromeIdsForTest => _chromeButtons.Keys.ToList();

    /// <summary>Test seam (InternalsVisibleTo): raises <see cref="LayoutChanged"/>, so the
    /// coordinator's geometry re-read is assertable without physically resizing a window.</summary>
    internal void RaiseLayoutChangedForTest() => RaiseLayoutChanged();

    /// <summary>
    /// The chrome row's dwell cue, drawn WPF-side (the page's JS cue cannot know the
    /// <c>chrome_</c> ids): the matching element's progress strip widens to
    /// <c>progress × element width</c>; a cleared or unknown id hides every strip.
    /// </summary>
    public void ShowChromeProgress(string? id, double progress)
    {
        foreach (var strip in _chromeProgress.Values)
            strip.Visibility = Visibility.Collapsed;

        if (id is null || progress <= 0 || !_chromeProgress.TryGetValue(id, out var target))
            return;
        if (!_chromeButtons.TryGetValue(id, out var element) || element.ActualWidth <= 0)
            return;

        target.Width = element.ActualWidth * Math.Clamp(progress, 0, 1);
        target.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// The chrome row's dwell rects, in the VM's chrome-id namespace — the coordinator feeds
    /// them to <see cref="BrowseViewModel.SetChromeTargets"/> once per show. False before the
    /// window is laid out.
    /// </summary>
    public bool TryGetChromeRects(out IReadOnlyList<(string Id, PhysicalRect Rect)> rects)
    {
        var list = new List<(string, PhysicalRect)>(_chromeButtons.Count);
        foreach (var (id, button) in _chromeButtons)
        {
            if (TryGetScreenRect(button, out var rect))
                list.Add((id, rect));
        }

        rects = list;
        return list.Count == _chromeButtons.Count;
    }

    private static bool TryGetScreenRect(FrameworkElement element, out PhysicalRect rect)
    {
        rect = default;
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return false;
        try
        {
            // Two corners: the DIP extents are the element's own (arguments), never added to a
            // PointToScreen result — the pattern the keyboard window pins.
            var topLeft = element.PointToScreen(new Point(0, 0));
            var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
            rect = new PhysicalRect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void ShowAddressError(string message)
    {
        // The failure notice rides the address bar: the one text the user already knows how to
        // read, and the field a retry would type into.
        AddressBox.Text = message;
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void EnsureWebHost()
    {
        if (_webHost is not null)
            return;
        _webHost = new WebView2BrowseHost();
        WebHostHost.Child = _webHost.Control;
    }

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        TryNavigate(AddressBox.Text);
    }

    /// <summary>
    /// The address box's omnibox contract (owner feedback 2026-11-05: it "shall be able to
    /// search"): an explicit http(s) URL navigates; a bare host that parses as one
    /// (<c>example.com</c>, <c>example.com/path</c>, an IPv4 literal) gets the https scheme and
    /// navigates; EVERYTHING else is a web search — spaces or a dotless word are a query, not a
    /// broken URL, so typing must never dead-end the way the old malformed-URL rejection did.
    /// Returns whether a navigation was requested. Internal test seam — the headless tests drive
    /// the same normalization the Enter handler runs.
    /// </summary>
    internal bool TryNavigate(string candidate)
    {
        candidate = candidate.Trim();
        if (candidate.Length == 0)
            return false;

        if (candidate.Contains("://", StringComparison.Ordinal))
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                NavigateRequested?.Invoke(uri);
                return true;
            }

            return SearchFor(candidate);
        }

        // A single token that parses as a DNS host (dot required — "wikipedia" alone is a query,
        // not a TLD-less host) or a CANONICAL IPv4 dotted quad is an address; everything else
        // is a search. Review r2 M1: .NET parses inet_aton shorthand, so bare numbers
        // ("42" → 0.0.0.42), decimals ("3.14" → 3.0.0.14) and hex ("0x7f.1") must search, and
        // userinfo ("user@example.com") must not smuggle a host through.
        if (!candidate.Contains(' ')
            && Uri.TryCreate("https://" + candidate, UriKind.Absolute, out var hostUri)
            && hostUri.UserInfo.Length == 0)
        {
            // The host part of the candidate ("example.com" out of "example.com/path"), with a
            // trailing dot tolerated by Uri but not by a user's intent.
            var hostPart = candidate.Split('/', '?')[0].TrimEnd('.');

            // r2 M1: .NET parses inet_aton shorthand, so an IPv4 literal counts only when the
            // candidate IS the canonical dotted quad the Uri reports back.
            var isCanonicalIpv4 = hostUri.HostNameType is UriHostNameType.IPv4
                && System.Text.RegularExpressions.Regex.IsMatch(
                    hostPart, @"^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$")
                && string.Equals(hostUri.Host, hostPart, StringComparison.OrdinalIgnoreCase);

            // A DNS host navigates when the candidate names exactly that host and its final
            // label is alphabetic — years, versions and bare numbers are queries (r2 M1).
            var isPlausibleDns = hostUri.HostNameType is UriHostNameType.Dns
                && hostPart.Contains('.')
                && string.Equals(hostUri.Host, hostPart, StringComparison.OrdinalIgnoreCase)
                && HasAlphabeticFinalLabel(hostUri.Host);

            if (isCanonicalIpv4 || isPlausibleDns)
            {
                NavigateRequested?.Invoke(hostUri);
                return true;
            }
        }

        return SearchFor(candidate);
    }

    /// <summary>The last DNS label must carry an alphabetic character: "3.14" parses as an IPv4
    /// shorthand (rejected above), "v1.2" ends in a digit (search), and a TLD is alphabetic by
    /// definition — this keeps years, versions and numbers in the search arm (review r2 M1).</summary>
    private static bool HasAlphabeticFinalLabel(string host)
    {
        var lastLabel = host[(host.LastIndexOf('.') + 1)..];
        return lastLabel.Length >= 2 && lastLabel.Any(char.IsLetter);
    }

    /// <summary>
    /// The search arm of the omnibox. Bing is the Windows/Edge default and needs no account
    /// wall; the engine is a one-line change here if the owner wants another, and a settings
    /// row is deferred with the browser's other settings work.
    /// </summary>
    private bool SearchFor(string query)
    {
        NavigateRequested?.Invoke(new Uri("https://www.bing.com/search?q=" + Uri.EscapeDataString(query)));
        return true;
    }

    private void RaiseSurfaceClosed()
    {
        if (_surfaceClosedRaised)
            return;
        _surfaceClosedRaised = true;
        SurfaceClosed?.Invoke();
    }
}
