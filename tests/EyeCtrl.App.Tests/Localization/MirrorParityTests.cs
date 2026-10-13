namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using NFluent;
using Xunit;

/// <summary>
/// D3's mirror parity (T08): a key declared with <c>mirrors: partner</c> must carry the same German
/// as its partner — the Settings ring preview against the live ring, the picker presets against the
/// grid presets. The English twins are deliberately separate keys (one key per surface); the
/// <c>mirrors</c> declaration and this gate exist so the two surfaces cannot drift apart in German.
///
/// <para>
/// Binding since T10 drafted the German: both keys of a pair must carry German, and the same German.
/// A pair translated on one side only is red too — one surface would read German while its twin
/// fell back to English.
/// </para>
/// </summary>
public class MirrorParityTests
{
    [Fact]
    public void EveryMirroredKey_CarriesThePartnersGerman()
    {
        using var english = LocalizationTestData.EnglishDocument();
        var errors = new List<string>();
        var german = ReadGermanTable(GermanPath(), errors);

        var violations = new List<string>(FindViolations(english.RootElement, german));
        violations.AddRange(errors);

        Check.WithCustomMessage(
                "A mirrored key's German differs from its partner's. The pair exists so the two "
                + "surfaces read the same German; give both keys the same text.\n  "
                + string.Join("\n  ", violations))
             .That(violations).IsEmpty();
    }

    /// <summary>
    /// R4: a moved or renamed German file must redden this gate, not turn it vacuously green —
    /// which is what "no German yet" looks like. The reader reports the absence as an error.
    /// </summary>
    [Fact]
    public void TheGate_FailsWhenTheGermanFileIsAbsent()
    {
        var errors = new List<string>();
        var german = ReadGermanTable(
            Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.json"), errors);

        Check.That(german).IsEmpty();
        Check.That(errors).HasSize(1);
        Check.That(errors[0]).Contains(StringTable.GermanFileName);
    }

    // ────── the comparator's falsification pins (the mutation check, on a fixture table) ──────

    [Fact]
    public void TheGate_CatchesADivergentGerman()
    {
        var violations = FindViolations(
            """
            {
              "settings.ring.leftClick": { "mirrors": "overlays.ring.leftClick" }
            }
            """,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["settings.ring.leftClick"] = "Linksklick",
                ["overlays.ring.leftClick"] = "Links klicken",
            });

        Check.That(violations).HasSize(1);
        Check.That(violations[0]).Contains("settings.ring.leftClick");
        Check.That(violations[0]).Contains("Links klicken");
    }

    [Fact]
    public void TheGate_AcceptsEqualGerman()
    {
        var violations = FindViolations(
            """
            {
              "settings.ring.leftClick": { "mirrors": "overlays.ring.leftClick" }
            }
            """,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["settings.ring.leftClick"] = "Linksklick",
                ["overlays.ring.leftClick"] = "Linksklick",
            });

