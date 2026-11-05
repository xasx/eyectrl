namespace EyeCtrl.Core.Tests.Browse;

using EyeCtrl.Core.Browse;
using EyeCtrl.Core.Models;

/// <summary>
/// Contract of the page→app elements envelope (see WebElementMessageParser): valid
/// payloads parse with the payload's dpr and the injected origin (no DPI is ever baked
/// in); extra fields are ignored; broken envelopes fail the whole parse; broken
/// individual elements are dropped while the rest survive; hostile magnitudes are
/// clamped into the same band as the external-dwell bridge.
/// </summary>
public class WebElementMessageParserTests
{
    private static readonly PhysicalPoint Origin = new(100, 50);

    // ---- helpers -------------------------------------------------------------

    private static string Envelope(string els, string type = "elements", string gen = "1", string dpr = "1")
        => $$"""{"type":"{{type}}","gen":{{gen}},"dpr":{{dpr}},"els":{{els}}}""";

    private static string Element(string id, string kind, string rect)
        => $$"""{"i":"{{id}}","k":"{{kind}}","r":[{{rect}}]}""";

    private static string ArrayOf(params string[] elements) => "[" + string.Join(",", elements) + "]";

    // ---- valid envelope ------------------------------------------------------

    [Fact]
    public void Parse_ValidEnvelope_ConvertsCssRectsWithPayloadDprAndInjectedOrigin()
    {
        // dpr 2.5 (the payload's runtime devicePixelRatio — a 200 % display arrives as 2),
        // origin injected by the caller: css [10,20,30,40] → physical (125,100,75,100).
        var ok = WebElementMessageParser.TryParse(
            Envelope(ArrayOf(Element("a1", "link", "10,20,30,40")), dpr: "2.5"),
            Origin,
            out var snapshot);

        Check.That(ok).IsTrue();
        Check.That(snapshot.Generation).IsEqualTo(1);
        Check.That(snapshot.Elements).HasSize(1);
        var element = snapshot.Elements[0];
        Check.That(element.Id).IsEqualTo("web_a1");
        Check.That(element.Kind).IsEqualTo(WebElementKind.Link);
        Check.That(element.RectPhysical.X).IsCloseTo(125, 1e-9);
        Check.That(element.RectPhysical.Y).IsCloseTo(100, 1e-9);
        Check.That(element.RectPhysical.Width).IsCloseTo(75, 1e-9);
        Check.That(element.RectPhysical.Height).IsCloseTo(100, 1e-9);
    }

    [Fact]
    public void Parse_KindStrings_MapToTheWebElementKindVocabulary()
    {
        var ok = WebElementMessageParser.TryParse(
            Envelope(ArrayOf(
                Element("l", "link", "0,0,1,1"),
                Element("b", "button", "0,0,1,1"),
                Element("i", "input", "0,0,1,1"),
                Element("s", "select", "0,0,1,1"),
                Element("t", "textarea", "0,0,1,1"),
                Element("o", "other", "0,0,1,1"))),
            default,
            out var snapshot);

        Check.That(ok).IsTrue();
        Check.That(snapshot.Elements.Select(e => e.Kind)).ContainsExactly(
            WebElementKind.Link,
            WebElementKind.Button,
            WebElementKind.Input,
            WebElementKind.Select,
            WebElementKind.TextArea,
            WebElementKind.Other);
    }

    [Fact]
    public void Parse_UnknownOrWrongCasedKind_MapsToOtherOrItsCasedMatch()
    {
        var ok = WebElementMessageParser.TryParse(
            Envelope(ArrayOf(
                Element("u", "canvas", "0,0,1,1"),
                Element("c", "Link", "0,0,1,1"),
                Element("n", "TEXTAREA", "0,0,1,1"))),
            default,
            out var snapshot);

        Check.That(ok).IsTrue();
        Check.That(snapshot.Elements[0].Kind).IsEqualTo(WebElementKind.Other);
        Check.That(snapshot.Elements[1].Kind).IsEqualTo(WebElementKind.Link);
        Check.That(snapshot.Elements[2].Kind).IsEqualTo(WebElementKind.TextArea);
    }

