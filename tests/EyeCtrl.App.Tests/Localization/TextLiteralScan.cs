namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
/// <summary>
/// The minimal C# text lexing the source gates need, kept separate from sink knowledge so the
/// number-format gate and the literal ratchet share exactly one lexer.
///
/// <para>
/// Not a compiler. It answers two questions and nothing more: where does each string literal start
/// and end, and what are the top-level arguments of a call. Enough for this repository's shape —
/// regular, verbatim and interpolated literals, escapes, and interpolation holes (including the
/// one hole in this tree that contains a nested string, <c>$"{… ?? "<null>"}"</c>). Raw string
/// literals are not used in <c>src/</c>; the scanner reports one as a bare regular literal if that
/// ever changes, which fails the gates loudly rather than silently.
/// </para>
/// </summary>
internal static class CSharpSourceText
{
    /// <summary>One string literal, by source span.</summary>
    /// <param name="Start">Index of the first prefix character (<c>$</c>/<c>@</c>/<c>"</c>).</param>
    /// <param name="End">Index just past the closing quote.</param>
    /// <param name="Source">The literal exactly as written, prefixes and quotes included.</param>
    /// <param name="Text">
    /// The decoded value for word-bearing tests: escapes resolved, and each interpolation hole
    /// (its braces included) replaced by a single space so a hole's expression cannot look like
    /// text.
    /// </param>
    /// <param name="Interpolated">True for a <c>$"…"</c>/<c>$@"…"</c> literal.</param>
    internal sealed record Literal(int Start, int End, string Source, string Text, bool Interpolated);

    /// <summary>Every string literal in the text, in source order.</summary>
    /// <param name="text">Comment-stripped C# source.</param>
    /// <returns>The literals.</returns>
    public static IReadOnlyList<Literal> FindLiterals(string text)
    {
        var literals = new List<Literal>();
        var i = 0;
        while (i < text.Length)
        {
            if (TryReadLiteral(text, i, out var literal))
            {
                literals.Add(literal);
                i = literal.End;
                continue;
            }

            i++;
        }

        return literals;
    }

    /// <summary>
    /// Whether the character at <paramref name="index"/> begins a string literal
    /// (<c>"</c>, <c>@"</c>, <c>$"</c>, <c>$@"</c>, <c>@$"</c>).
    /// </summary>
    public static bool TryReadLiteral(string text, int index, out Literal literal)
    {
        literal = null!;
        var i = index;
        var interpolated = false;
        var verbatim = false;

        while (i < text.Length && (text[i] == '$' || text[i] == '@') && i - index < 2)
        {
            if (text[i] == '$') interpolated = true;
            else verbatim = true;
            i++;
        }

        if (i >= text.Length || text[i] != '"') return false;
        if (!interpolated && !verbatim && index != i) return false;

        var value = new StringBuilder();
        i++; // opening quote
        while (i < text.Length)
        {
            var c = text[i];

            if (verbatim)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    value.Append('"');
                    i += 2;
                    continue;
                }

                if (c == '"') break;
                value.Append(c);
                i++;
                continue;
            }

            if (c == '\\')
            {
                value.Append(Unescape(text, ref i));
                continue;
            }

            if (interpolated && c == '{')
            {
                if (i + 1 < text.Length && text[i + 1] == '{')
                {
                    value.Append("{{");
                    i += 2;
                    continue;
                }

                SkipInterpolationHole(text, ref i);
                value.Append(' ');
                continue;
            }

            if (interpolated && c == '}' && i + 1 < text.Length && text[i + 1] == '}')
            {
                value.Append("}}");
                i += 2;
                continue;
            }

