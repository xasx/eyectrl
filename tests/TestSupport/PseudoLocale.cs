namespace EyeCtrl.Tests.TestSupport;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;

/// <summary>
/// T09 PR 2's pseudo-locale: the embedded English table, transformed at TEST time into text that
/// behaves like a long translation. It is never shipped and never written to disk beside the App.
///
/// <para>
/// <b>The transform, per text.</b> Every word grows by 40 % (rounded up) — the word's own letters
/// repeated after it, so a run that cannot break grows too, as German compounds do. Every letter
/// is swapped for an accented look-alike (<c>a→á</c>, <c>e→ë</c>, <c>C→Ç</c> …), so a font that
/// lacks a glyph or a measurement that ignores diacritics' height shows up. The whole text is
/// wrapped in <c>[…]</c>, so a truncated or clipped string is visible at either end. Placeholders
/// (<c>{0}</c>, <c>{1:F1}</c>) and escaped braces are kept byte for byte, so the result formats
/// exactly as the English does; plural <c>{one, other}</c> forms are transformed form by form.
/// </para>
///
/// <para>
/// <b>Why words, not padding.</b> A suffix such as <c>~~~~</c> makes a text longer but never makes
/// a run longer, so the class-B rule ("the longest run fits a line") would never be exercised.
/// Lengthening each word is what German does to a layout.
/// </para>
/// </summary>
internal static class PseudoLocale
{
    /// <summary>How much longer each word becomes.</summary>
    internal const double Expansion = 0.4;

    private static readonly Dictionary<char, char> Accents = new()
    {
        ['a'] = 'á', ['b'] = 'ƀ', ['c'] = 'ç', ['d'] = 'đ', ['e'] = 'ë', ['f'] = 'ƒ', ['g'] = 'ğ',
        ['h'] = 'ĥ', ['i'] = 'í', ['j'] = 'ĵ', ['k'] = 'ķ', ['l'] = 'ļ', ['m'] = 'ɱ', ['n'] = 'ñ',
        ['o'] = 'ö', ['p'] = 'þ', ['q'] = 'ʠ', ['r'] = 'ŕ', ['s'] = 'š', ['t'] = 'ţ', ['u'] = 'ü',
        ['v'] = 'ṽ', ['w'] = 'ŵ', ['x'] = 'ẋ', ['y'] = 'ý', ['z'] = 'ž',
        ['A'] = 'Å', ['B'] = 'Ɓ', ['C'] = 'Ç', ['D'] = 'Đ', ['E'] = 'Ë', ['F'] = 'Ƒ', ['G'] = 'Ğ',
        ['H'] = 'Ĥ', ['I'] = 'Í', ['J'] = 'Ĵ', ['K'] = 'Ķ', ['L'] = 'Ļ', ['M'] = 'Ṁ', ['N'] = 'Ñ',
        ['O'] = 'Ö', ['P'] = 'Þ', ['Q'] = 'Ǫ', ['R'] = 'Ŕ', ['S'] = 'Š', ['T'] = 'Ţ', ['U'] = 'Ü',
        ['V'] = 'Ṽ', ['W'] = 'Ŵ', ['X'] = 'Ẋ', ['Y'] = 'Ý', ['Z'] = 'Ž',
    };

    private static readonly Lazy<string> PseudoJson = new(BuildJson);

    private static readonly Lazy<StringTable> PseudoTable = new(() =>
        StringTable.Create(
            UiLanguage.English,
            CultureInfo.InvariantCulture,
            StringTableContent.ParseEnglish(PseudoJson.Value)));

    /// <summary>
    /// The pseudo-locale as a table: English selection, so every key resolves the transformed
    /// "English" with the invariant provider. Pass it to a window that takes a table, or activate it
    /// (in a test that owns the global, under the "StringTable Tests" collection).
    /// </summary>
    internal static StringTable Table => PseudoTable.Value;

    /// <summary>The pseudo-locale's strings.json, every entry's metadata unchanged.</summary>
    internal static string Json => PseudoJson.Value;

    /// <summary>Transforms one text: longer words, accented letters, bracketed; placeholders kept.</summary>
    internal static string Transform(string text)
    {
        var output = new StringBuilder(text.Length * 2);
        output.Append('[');
        var word = new StringBuilder();

        void FlushWord()
        {
            if (word.Length == 0) return;
            var letters = word.ToString();
            var extra = (int)Math.Ceiling(letters.Length * Expansion);
            foreach (var c in letters) output.Append(Accent(c));
            for (var i = 0; i < extra; i++) output.Append(Accent(char.ToLowerInvariant(letters[i % letters.Length])));
            word.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{' || c == '}')
            {
                FlushWord();
                if (i + 1 < text.Length && text[i + 1] == c)
                {
                    output.Append(c).Append(c);    // an escaped brace
                    i++;
                    continue;
                }

                if (c == '{')
                {
                    var close = text.IndexOf('}', i);
                    output.Append(text, i, close - i + 1);   // a placeholder, verbatim
                    i = close;
                    continue;
                }

                output.Append(c);
                continue;
            }

            if (char.IsLetter(c))
            {
                word.Append(c);
                continue;
            }

            FlushWord();
            output.Append(c);
        }

        FlushWord();
        output.Append(']');
        return output.ToString();
    }

    private static char Accent(char c) => Accents.TryGetValue(c, out var accented) ? accented : c;

    private static string BuildJson()
    {
        using var stream = typeof(StringTable).Assembly.GetManifestResourceStream(StringTable.EmbeddedResourceName)
            ?? throw new InvalidOperationException($"The embedded resource '{StringTable.EmbeddedResourceName}' is missing.");
        var root = JsonNode.Parse(stream)!.AsObject();
        foreach (var (key, value) in root.ToList())
        {
            if (key.StartsWith('_') || value is not JsonObject entry) continue;
            switch (entry["text"])
            {
                case JsonValue single:
                    entry["text"] = Transform(single.GetValue<string>());
                    break;
                case JsonObject forms:
                    foreach (var (form, formText) in forms.ToList())
                        forms[form] = Transform(formText!.GetValue<string>());
                    break;
            }
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The pseudo-locale's items of one fit class (the English items, text transformed).</summary>
    internal static IReadOnlyList<FitCatalog.Item> ItemsOf(string fitClass) =>
        FitCatalog.ItemsOf(fitClass).Select(i => i with { Text = Transform(i.Text) }).ToList();
}