    [Fact]
    public void Parse_MissingKind_KeepsTheElementAsOther()
    {
        // Kind is a refinement, not identity: an element without k is still actionable.
        const string json = """{"type":"elements","gen":1,"dpr":1,"els":[{"i":"a","r":[0,0,1,1]}]}""";

        var ok = WebElementMessageParser.TryParse(json, default, out var snapshot);

        Check.That(ok).IsTrue();
        Check.That(snapshot.Elements).HasSize(1);
        Check.That(snapshot.Elements[0].Kind).IsEqualTo(WebElementKind.Other);
    }

    [Fact]
    public void Parse_IdReceivesTheWebPrefix_OnlyWhenItIsMissing()
    {
        var ok = WebElementMessageParser.TryParse(
            Envelope(ArrayOf(
                Element("btn-1", "button", "0,0,1,1"),
                Element("web_btn-2", "button", "0,0,1,1"))),
            default,
            out var snapshot);

        Check.That(ok).IsTrue();
        Check.That(snapshot.Elements[0].Id).IsEqualTo(WebElementInfo.IdPrefix + "btn-1");
        Check.That(snapshot.Elements[1].Id).IsEqualTo("web_btn-2");
    }

    [Fact]
    public void Parse_ExtraFields_AtEveryLevel_AreIgnored()
    {
        const string json = """
            {"type":"elements","gen":7,"dpr":1,"els":[{"i":"x","k":"button","r":[0,0,1,1],"text":"Buy now","href":"https://x"}],"url":"https://x","title":"Shop"}
            """;

        var ok = WebElementMessageParser.TryParse(json, default, out var snapshot);

        // Content-free bridge: the extra (content-bearing) fields are simply not read.
        Check.That(ok).IsTrue();
        Check.That(snapshot.Generation).IsEqualTo(7);
        Check.That(snapshot.Elements).HasSize(1);
        Check.That(snapshot.Elements[0].Id).IsEqualTo("web_x");
    }

    [Fact]
    public void Parse_EmptyElementsArray_YieldsAnEmptySnapshotOfThatGeneration()
    {
        var ok = WebElementMessageParser.TryParse(Envelope("[]", gen: "42"), default, out var snapshot);

        Check.That(ok).IsTrue();
        Check.That(snapshot.Generation).IsEqualTo(42);
        Check.That(snapshot.Elements).IsEmpty();
    }

    // ---- element-level rejections: dropped, siblings survive ------------------

    [Fact]
    public void Parse_BrokenElements_AreDropped_WhileValidSiblingsSurvive()
    {
        var ok = WebElementMessageParser.TryParse(
            Envelope(ArrayOf(
                Element("good", "button", "1,2,3,4"),
                Element("", "button", "1,2,3,4"),                     // empty id
                """{"i":"short","k":"button","r":[1,2]}""",           // rect arity 2
                """{"i":"stringy","k":"button","r":[1,2,3,"4"]}""",   // non-numeric rect entry
                """{"i":"extra","k":"button","r":[1,2,3,4,5]}""",     // rect arity 5
                Element(new string('x', 300), "button", "1,2,3,4"))), // oversized id
            default,
            out var snapshot);

        Check.That(ok).IsTrue();
        Check.That(snapshot.Elements).HasSize(1);
        Check.That(snapshot.Elements[0].Id).IsEqualTo("web_good");
    }

    [Fact]
    public void Parse_ElementMissingItsRect_IsDropped()
    {
        const string json = """{"type":"elements","gen":1,"dpr":1,"els":[{"i":"a","k":"button"},{"i":"b","k":"button","r":[0,0,1,1]}]}""";

        var ok = WebElementMessageParser.TryParse(json, default, out var snapshot);

        Check.That(ok).IsTrue();
        Check.That(snapshot.Elements.Select(e => e.Id)).ContainsExactly("web_b");
    }

    [Fact]
    public void Parse_ElementWithNonObjectShape_IsDropped()
    {
        var ok = WebElementMessageParser.TryParse(
            Envelope(ArrayOf("\"just-a-string\"", Element("b", "button", "0,0,1,1"))),
            default,
            out var snapshot);

        Check.That(ok).IsTrue();
        Check.That(snapshot.Elements.Select(e => e.Id)).ContainsExactly("web_b");
    }