            if (c == '"') break;
            value.Append(c);
            i++;
        }

        if (i >= text.Length) return false;
        var end = i + 1;
        literal = new Literal(index, end, text[index..end], value.ToString(), interpolated);
        return true;
    }

    /// <summary>Decodes one backslash escape, advancing <paramref name="i"/> past it.</summary>
    private static string Unescape(string text, ref int i)
    {
        i++; // backslash
        if (i >= text.Length) return "\\";
        var c = text[i++];
        switch (c)
        {
            case 'n': return "\n";
            case 'r': return "\r";
            case 't': return "\t";
            case '0': return "\0";
            case 'a': return "\a";
            case 'b': return "\b";
            case 'f': return "\f";
            case 'v': return "\v";
            case '\\': return "\\";
            case '"': return "\"";
            case '\'': return "'";
            case 'u' when i + 4 <= text.Length:
                var u = text.Substring(i, 4);
                i += 4;
                return ushort.TryParse(u, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var uv)
                    ? ((char)uv).ToString()
                    : "\\u" + u;
            case 'U' when i + 8 <= text.Length:
                var big = text.Substring(i, 8);
                i += 8;
                return uint.TryParse(big, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bv) && bv <= 0x10FFFF
                    ? char.ConvertFromUtf32((int)bv)
                    : "\\U" + big;
            case 'x':
                var start = i;
                while (i < text.Length && i - start < 4 && Uri.IsHexDigit(text[i])) i++;
                var hex = text[start..i];
                return int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hv)
                    ? ((char)hv).ToString()
                    : "\\x" + hex;
            default: return c.ToString();
        }
    }

    /// <summary>
    /// Skips one <c>{ … }</c> interpolation hole, nested braces, braces inside nested strings and
    /// nested string literals included, leaving <paramref name="i"/> on the closing <c>}</c>.
    /// </summary>
    private static void SkipInterpolationHole(string text, ref int i)
    {
        var depth = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                var j = i + 1;
                while (j < text.Length)
                {
                    if (text[j] == '\\') { j += 2; continue; }
                    if (text[j] == '"') break;
                    j++;
                }

                i = j + 1;
                continue;
            }

            if (c == '\'')
            {
                var j = i + 1;
                while (j < text.Length)
                {
                    if (text[j] == '\\') { j += 2; continue; }
                    if (text[j] == '\'') break;
                    j++;
                }

                i = j + 1;
                continue;
            }

            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return;
            }

            i++;
        }
    }

    /// <summary>The index of the <c>)</c> matching the <c>(</c> at <paramref name="openParen"/>, or -1.</summary>
    /// <param name="text">Comment-stripped source.</param>
    /// <param name="openParen">Index of an opening parenthesis.</param>
    /// <returns>The matching closing parenthesis, or -1 when unbalanced.</returns>
    public static int FindCloseParen(string text, int openParen)
    {
        var depth = 0;
        var i = openParen;
        while (i < text.Length)
        {
            if (TryReadLiteral(text, i, out var literal))
            {
                i = literal.End;
                continue;
            }

            var c = text[i];
            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) return i;
            }

            i++;
        }

        return -1;
    }

    /// <summary>
    /// The top-level comma-separated argument spans inside <c>( … )</c>, as (start, end) pairs.
    /// An empty argument list yields no ranges. Generic angle brackets are not balanced — a comma
    /// inside <c>&lt;…&gt;</c> would split early; no sink argument in this tree needs that.
    /// </summary>
    /// <param name="text">Comment-stripped source.</param>
    /// <param name="openParen">Index of the opening parenthesis.</param>
    /// <returns>One range per argument, or an empty list.</returns>
    public static IReadOnlyList<(int Start, int End)> SplitArguments(string text, int openParen)
    {
        var close = FindCloseParen(text, openParen);
        if (close < 0) return [];

        var args = new List<(int Start, int End)>();
        var depth = 0;
        var start = openParen + 1;
        var i = start;
        var sawContent = false;

        void Commit(int end)
        {
            var s = start;
            while (s < end && char.IsWhiteSpace(text[s])) s++;
            var e = end;
            while (e > s && char.IsWhiteSpace(text[e - 1])) e--;
            if (e > s) args.Add((s, e));
        }

        while (i < close)
        {
            if (TryReadLiteral(text, i, out var literal))
            {
                sawContent = true;
                i = literal.End;
                continue;
            }

            var c = text[i];
            if (c is '(' or '[' or '{')
            {
                depth++;
                sawContent = true;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                Commit(i);
                start = i + 1;
                sawContent = false;
            }
            else if (!char.IsWhiteSpace(c))
            {
                sawContent = true;
            }

            i++;
        }

        if (sawContent || args.Count > 0) Commit(close);
        return args;
    }

    /// <summary>Whether a decoded literal contains at least one letter — the word-bearing test.</summary>
    /// <param name="literal">The literal to test.</param>
    /// <returns>True when a letter survives outside the interpolation holes.</returns>
    public static bool HasWord(Literal literal) => literal.Text.Any(char.IsLetter);

    /// <summary>
    /// The text with every string literal blanked to spaces, so a call regex cannot match inside
    /// one. Literal spans and line structure are preserved.
    /// </summary>
    /// <param name="text">Comment-stripped source.</param>
    /// <returns>The masked text.</returns>
    public static string MaskLiterals(string text)
    {
        var chars = text.ToCharArray();
        foreach (var literal in FindLiterals(text))
        {
            for (var i = literal.Start; i < literal.End; i++)
                chars[i] = ' ';
        }

        return new string(chars);
    }
}

