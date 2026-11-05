namespace EyeCtrl.Core.Browse;

using System.Text.Json;
using EyeCtrl.Core.Models;

/// <summary>
/// Parses the page→app elements envelope the injected browse script posts through
/// <c>window.chrome.webview.postMessage</c>: <c>{type:"elements", gen, dpr,
/// els:[{i,k,r:[x,y,w,h]}]}</c>. The <c>r</c> rects are CSS-pixel viewport rects; the
/// runtime <c>window.devicePixelRatio</c> rides in the payload and the web view's
/// on-screen origin is injected by the caller, so no DPI value is ever baked in here —
/// each snapshot is self-describing and the 200 % display simply arrives as dpr 2.
/// <para>Rejection split: a malformed JSON document or a broken envelope (wrong/missing
/// <c>type</c>, missing or non-integral <c>gen</c>, missing or non-positive <c>dpr</c>,
/// missing <c>els</c>) fails the whole parse (<see cref="TryParse"/> returns false);
/// a broken element (missing/oversized id, non-numeric or non-finite rect values,
/// wrong rect arity) is dropped and the remaining elements survive. Every accepted
/// rect is clamped to the same ±100 000 px band as the external-dwell bridge
/// (ExternalDwellService.ToRect), so a hostile or buggy page can never push dwell
/// registration or click mapping out of bounds. Unknown property fields are ignored;
/// an unknown <c>k</c> string maps to <see cref="WebElementKind.Other"/>. The bridge is
/// content-free: ids, kinds and rects only — element text or labels must never be
/// added to the envelope.</para>
/// </summary>
public static class WebElementMessageParser
{
    /// <summary>The envelope <c>type</c> this parser accepts; other message types (posted
    /// by later revisions of the bridge script) fail the parse rather than throw.</summary>
    public const string MessageType = "elements";

    /// <summary>Ids longer than this are page bugs (or attacks), not element identity;
    /// such elements are dropped.</summary>
    public const int MaxIdLength = 256;

    /// <summary>The rect clamp band, mirroring ExternalDwellService.ToRect: far beyond any
    /// physical screen, so layout math downstream stays bounded.</summary>
    private const double RectLimit = 100_000;

