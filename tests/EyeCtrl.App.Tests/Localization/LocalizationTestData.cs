namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using EyeCtrl.Core.Localization;

/// <summary>
/// The shared source/data readers for the localization gates: the shipped English table, and every
/// <c>.cs</c>/<c>.xaml</c> file under <c>src/</c> (comments stripped, obj/bin excluded) — the same
/// scan surface the DpiHelper source guards use.
/// </summary>
internal static class LocalizationTestData
{
    /// <summary>The embedded English table, parsed by production code itself.</summary>
    public static StringTableContent EnglishContent()
    {
        using var stream = typeof(StringTable).Assembly
            .GetManifestResourceStream(StringTable.EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                $"The embedded resource '{StringTable.EmbeddedResourceName}' is missing from EyeCtrl.Core.");
        using var reader = new StreamReader(stream);
        return StringTableContent.ParseEnglish(reader.ReadToEnd());
    }

    /// <summary>The embedded English table as JSON, for gates that need the full entry metadata.</summary>
    public static JsonDocument EnglishDocument()
    {
        using var stream = typeof(StringTable).Assembly
            .GetManifestResourceStream(StringTable.EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                $"The embedded resource '{StringTable.EmbeddedResourceName}' is missing from EyeCtrl.Core.");
        using var reader = new StreamReader(stream);
        return JsonDocument.Parse(reader.ReadToEnd());
    }

    /// <summary>Every .cs/.xaml file under src/, comment-stripped, as repo-relative pairs.</summary>
    public static IEnumerable<(string Relative, string Text)> SourceFiles()
    {
        var root = SourceScanner.RepoRoot().FullName;
        var src = Path.Combine(root, "src");
        foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
        {
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            yield return (Path.GetRelativePath(root, file).Replace('\\', '/'),
                          SourceScanner.StripComments(File.ReadAllText(file)));
        }
    }
}

/// <summary>
/// The reference kinds the key-coverage and single-surface gates see in a source text:
/// C# <c>T("…")</c> and <c>T(language, "…")</c> calls, the XAML <c>{loc:T …}</c> markup extension,
/// and key-map string literals (a literal whose first segment is a surface id).
/// </summary>
/// <remarks>
/// Convention: keys are written literally at the call site. A key built with <c>$"…"</c> is
/// invisible to these gates and to the validator's unused-key scan, which is exactly why the
/// convention exists.
/// </remarks>
internal static class StringKeyReferences
{
    internal sealed record Reference(string Key, string Kind);

    private static readonly string[] FileLikeSuffixes =
    [
        ".json", ".jsonl", ".dll", ".exe", ".csv", ".log", ".wav", ".onnx",
        ".xml", ".xaml", ".cs", ".md", ".txt", ".bin", ".png", ".ico"
    ];

    private static readonly Regex CSharpCall = new(
        @"(?<![A-Za-z0-9_])T\s*\(\s*""(?<key>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex CSharpNamedCall = new(
        @"(?<![A-Za-z0-9_])T\s*\(\s*UiLanguage\s*\.\s*\w+\s*,\s*""(?<key>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex XamlExtension = new(
        @"\{\s*loc\s*:\s*T\s+(?:Key\s*=\s*)?['""]?(?<key>[A-Za-z0-9_.\-]+)['""]?\s*\}",
        RegexOptions.Compiled);

    private static readonly Regex DottedLiteral = new(
        @"""(?<key>[a-z][a-z0-9\-]*(?:\.[a-z0-9\-]+)+)""",
        RegexOptions.Compiled);

    /// <summary>All references in comment-stripped source text, deduplicated by key.</summary>
    public static IReadOnlyList<Reference> Extract(string sourceText)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<Reference>();

        void Add(Regex regex, string kind)
        {
            foreach (Match match in regex.Matches(sourceText))
            {
                var key = match.Groups["key"].Value;
                if (seen.Add(key)) found.Add(new Reference(key, kind));
            }
        }

        Add(CSharpCall, "T(...) call");
        Add(CSharpNamedCall, "T(language, ...) call");
        Add(XamlExtension, "{loc:T} usage");

        foreach (Match match in DottedLiteral.Matches(sourceText))
        {
            var literal = match.Groups["key"].Value;
            if (!IsKeyMapLiteral(literal)) continue;
            if (seen.Add(literal))
                found.Add(new Reference(literal, "key-map literal"));
        }

        return found;
    }

    /// <summary>
    /// A key-map literal: the literal's first segment is a surface id and its last is not a file
    /// extension, so <c>"settings.title"</c> counts and <c>"settings.json"</c> does not.
    /// </summary>
    private static bool IsKeyMapLiteral(string literal)
    {
        var segments = literal.Split('.');
        if (!StringTableSchema.Surfaces.Contains(segments[0], StringComparer.Ordinal)) return false;
        return !FileLikeSuffixes.Contains("." + segments[^1], StringComparer.OrdinalIgnoreCase);
    }
}