/// <summary>
/// The English text ratchet's sink definitions: every sink is a place where a string reaches the
/// screen, per the ticket's list, and the scanner records the literal exactly as written.
/// </summary>
/// <remarks>
/// Deliberately not a sentence heuristic. A sink is a syntactic position, so a new literal in one
/// shows up as a new baseline entry with no judgement call, and an exemption is a documented list
/// entry rather than a word list. Log calls, exception constructors and <c>nameof</c> are excluded
/// by construction: they are not among the sinks, and <c>nameof</c> is not a string literal.
/// </remarks>
internal static class TextLiteralScan
{
    /// <summary>One recorded literal: its file, its source text and how often the file has it.</summary>
    /// <param name="Path">Repo-relative path with forward slashes.</param>
    /// <param name="Literal">The literal exactly as written (`$"… x"`, `"…"`, or the XAML value).</param>
    /// <param name="Count">Occurrences of this literal at sinks in this file.</param>
    internal sealed record Entry(string Path, string Literal, int Count);

    /// <summary>The five UI projects plus the Core/lib producer files the ticket names.</summary>
    public static readonly string[] ScannedProjectPrefixes =
    [
        "src/EyeCtrl.App/",
        "src/EyeCtrl.Controls/",
        "src/EyeCtrl.ViewModels/",
        "src/EyeCtrl.Supervisor/",
        "src/EyeCtrl.EyeCare/",
    ];

    /// <summary>
    /// Producer files scanned in full: their returned literals are UI text. Exception-constructor
    /// literals and surface-id/key arguments are excluded everywhere by <see cref="IsExcludedSink"/>.
    /// </summary>
    public static readonly string[] ProducerFiles =
    [
        "src/EyeCtrl.Core/Models/RingLabelMap.cs",
        "src/EyeCtrl.Core/Settings/ThemeSettings.cs",
        "src/EyeCtrl.Supervisor/Supervision/SupervisorStateText.cs",
        "src/EyeCtrl.App/Overlays/CalibrationCompletion.cs",
        "src/EyeCtrl.ViewModels/CalibrationFailureText.cs",
    ];

    /// <summary>
    /// Producer <em>members</em> whose return values are UI text (everything after <c>=&gt;</c> or
    /// inside the braces). Used where the enclosing file carries non-UI literals too.
    /// </summary>
    private static readonly (string Path, string Member)[] ProducerMembers =
    [
        ("src/EyeCtrl.App/Services/SelfRestartStrategy.cs", "Describe"),
        ("src/EyeCtrl.App/Overlays/TrackStatusWindow.xaml.cs", "QualityWord"),
        ("src/EyeCtrl.App/Views/Settings/Sources/CalibrationPageSource.cs", "StatusText"),
    ];

