namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using EyeCtrl.Core.Prediction;
using NFluent;
using Xunit;

/// <summary>
/// Guards the SHIPPED Resources/Completions tries, not synthetic fixtures — SentenceCompletionIndexTests
/// already covers the trie against hand-built indexes, which is exactly how the shipped files were able
/// to stay absent for months while the loader silently produced a null trie. The files are read through
/// the same BaseDirectory/Resources path PredictionStackFactory uses, so this also fails if the Content
/// copy is ever dropped from the csproj.
/// </summary>
public class ShippedCompletionsTests
{
    private const int MaxBytesPerLanguage = 10 * 1024 * 1024;

    private static readonly string[] Languages = ["en", "fr", "de", "es", "pl"];

    // Near-guaranteed sentence starters in any Tatoeba sample; a regeneration may
    // legitimately rotate individual words out, but not all of a language's at once.
    private static readonly Dictionary<string, string[]> ProbeWords = new()
    {
        ["en"] = ["i", "you", "how"],
        ["fr"] = ["je", "tu", "comment"],
        ["de"] = ["ich", "du", "wie"],
        ["es"] = ["que", "yo", "como"],
        ["pl"] = ["czy", "jak", "ty"],
    };

    public static IEnumerable<object[]> LanguagesData() =>
        Languages.Select(lang => new object[] { lang });

    private static string TriePath(string lang) =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Completions", $"completions_{lang}.bin");

    [Fact]
    public void EveryLanguage_ShipsATrieBelowTheSizeBudget()
    {
        foreach (var lang in Languages)
        {
            var path = TriePath(lang);
            Check.That(File.Exists(path)).As($"shipped trie at {path}").IsTrue();
            Check.That(new FileInfo(path).Length).As($"{lang} trie size").IsStrictlyLessThan(MaxBytesPerLanguage);
        }
    }

    [Theory]
    [MemberData(nameof(LanguagesData))]
    public void EveryTrie_LoadsAndAnswersAProbeWord(string lang)
    {
        using var stream = File.OpenRead(TriePath(lang));
        var index = SentenceCompletionIndex.Load(stream);

        var anyCompletions = false;
        foreach (var probe in ProbeWords[lang])
        {
            var completions = index.GetCompletions(probe, 5);
            if (completions.Count == 0) continue;
            anyCompletions = true;

            foreach (var (completion, _) in completions)
                Check.That(completion).As($"{lang}/{probe} completion text").Not.IsNullOrEmpty();

            // Asserting descending frequency would only re-test GetCompletions' own OrderByDescending.
            // What the shipped data has to prove is the shortness bias: its weight is
            // maxWords + 1 - sentenceLength, and every completion under one node was cut at the same
            // prefix length, so ranking first means being the shorter completion. Regenerate without
            // the bias flag and near-unique sentences all weigh 1, which drops this ordering.
            var wordCounts = completions
                .Select(c => c.completion.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)
                .ToList();
            Check.That(wordCounts.First()).As($"{lang}/{probe} top completion length").IsLessOrEqualThan(wordCounts.Last());
        }
        Check.That(anyCompletions).As($"at least one probe word for '{lang}' returns completions").IsTrue();
    }
}