    // ---- non-finite and hostile magnitudes ------------------------------------

    [Fact]
    public void Parse_NaNAndInfinityLiterals_AreNotValidJson_AndFailTheParse()
    {
        // NaN/Infinity are not JSON tokens: they fail document parse, never surface as values.
        var nan = WebElementMessageParser.TryParse(
            Envelope(ArrayOf(Element("a", "button", "NaN,0,10,10"))), default, out var nanSnapshot);
        var infinity = WebElementMessageParser.TryParse(
            Envelope(ArrayOf(Element("a", "button", "Infinity,0,10,10"))), default, out var infSnapshot);

        Check.That(nan).IsFalse();
        Check.That(nanSnapshot).IsEqualTo(WebElementSnapshot.Empty);
        Check.That(infinity).IsFalse();
        Check.That(infSnapshot).IsEqualTo(WebElementSnapshot.Empty);
    }

    [Fact]
    public void Parse_OutOfRangeRectMagnitude_IsDropped_NotInfinite()
    {
        // 1e400 overflows every double: the element must be rejected, never carried as Infinity.
        var ok = WebElementMessageParser.TryParse(
            Envelope(ArrayOf(
                Element("fine", "button", "1,2,3,4"),
                Element("boom", "button", "1e400,2,3,4"))),
            default,
            out var snapshot);

        Check.That(ok).IsTrue();
        Check.That(snapshot.Elements.Select(e => e.Id)).ContainsExactly("web_fine");
    }

    [Fact]
    public void Parse_HugeFiniteRects_AreClampedLikeTheExternalDwellBridge()
    {
        // ExternalDwellService.ToRect's ±100 000 band: positions to [-100k, 100k], sizes to [0, 100k].
        var ok = WebElementMessageParser.TryParse(
            Envelope(ArrayOf(Element("big", "button", "500000,-300000,500000,-5")), dpr: "1"),
            default,
            out var snapshot);

        Check.That(ok).IsTrue();
        var rect = snapshot.Elements[0].RectPhysical;
        Check.That(rect.X).IsCloseTo(100_000, 1e-9);
        Check.That(rect.Y).IsCloseTo(-100_000, 1e-9);
        Check.That(rect.Width).IsCloseTo(100_000, 1e-9);
        Check.That(rect.Height).IsCloseTo(0, 1e-9);
    }

    [Fact]
    public void Parse_DprOverflowingTheConversion_DropsTheElement()
    {
        // A finite but absurd dpr must not smuggle infinite coordinates past the clamp.
        var ok = WebElementMessageParser.TryParse(
            Envelope(ArrayOf(Element("a", "button", "10,10,10,10")), dpr: "1e308"),
            default,
            out var snapshot);

        Check.That(ok).IsTrue();
        Check.That(snapshot.Elements).IsEmpty();
    }

    // ---- envelope-level rejections: the whole parse fails ----------------------

    [Fact]
    public void Parse_MissingEnvelopeField_FailsTheParse()
    {
        const string els = """[{"i":"a","k":"button","r":[0,0,1,1]}]""";
        var missingType = WebElementMessageParser.TryParse($$"""{"gen":1,"dpr":1,"els":{{els}}}""", default, out var s1);
        var missingGen = WebElementMessageParser.TryParse($$"""{"type":"elements","dpr":1,"els":{{els}}}""", default, out var s2);
        var missingDpr = WebElementMessageParser.TryParse($$"""{"type":"elements","gen":1,"els":{{els}}}""", default, out var s3);
        var missingEls = WebElementMessageParser.TryParse($$"""{"type":"elements","gen":1,"dpr":1}""", default, out var s4);

        Check.That(missingType).IsFalse();
        Check.That(s1).IsEqualTo(WebElementSnapshot.Empty);
        Check.That(missingGen).IsFalse();
        Check.That(s2).IsEqualTo(WebElementSnapshot.Empty);
        Check.That(missingDpr).IsFalse();
        Check.That(s3).IsEqualTo(WebElementSnapshot.Empty);
        Check.That(missingEls).IsFalse();
        Check.That(s4).IsEqualTo(WebElementSnapshot.Empty);
    }