    /// <summary>
    /// C# sinks whose arguments carry text. The regex finds the call on the comment-stripped text
    /// with literals masked out, so a literal's content can never look like a call.
    /// </summary>
    private static readonly Regex SinkCall = new(
        @"(?<![A-Za-z0-9_])(?<name>ShowModal|ShowError|new\s+ModalChoice|new\s+ToolStripMenuItem|MessageBox\.Show|AutomationProperties\.SetName|new\s+(?:SliderSpec|ToggleSpec|ComboBoxSpec|ColorPickerSpec|ButtonSpec|LabelSpec|RingPreviewSpec|RingJumpSpec)|new\s+QuickActionPreset|new\s+(?:DwellToggle|DwellComboBox|DwellColorPicker|DwellButton))\s*\(",
        RegexOptions.Compiled);

    /// <summary>Member assignments that take text directly.</summary>
    private static readonly Regex DottedAssign = new(
        @"\.(?<member>Text|Content|Header|Title|ToolTipText|ToolTip|Label|Description|ErrorText|AutomationProperties\.Name)\s*=(?!=)",
        RegexOptions.Compiled);

    /// <summary>
    /// Bare (initializer / named-parameter) assignments that take text directly.
    /// <c>ColorSwatchNames</c> is the one host-side field: the settings window declares the
    /// swatch names there (T02 fixup-01) so the count can be pinned against the palette, and the
    /// names are host text like any other.
    /// </summary>
    private static readonly Regex BareAssign = new(
        @"(?<![A-Za-z0-9_.])(?<member>Text|Content|Header|Title|ToolTip|Description|Label|onText|offText|swatchNames|notAvailableFormat|accessibleName|AccessibleName|ColorSwatchNames)\s*=(?!=)",
        RegexOptions.Compiled);

    /// <summary>Named arguments that take text.</summary>
    private static readonly Regex NamedArgument = new(
        @"(?<![A-Za-z0-9_-])(?<name>accessibleName|AccessibleName|onText|offText|swatchNames|notAvailableFormat|Display|label)\s*:",

        RegexOptions.Compiled);

    /// <summary>Which constructor arguments carry text, by argument index.</summary>
    private static int[] TextArgumentIndexes(string sinkName) => sinkName switch
    {
        "LabelSpec" => [0],
        "ComboBoxSpec" => [1, 2],          // label + Display array
        "RingPreviewSpec" => [2, 3],       // the two captions
        // The controls' text arguments. DwellComboBox lists both overloads' marker positions
        // (9 convenience / 10 main); the extra index is an int on the other overload, so it can
        // never contribute a literal.
        "DwellToggle" => [2, 3, 4],        // label, onText, offText
        "DwellComboBox" => [2, 3, 9, 10],  // label, displayOptions, notAvailableFormat
        "DwellColorPicker" => [2, 8],      // label, swatchNames (presets are hex data)
        "DwellButton" => [2],              // label
        _ => [1],                          // spec labels
    };

    /// <summary>Whether <paramref name="relative"/> is on the ratchet's scan surface.</summary>
    /// <param name="relative">Repo-relative path with forward slashes.</param>
    /// <returns>True when the file is scanned.</returns>
    public static bool IsScanned(string relative) =>
        ScannedProjectPrefixes.Any(prefix => relative.StartsWith(prefix, StringComparison.Ordinal))
        || ProducerFiles.Contains(relative, StringComparer.Ordinal);

    /// <summary>The scanned files: the five UI projects plus the Core/lib producer files.</summary>
    /// <returns>Comment-stripped (relative path, text) pairs.</returns>
    public static IEnumerable<(string Relative, string Text)> ScannedSourceFiles() =>
        LocalizationTestData.SourceFiles().Where(file => IsScanned(file.Relative));

    /// <summary>Scans one file's comment-stripped text and returns every sink literal.</summary>
    /// <param name="path">Repo-relative path, forward slashes.</param>
    /// <param name="text">Comment-stripped source.</param>
    /// <returns>One entry per (literal, sink occurrence).</returns>
    public static IReadOnlyList<Entry> FindSites(string path, string text)
    {
        var isXaml = path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
        var hits = isXaml ? FindXamlSites(text) : FindCSharpSites(path, text);

        var byLiteral = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var literal in hits)
        {
            byLiteral[literal] = byLiteral.TryGetValue(literal, out var n) ? n + 1 : 1;
        }

