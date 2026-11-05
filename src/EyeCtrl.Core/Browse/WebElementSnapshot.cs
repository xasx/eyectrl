namespace EyeCtrl.Core.Browse;

using EyeCtrl.Core.Models;

/// <summary>The coarse class of an interactive element enumerated by the browse bridge.
/// Content-free by contract (the KB-E1 privacy standard applies to the browse bridge too):
/// the kind is a closed id — no element text, label, URL or attribute ever crosses the
/// bridge, because a text plus the layout would spell the page's content.</summary>
public enum WebElementKind
{
    /// <summary>A link (<c>a href</c>).</summary>
    Link = 0,
    /// <summary>A button or button-like element.</summary>
    Button,
    /// <summary>A text input field.</summary>
    Input,
    /// <summary>A dropdown selector.</summary>
    Select,
    /// <summary>A multi-line text area.</summary>
    TextArea,
    /// <summary>Anything else interactive (or an unrecognised kind string).</summary>
    Other,
}

/// <summary>One interactive element of the browsed page, positioned in physical screen
/// pixels. <see cref="Id"/> lives in the <see cref="IdPrefix"/> namespace so browse targets
/// can never collide with another surface's ids in the shared dwell registry.</summary>
/// <param name="Id">The bridge-stable element id, prefixed <see cref="IdPrefix"/>.</param>
/// <param name="Kind">The element's coarse class.</param>
/// <param name="RectPhysical">The element's viewport rect converted to physical screen
/// pixels (CSS rect × the page's <c>devicePixelRatio</c> plus the web view's on-screen
/// origin — both queried at runtime, never hardcoded; a 200 % display simply arrives as
/// dpr 2 in the payload).</param>
public sealed record WebElementInfo(string Id, WebElementKind Kind, PhysicalRect RectPhysical)
{
    /// <summary>The namespace every browse element id carries (assigned by
    /// <see cref="WebElementMessageParser"/> when a page message is parsed).</summary>
    public const string IdPrefix = "web_";
}

/// <summary>One page→app elements report: the page's interactive elements as of a
/// generation counter. Immutable; produced only by <see cref="WebElementMessageParser"/>.</summary>
/// <param name="Generation">Monotonic per-page-report counter from the payload, for
/// ordering and staleness checks by the consumer.</param>
/// <param name="Elements">The enumerated elements; may be empty (a page with nothing
/// interactive, or every element rejected as malformed).</param>
public sealed record WebElementSnapshot(int Generation, IReadOnlyList<WebElementInfo> Elements)
{
    /// <summary>A snapshot with no elements (generation 0); the out value of a failed
    /// parse and the empty-next input of a reconcile plan.</summary>
    public static WebElementSnapshot Empty { get; } = new(0, Array.Empty<WebElementInfo>());
}
