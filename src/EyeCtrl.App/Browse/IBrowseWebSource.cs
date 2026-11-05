namespace EyeCtrl.App.Browse;

using System;

/// <summary>
/// The one WebView2 seam the rest of the app sees. Everything above this interface —
/// coordinator, tests — stays free of the Microsoft.Web.WebView2 assembly; the concrete
/// <see cref="WebView2BrowseHost"/> is the only type that touches it, and the production
/// host is never constructed headlessly (the window creates it lazily on first Show).
/// <para>
/// Events arrive on the thread the host runs on (the UI thread for the WPF control's async
/// continuations); subscribers marshal if they must. The element envelope's contract is
/// Core's (<c>EyeCtrl.Core.Browse.WebElementMessageParser</c>); the dwell-cue message posted
/// INTO the page is <c>{"type":"dwell","id":...,"p":...}</c>.
/// </para>
/// </summary>
public interface IBrowseWebSource : IDisposable
{
    /// <summary>The raw page→app envelope JSON the bridge script posted (T1's parser consumes
    /// it). The host authenticates the sender first (external review C3c): only the current
    /// top-level document's messages reach this event, and the envelope must still carry the
    /// <see cref="BridgeSecret"/> for the parser to accept it.</summary>
    public event Action<string>? ElementMessageReceived;

    /// <summary>The main frame requested a navigation — before any of the new document's
    /// content loads (external review I1): the coordinator clears the page dwell targets here,
    /// ahead of the new document's bridge script, so a static page's first snapshot survives.</summary>
    public event Action? NavigationStarting;

    /// <summary>A top-level navigation settled. <paramref name="success"/> mirrors the event's
    /// IsSuccess. Nothing is cleared here any more (review I1 — clearing after the new page's
    /// first snapshot wiped it); the coordinator only re-reads its chrome geometry.</summary>
    public event Action<string, bool>? NavigationCompleted;

    /// <summary>The per-summon bridge secret this source's injected script bakes into every
    /// envelope (external review C3c). The coordinator passes it to the parser; a page that
    /// forges an envelope cannot know it. Generated in the constructor, so it exists before
    /// the runtime does.</summary>
    public string BridgeSecret { get; }

    /// <summary>Whether Back is possible changed (history-driven).</summary>
    public event Action<bool>? BackPossibilityChanged;

    /// <summary>Whether Forward is possible changed (history changed; the coordinator decides
    /// what to show on the chrome affordance).</summary>
    public event Action<bool>? ForwardPossibilityChanged;

    /// <summary>The CoreWebView2 came up and the bridge script is installed.</summary>
    public event Action? Ready;

    /// <summary>The web source is unusable — a missing Evergreen runtime, a failed environment
    /// creation or a failed native-loader load. The reason is for the user-facing notice.</summary>
    public event Action<string>? Failed;

    /// <summary>Navigates to <paramref name="uri"/> (initializing the runtime on first use).</summary>
    public void Navigate(Uri uri);

    /// <summary>Navigates to an HTML string (initializing the runtime on first use).</summary>
    public void NavigateToString(string html);

    /// <summary>History back; a no-op before the runtime is ready.</summary>
    public void GoBack();

    /// <summary>History forward; a no-op before the runtime is ready.</summary>
    public void GoForward();

    /// <summary>Reloads the page; a no-op before the runtime is ready.</summary>
    public void Reload();

    /// <summary>Posts JSON into the page (the JS cue listener consumes
    /// <c>{"type":"dwell","id","p"}</c>); a no-op before the runtime is ready.</summary>
    public void PostToPage(string json);
}