    /// <summary>
    /// Parses one elements envelope. On false, <paramref name="snapshot"/> is
    /// <see cref="WebElementSnapshot.Empty"/> — never null, never partially filled.
    /// </summary>
    /// <param name="json">The raw message body posted by the page.</param>
    /// <param name="originPhysical">The web view's top-left corner in physical screen
    /// pixels (queried at runtime from the window); CSS coordinates are offset by it.</param>
    /// <param name="snapshot">The parsed snapshot, or <see cref="WebElementSnapshot.Empty"/>.</param>
    /// <returns>true when the envelope was an elements message with a valid shape.</returns>
    public static bool TryParse(string? json, PhysicalPoint originPhysical, out WebElementSnapshot snapshot)
    {
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (TryParseDocument(document.RootElement, originPhysical, out snapshot))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Malformed JSON (including NaN/Infinity literals, which are not valid JSON):
                // fall through to the empty snapshot.
            }
        }

        snapshot = WebElementSnapshot.Empty;
        return false;
    }

    private static bool TryParseDocument(JsonElement root, PhysicalPoint originPhysical, out WebElementSnapshot snapshot)
    {
        snapshot = WebElementSnapshot.Empty;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(MessageFields.Type, out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String
            || typeElement.GetString() != MessageType
            || !root.TryGetProperty(MessageFields.Generation, out var genElement)
            || genElement.ValueKind != JsonValueKind.Number
            || !TryReadNonNegativeInt32(genElement, out var generation)
            || !root.TryGetProperty(MessageFields.DevicePixelRatio, out var dprElement)
            || dprElement.ValueKind != JsonValueKind.Number
            || !TryReadPositiveDouble(dprElement, out var dpr)
            || !root.TryGetProperty(MessageFields.Elements, out var elsElement)
            || elsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var elements = new List<WebElementInfo>();
        foreach (var item in elsElement.EnumerateArray())
        {
            if (TryReadElement(item, originPhysical, dpr, out var element))
            {
                elements.Add(element);
            }
        }

        snapshot = new WebElementSnapshot(generation, elements);
        return true;
    }

    private static bool TryReadElement(JsonElement item, PhysicalPoint originPhysical, double dpr, out WebElementInfo element)
    {
        element = new WebElementInfo(string.Empty, WebElementKind.Other, default);
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty(ElementFields.Id, out var idElement)
            || idElement.ValueKind != JsonValueKind.String
            || idElement.GetString() is not { Length: > 0 } rawId
            || rawId.Length > MaxIdLength)
        {
            return false;
        }

        // Kind is a refinement, not identity: a missing or unrecognised k still yields an
        // actionable element classified as Other.
        var kind = WebElementKind.Other;
        if (item.TryGetProperty(ElementFields.Kind, out var kindElement)
            && kindElement.ValueKind == JsonValueKind.String)
        {
            kind = ParseKind(kindElement.GetString());
        }

        if (!item.TryGetProperty(ElementFields.Rect, out var rectElement)
            || rectElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var values = new double[4];
        var index = 0;
        foreach (var value in rectElement.EnumerateArray())
        {
            if (index >= values.Length || value.ValueKind != JsonValueKind.Number || !TryReadFiniteDouble(value, out var number))
            {
                return false;
            }

            values[index++] = number;
        }

        if (index != values.Length)
        {
            return false;
        }

        // CSS → physical: payload dpr × CSS rect, offset by the web view's screen origin.
        // The IsFinite guards keep a hostile magnitude pair (huge CSS × huge dpr) from
        // overflowing into an infinite coordinate that the clamp below would silently keep.
        double x = (values[0] * dpr) + originPhysical.X;
        double y = (values[1] * dpr) + originPhysical.Y;
        double width = values[2] * dpr;
        double height = values[3] * dpr;
        if (!double.IsFinite(x) || !double.IsFinite(y)
            || !double.IsFinite(width) || !double.IsFinite(height))
        {
            return false;
        }

        element = new WebElementInfo(
            NormalizeId(rawId),
            kind,
            new PhysicalRect(
                Math.Clamp(x, -RectLimit, RectLimit),
                Math.Clamp(y, -RectLimit, RectLimit),
                Math.Clamp(width, 0, RectLimit),
                Math.Clamp(height, 0, RectLimit)));
        return true;
    }

    /// <summary>Namespaces the page's element id into the browse registry's id domain so
    /// browse targets can never collide with another surface's ids in the shared dwell
    /// detector. Already-prefixed ids pass through unchanged.</summary>
    private static string NormalizeId(string rawId)
        => rawId.StartsWith(WebElementInfo.IdPrefix, StringComparison.Ordinal) ? rawId : WebElementInfo.IdPrefix + rawId;

    private static WebElementKind ParseKind(string? kind) => kind?.ToLowerInvariant() switch
    {
        "link" => WebElementKind.Link,
        "button" => WebElementKind.Button,
        "input" => WebElementKind.Input,
        "select" => WebElementKind.Select,
        "textarea" => WebElementKind.TextArea,
        _ => WebElementKind.Other,
    };

    private static bool TryReadNonNegativeInt32(JsonElement element, out int value)
    {
        // Accepts integral forms (3 and 3.0 alike) and rejects fractions and out-of-range values.
        if (element.TryGetInt32(out value))
        {
            return value >= 0;
        }

        if (!TryReadFiniteDouble(element, out var doubleValue)
            || doubleValue < 0
            || doubleValue > int.MaxValue
            || doubleValue != Math.Floor(doubleValue))
        {
            return false;
        }

        value = (int)doubleValue;
        return true;
    }

    private static bool TryReadPositiveDouble(JsonElement element, out double value)
        => TryReadFiniteDouble(element, out value) && value > 0;

    private static bool TryReadFiniteDouble(JsonElement element, out double value)
    {
        // An out-of-range magnitude (1e400) throws rather than yielding Infinity on some
        // runtimes; either failure mode is a rejected element, never a crash.
        try
        {
            value = element.GetDouble();
        }
        catch (FormatException)
        {
            value = 0;
            return false;
        }

        return double.IsFinite(value);
    }

    /// <summary>Envelope property names, kept as constants so the parser and the bridge
    /// script's contract read side by side.</summary>
    private static class MessageFields
    {
        public const string Type = "type";
        public const string Generation = "gen";
        public const string DevicePixelRatio = "dpr";
        public const string Elements = "els";
    }

    /// <summary>Element property names (see <see cref="MessageFields"/>).</summary>
    private static class ElementFields
    {
        public const string Id = "i";
        public const string Kind = "k";
        public const string Rect = "r";
    }
}
