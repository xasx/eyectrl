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

            for (var i = 0; i < completions.Count; i++)
            {
                Check.That(completions[i].completion).As($"{lang}/{probe}[{i}] text").Not.IsNullOrEmpty();
                if (i > 0)
                    Check.That(completions[i - 1].frequency)
                        .As($"{lang}/{probe} ranking at {i}")
                        .IsGreaterOrEqualThan(completions[i].frequency);
            }
        }
        Check.That(anyCompletions).As($"at least one probe word for '{lang}' returns completions").IsTrue();
    }
}