        return byLiteral
            .Select(pair => new Entry(path, pair.Key, pair.Value))
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ThenBy(e => e.Literal, StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> FindCSharpSites(string path, string text)
    {
        var literals = CSharpSourceText.FindLiterals(text);
        var found = new List<string>();
        var masked = Mask(text, literals);
        var excluded = ExcludedSpans(text, masked);

        void AddRange(int start, int end)
        {
            foreach (var literal in literals)
            {
                if (literal.Start < start || literal.Start >= end) continue;
                if (excluded.Any(span => literal.Start >= span.Start && literal.Start < span.End)) continue;
                if (!CSharpSourceText.HasWord(literal)) continue;
                found.Add(literal.Source);
            }
        }

        // Whole-file producers: every literal in the file is text.
        if (ProducerFiles.Contains(path, StringComparer.Ordinal))
        {
            AddRange(0, text.Length);
            return found;
        }

        // Member producers: literals inside the member's expression body or block.
        foreach (var (memberPath, member) in ProducerMembers)
        {
            if (path != memberPath) continue;
            var body = FindMemberBody(text, member);
            if (body is { } span) AddRange(span.Start, span.End);
        }

        // Call sinks: argument spans, narrowed per sink where the ticket says so.
        foreach (Match match in SinkCall.Matches(masked))
        {
            var name = match.Groups["name"].Value.Replace("new ", string.Empty).Trim();
            var openParen = match.Index + match.Length - 1;
            var closeParen = CSharpSourceText.FindCloseParen(text, openParen);
            if (closeParen < 0) continue;

            if (name == "ModalChoice" || name == "ToolStripMenuItem")
            {
                AddRange(openParen, closeParen);
                continue;
            }

            if (name.EndsWith("Spec", StringComparison.Ordinal)
                || name.StartsWith("Dwell", StringComparison.Ordinal))
            {
                var args = CSharpSourceText.SplitArguments(text, openParen);
                foreach (var index in TextArgumentIndexes(name))
                {
                    if (index < args.Count) AddRange(args[index].Start, args[index].End);
                }
                continue;
            }

            if (name == "QuickActionPreset")
            {
                // The Label argument: set ids and payloads are not display text.
                var args = CSharpSourceText.SplitArguments(text, openParen);
                if (args.Count > 1) AddRange(args[1].Start, args[1].End);
                continue;
            }

            AddRange(openParen, closeParen);
        }

        // Assignment and named-argument sinks: from the `=`/`:` to the end of the expression.
        foreach (Match match in DottedAssign.Matches(masked))
            AddRange(match.Index + match.Length, ExpressionEnd(text, match.Index + match.Length));
        foreach (Match match in BareAssign.Matches(masked))
            AddRange(match.Index + match.Length, ExpressionEnd(text, match.Index + match.Length));
        foreach (Match match in NamedArgument.Matches(masked))
            AddRange(match.Index + match.Length, ExpressionEnd(text, match.Index + match.Length));

        return found;
    }

    private static List<string> FindXamlSites(string text)
    {
        var found = new List<string>();
        var attribute = new Regex(
            @"(?<=\s)(?<name>Text|Content|Header|Title|ToolTip|AutomationProperties\.Name)\s*=\s*""(?<value>[^""]*)""",
            RegexOptions.Compiled);
        foreach (Match match in attribute.Matches(text))
        {
            var value = match.Groups["value"].Value;
            if (value.Length == 0 || value.TrimStart().StartsWith('{')) continue;
            if (!Decoded(value).Any(char.IsLetter)) continue;
            found.Add("\"" + value + "\"");
        }

        return found;
    }

    /// <summary>Decodes XML entities enough for the word-bearing test.</summary>
    private static string Decoded(string value) =>
        Regex.Replace(value, @"&(#x[0-9A-Fa-f]+|#\d+|[a-z]+);", m => m.Groups[1].Value switch
        {
            "amp" => "&",
            "lt" => "<",
            "gt" => ">",
            "quot" => "\"",
            "apos" => "'",
            var inner when inner.StartsWith("#x", StringComparison.OrdinalIgnoreCase) =>
                char.ConvertFromUtf32(Convert.ToInt32(inner[2..], 16)),
            var inner when inner.StartsWith('#') =>
                char.ConvertFromUtf32(Convert.ToInt32(inner[1..], CultureInfo.InvariantCulture)),
            _ => " ",
        });

    /// <summary>Replaces every literal with spaces, so call regexes cannot match inside one.</summary>
    private static string Mask(string text, IReadOnlyList<CSharpSourceText.Literal> literals) =>
        CSharpSourceText.MaskLiterals(text);

    /// <summary>
    /// Spans whose literals are never UI text: exception-constructor messages (the ticket's
    /// exclusion), <c>FormatProviderFor("…")</c> surface ids, and <c>T("…")</c> string keys —
    /// ids and keys are the machinery, not the text.
    /// </summary>
    private static List<(int Start, int End)> ExcludedSpans(string text, string masked)
    {
        var spans = new List<(int Start, int End)>();
        var patterns = new[]
        {
            @"new\s+\w*Exception\s*\(",
            @"(?<![A-Za-z0-9_])FormatProviderFor\s*\(",
            @"(?<![A-Za-z0-9_])T\s*\(",
        };

        foreach (var pattern in patterns)
        {
            foreach (Match match in Regex.Matches(masked, pattern))
            {
                var openParen = masked.IndexOf('(', match.Index);
                var close = CSharpSourceText.FindCloseParen(text, openParen);
                if (close > 0) spans.Add((openParen, close));
            }
        }

        return spans;
    }

    /// <summary>
    /// The end of the expression starting at <paramref name="start"/>: the next top-level
    /// <c>;</c>, <c>,</c> or <c>)</c>, string literals skipped.
    /// </summary>
    private static int ExpressionEnd(string text, int start)
    {
        var depth = 0;
        var i = start;
        while (i < text.Length)
        {
            if (CSharpSourceText.TryReadLiteral(text, i, out var literal))
            {
                i = literal.End;
                continue;
            }

            var c = text[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') { if (depth == 0) return i; depth--; }
            else if (c is ';' or ',' && depth == 0) return i;

            i++;
        }

        return text.Length;
    }

    /// <summary>
    /// The span of <paramref name="member"/>'s body in <paramref name="text"/>: everything after
    /// <c>=&gt;</c> to the matching <c>;</c>, or the balanced <c>{ … }</c> block after the
    /// parameter list. Null when the member is absent.
    /// </summary>
    private static (int Start, int End)? FindMemberBody(string text, string member)
    {
        var masked = Mask(text, CSharpSourceText.FindLiterals(text));
        var declaration = Regex.Match(masked, $@"(?<![A-Za-z0-9_]){Regex.Escape(member)}\s*\(");
        if (!declaration.Success) return null;

        var openParen = masked.IndexOf('(', declaration.Index);
        var closeParen = CSharpSourceText.FindCloseParen(text, openParen);
        if (closeParen < 0) return null;

        var rest = text[(closeParen + 1)..];
        var trimmed = rest.TrimStart();
        var offset = closeParen + 1 + (rest.Length - trimmed.Length);

        if (trimmed.StartsWith("=>", StringComparison.Ordinal))
        {
            var bodyStart = offset + 2;
            var bodyEnd = ExpressionEnd(text, bodyStart);
            return (bodyStart, bodyEnd);
        }

        if (trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            var depth = 0;
            var i = offset;
            while (i < text.Length)
            {
                if (CSharpSourceText.TryReadLiteral(text, i, out var literal))
                {
                    i = literal.End;
                    continue;
                }

                if (text[i] == '{') depth++;
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth == 0) return (offset, i + 1);
                }

                i++;
            }
        }

        return null;
    }
}
