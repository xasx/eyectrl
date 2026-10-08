namespace EyeCtrl.Core.Localization;

using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Parsed, immutable content of one string-table file: <c>strings.json</c> (English) or
/// <c>strings.de.json</c> (German). Production resolves text through <see cref="StringTable"/>;
/// this type is public so tests and later tickets can inject tables instead of touching the
/// process-global one.
/// </summary>
/// <remarks>
/// Parsing is defensive by design: a malformed file or entry is reported through the caller's
/// <c>warn</c> delegate and skipped, never thrown. A string table must not be able to fail a
/// start — the English table is what the startup-error box itself renders from.
/// </remarks>
public sealed class StringTableContent
{
    private static readonly StringTableContent EmptyContent = new(
        new Dictionary<string, StringTableText>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal));

    private readonly Dictionary<string, StringTableText> _texts;
    private readonly Dictionary<string, string> _surfacesByKey;

    private StringTableContent(
        Dictionary<string, StringTableText> texts,
        Dictionary<string, string> surfacesByKey)
    {
        _texts = texts;
        _surfacesByKey = surfacesByKey;
    }

    /// <summary>An empty table — the shipped German file, and a neutral for tests.</summary>
    public static StringTableContent Empty => EmptyContent;

    /// <summary>The number of keys in the content.</summary>
    public int Count => _texts.Count;

    /// <summary>Whether <paramref name="key"/> carries a value in this content.</summary>
    /// <param name="key">The key to look up.</param>
    /// <returns>Whether the key is present.</returns>
    public bool ContainsKey(string key) => _texts.ContainsKey(key);

    internal IReadOnlyDictionary<string, StringTableText> Texts => _texts;

    /// <summary>Key → <c>where</c> surface id. Populated for English content only.</summary>
    internal IReadOnlyDictionary<string, string> SurfacesByKey => _surfacesByKey;

    /// <summary>
    /// Parses English table JSON: <c>key → {text, who, where, fit, args, …}</c>, where
    /// <c>text</c> is a string or <c>{one, other}</c>. Entries without a valid <c>text</c> or
    /// <c>where</c> are skipped with a warning; <c>_</c>-prefixed keys are reserved meta notes
    /// and skipped silently.
    /// </summary>
    /// <param name="json">The file's contents.</param>
    /// <param name="warn">Receives human-readable warning lines; null discards them.</param>
    /// <returns>The parsed content, empty on a whole-file failure.</returns>
    public static StringTableContent ParseEnglish(string json, Action<string>? warn = null)
    {
        if (!TryParseRoot(json, warn, out var root)) return Empty;

        var texts = new Dictionary<string, StringTableText>(StringComparer.Ordinal);
        var surfaces = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, node) in root)
        {
            if (IsReservedMetaKey(key)) continue;

            if (node is not JsonObject entry)
            {
                warn?.Invoke($"String table entry '{key}' is not an object; skipped.");
                continue;
            }

            var text = ParseText(entry["text"]);
            if (text is null)
            {
                warn?.Invoke($"String table entry '{key}' has no valid 'text'; skipped.");
                continue;
            }

            if (!TryGetString(entry["where"], out var where))
            {
                warn?.Invoke($"String table entry '{key}' has no valid 'where' surface id; skipped.");
                continue;
            }

            texts[key] = text;
            surfaces[key] = where;
        }

        return new StringTableContent(texts, surfaces);
    }

    /// <summary>
    /// Parses German table JSON: <c>key → {text, src}</c>, where <c>text</c> is a string or
    /// <c>{one, other}</c>. Entries without a valid <c>text</c> are skipped with a warning;
    /// <c>_</c>-prefixed keys are reserved meta notes and skipped silently.
    /// </summary>
    /// <param name="json">The file's contents.</param>
    /// <param name="warn">Receives human-readable warning lines; null discards them.</param>
    /// <returns>The parsed content, empty on a whole-file failure.</returns>
    public static StringTableContent ParseGerman(string json, Action<string>? warn = null)
    {
        if (!TryParseRoot(json, warn, out var root)) return Empty;

        var texts = new Dictionary<string, StringTableText>(StringComparer.Ordinal);
        foreach (var (key, node) in root)
        {
            if (IsReservedMetaKey(key)) continue;

            if (node is not JsonObject entry)
            {
                warn?.Invoke($"German string entry '{key}' is not an object; skipped.");
                continue;
            }

            var text = ParseText(entry["text"]);
            if (text is null)
            {
                warn?.Invoke($"German string entry '{key}' has no valid 'text'; skipped.");
                continue;
            }

            texts[key] = text;
        }

        return new StringTableContent(texts, new Dictionary<string, string>(StringComparer.Ordinal));
    }

    /// <summary>
    /// Whether a key is a reserved meta note. Underscore-prefixed keys carry file notes for the
    /// table's authors (the settled plural rule, for example) and are never resolved.
    /// </summary>
    private static bool IsReservedMetaKey(string key) => key.StartsWith('_');

    private static bool TryParseRoot(string json, Action<string>? warn, out JsonObject root)
    {
        try
        {
            if (JsonNode.Parse(json) is JsonObject obj)
            {
                root = obj;
                return true;
            }
            warn?.Invoke("String table root is not a JSON object.");
        }
        catch (JsonException ex)
        {
            warn?.Invoke($"String table is not valid JSON: {ex.Message}");
        }

        root = new JsonObject();
        return false;
    }

    private static StringTableText? ParseText(JsonNode? node)
    {
        if (TryGetString(node, out var single)) return StringTableText.Singular(single);

        if (node is JsonObject forms
            && TryGetString(forms["one"], out var one)
            && TryGetString(forms["other"], out var other))
        {
            return StringTableText.Plural(one, other);
        }

        return null;
    }

    private static bool TryGetString(JsonNode? node, out string value)
    {
        if (node is JsonValue jsonValue
            && jsonValue.TryGetValue(out string? text)
            && !string.IsNullOrWhiteSpace(text))
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