    [Fact]
    public void Parse_WrongFieldType_FailsTheParse()
    {
        var wrongType = WebElementMessageParser.TryParse(
            Envelope("[]", type: "navstate"), default, out var s1);
        var stringGen = WebElementMessageParser.TryParse(
            Envelope("[]", gen: "\"1\""), default, out var s2);
        var fractionalGen = WebElementMessageParser.TryParse(
            Envelope("[]", gen: "1.5"), default, out var s3);
        var negativeGen = WebElementMessageParser.TryParse(
            Envelope("[]", gen: "-1"), default, out var s4);
        var stringDpr = WebElementMessageParser.TryParse(
            Envelope("[]", dpr: "\"1\""), default, out var s5);
        var zeroDpr = WebElementMessageParser.TryParse(
            Envelope("[]", dpr: "0"), default, out var s6);
        var negativeDpr = WebElementMessageParser.TryParse(
            Envelope("[]", dpr: "-1"), default, out var s7);
        var objectEls = WebElementMessageParser.TryParse(
            Envelope("{}"), default, out var s8);
        var rootArray = WebElementMessageParser.TryParse("[]", default, out var s9);

        Check.That(wrongType).IsFalse();
        Check.That(s1).IsEqualTo(WebElementSnapshot.Empty);
        Check.That(stringGen).IsFalse();
        Check.That(s2).IsEqualTo(WebElementSnapshot.Empty);
        Check.That(fractionalGen).IsFalse();
        Check.That(s3).IsEqualTo(WebElementSnapshot.Empty);
        Check.That(negativeGen).IsFalse();
        Check.That(s4).IsEqualTo(WebElementSnapshot.Empty);
        Check.That(stringDpr).IsFalse();
        Check.That(s5).IsEqualTo(WebElementSnapshot.Empty);
        Check.That(zeroDpr).IsFalse();
        Check.That(s6).IsEqualTo(WebElementSnapshot.Empty);
        Check.That(negativeDpr).IsFalse();
        Check.That(s7).IsEqualTo(WebElementSnapshot.Empty);
        Check.That(objectEls).IsFalse();
        Check.That(s8).IsEqualTo(WebElementSnapshot.Empty);
        Check.That(rootArray).IsFalse();
        Check.That(s9).IsEqualTo(WebElementSnapshot.Empty);
    }

    [Fact]
    public void Parse_GenAsIntegralDouble_IsAccepted()
    {
        var ok = WebElementMessageParser.TryParse(Envelope("[]", gen: "3.0"), default, out var snapshot);

        Check.That(ok).IsTrue();
        Check.That(snapshot.Generation).IsEqualTo(3);
    }

    // ---- malformed / degenerate input ------------------------------------------

    [Fact]
    public void Parse_MalformedOrEmptyJson_ReturnsFalseWithoutThrowing()
    {
        var malformed = WebElementMessageParser.TryParse("{not json", default, out var s1);
        var truncated = WebElementMessageParser.TryParse("{\"type\":\"elements\",\"els\":[{\"i\":\"a\"}", default, out var s2);
        var empty = WebElementMessageParser.TryParse(string.Empty, default, out var s3);
        var nullJson = WebElementMessageParser.TryParse(null, default, out var s4);
        var scalar = WebElementMessageParser.TryParse("42", default, out var s5);

        Check.That(malformed).IsFalse();
        Check.That(s1).IsEqualTo(WebElementSnapshot.Empty);
        Check.That(truncated).IsFalse();
        Check.That(s2).IsEqualTo(WebElementSnapshot.Empty);
        Check.That(empty).IsFalse();
        Check.That(s3).IsEqualTo(WebElementSnapshot.Empty);
        Check.That(nullJson).IsFalse();
        Check.That(s4).IsEqualTo(WebElementSnapshot.Empty);
        Check.That(scalar).IsFalse();
        Check.That(s5).IsEqualTo(WebElementSnapshot.Empty);
    }
}