        Check.That(violations).IsEmpty();
    }

    [Fact]
    public void TheGate_FailsWhenOnlyOneSideHasGerman()
    {
        // Binding since T10: the partner is translated and the mirrored side is not — or the other
        // way round. One of the two surfaces would read English beside its German twin.
        var violations = FindViolations(
            """
            {
              "settings.ring.leftClick": { "mirrors": "overlays.ring.leftClick" },
              "settings.ring.drag": { "mirrors": "overlays.ring.drag" }
            }
            """,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["overlays.ring.leftClick"] = "Linksklick",
                ["settings.ring.drag"] = "Ziehen",
            });

        Check.That(violations).HasSize(2);
        Check.That(violations[0]).Contains("only overlays.ring.leftClick has German");
        Check.That(violations[1]).Contains("only settings.ring.drag has German");
    }

    [Fact]
    public void TheGate_StaysQuietWhenNeitherSideHasGerman()
    {
        var violations = FindViolations(
            """
            {
              "settings.ring.leftClick": { "mirrors": "overlays.ring.leftClick" }
            }
            """,
            new Dictionary<string, string?>(StringComparer.Ordinal));

        Check.That(violations).IsEmpty();
    }

    [Fact]
    public void TheGate_ComparesPluralFormsToo()
    {
        // A plural pair must match form FOR form: "1 sample" against "1 sample" is not parity if
        // the other form drifted.
        var violations = FindViolations(
            """
            {
              "settings.count": { "mirrors": "overlays.count" }
            }
            """,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["settings.count"] = "1 Probe\u001f{0} Proben",
                ["overlays.count"] = "1 Probe\u001f{0} Stichproben",
            });

        Check.That(violations).HasSize(1);
        Check.That(violations[0]).Contains("settings.count");
    }

    /// <summary>
    /// R4 (T08 review-01): a DROPPED <c>mirrors:</c> field must fail, not silently shrink this
    /// gate's declared-pairs-only loop. The shipped table's pair set is pinned against the pairs
    /// D3 names — the 14 ring keys and the 24 presets — derived from the action enum and
    /// <see cref="QuickActionPresets"/>, so an action or preset added without its twin fails here.
    /// </summary>
    [Fact]
    public void TheShippedTable_DeclaresExactlyTheExpectedMirrorPairs()
    {
        using var english = LocalizationTestData.EnglishDocument();
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in english.RootElement.EnumerateObject())
        {
            if (property.Name.StartsWith("_", StringComparison.Ordinal)) continue;
            if (property.Value.TryGetProperty("mirrors", out var mirror)
                && mirror.ValueKind == JsonValueKind.String)
            {
                declared[property.Name] = mirror.GetString()!;
            }
        }

        var expected = ExpectedMirrorPairs();
        var missing = expected.Keys
            .Except(declared.Keys, StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
        var undeclared = declared.Keys
            .Except(expected.Keys, StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
        Check.WithCustomMessage(
                "The shipped table's mirrors declarations drifted from the D3 pair set. Missing: ["
                + string.Join(", ", missing) + "]. Not in the expected set: ["
                + string.Join(", ", undeclared) + "].")
            .That(missing.Concat(undeclared)).IsEmpty();

        foreach (var (key, partner) in expected)
        {
            Check.WithCustomMessage($"{key} mirrors {declared[key]} instead of {partner}.")
                .That(declared[key]).IsEqualTo(partner);
        }
    }

    /// <summary>
    /// The D3 pairs, derived from the action enum and the preset lists so an addition cannot slip
    /// past: every action's ring key, Back, More, and every preset.
    /// </summary>
    private static Dictionary<string, string> ExpectedMirrorPairs()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var action in Enum.GetValues<InteractionAction>())
            AddRingPair(expected, RingLabelMap.For(action));
        AddRingPair(expected, RingLabelMap.For(null));
        expected["settings.ring.more"] = "overlays.ring.more";

        foreach (var preset in QuickActionPresets.KeyboardShortcuts
                     .Concat(QuickActionPresets.MediaControls)
                     .Concat(QuickActionPresets.WindowActions))
        {
            expected["settings.preset." + preset.Id] = "overlays.preset." + preset.Id;
        }

        return expected;

        static void AddRingPair(Dictionary<string, string> expected, string overlaysKey)
        {
            // "overlays.ring.leftClick" → "settings.ring.leftClick".
            expected["settings" + overlaysKey["overlays".Length..]] = overlaysKey;
        }
    }

    // ────── the readers ──────

    /// <summary>
    /// The shipped German table's source path. The file name comes from
    /// <see cref="StringTable.GermanFileName"/> so a rename moves both in one step; the gate fails
    /// loudly when the file is not there.
    /// </summary>
    private static string GermanPath() => Path.Combine(
        SourceScanner.RepoRoot().FullName,
        "src", "EyeCtrl.App", "Localization", StringTable.GermanFileName);

    /// <summary>
    /// The German table at <paramref name="path"/>. An absent file appends an error and reads as
    /// no German: a silently missing table must never look like "German not written yet".
    /// </summary>
    private static IReadOnlyDictionary<string, string?> ReadGermanTable(string path, List<string> errors)
    {
        if (!File.Exists(path))
        {
            errors.Add(
                $"'{StringTable.GermanFileName}' is absent at {path}; the mirror-parity gate cannot run");
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.StartsWith("_", StringComparison.Ordinal)) continue;
            result[property.Name] = property.Value.TryGetProperty("text", out var text)
                ? CanonicalText(text)
                : null;
        }
        return result;
    }

    private static IReadOnlyList<string> FindViolations(string englishJson, IReadOnlyDictionary<string, string?> german)
    {
        using var document = JsonDocument.Parse(englishJson);
        return FindViolations(document.RootElement, german);
    }

    private static IReadOnlyList<string> FindViolations(
        JsonElement english, IReadOnlyDictionary<string, string?> german)
    {
        var violations = new List<string>();
        foreach (var property in english.EnumerateObject())
        {
            if (property.Name.StartsWith("_", StringComparison.Ordinal)) continue;
            if (!property.Value.TryGetProperty("mirrors", out var mirror)
                || mirror.ValueKind != JsonValueKind.String) continue;

            var partner = mirror.GetString()!;
            var hasOwn = german.TryGetValue(property.Name, out var own) && own is not null;
            var hasOther = german.TryGetValue(partner, out var other) && other is not null;
            if (!hasOwn && !hasOther) continue;
            if (hasOwn != hasOther)
            {
                violations.Add($"{property.Name}: only {(hasOwn ? property.Name : partner)} has German; its twin "
                               + $"{(hasOwn ? partner : property.Name)} would fall back to English");
                continue;
            }

            if (!string.Equals(own, other, StringComparison.Ordinal))
                violations.Add($"{property.Name}: German \"{own}\" differs from {partner} \"{other}\"");
        }
        return violations;
    }

    /// <summary>
    /// One German value's comparable form: the string itself, or a plural's two forms joined with
    /// U+001F. A shape mismatch (plural against singular) therefore reads as a difference.
    /// </summary>
    private static string? CanonicalText(JsonElement text) => text.ValueKind switch
    {
        JsonValueKind.String => text.GetString(),
        JsonValueKind.Object
            when text.TryGetProperty("one", out var one) && text.TryGetProperty("other", out var other)
            => one.GetString() + "\u001f" + other.GetString(),
        _ => null,
    };
}
