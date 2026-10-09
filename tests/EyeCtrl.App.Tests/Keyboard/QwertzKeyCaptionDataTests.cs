namespace EyeCtrl.App.Tests.Keyboard;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using EyeCtrl.Core.Keyboard;

/// <summary>
/// The T14 Phase-2 captions in the SHIPPED qwertz layout (user's picks, 2026-10-07). Pinned as
/// data because three things about them are safety-relevant and none is visible to a build:
/// the specialAction ids classify keys for the escaping dwell floor
/// (KeyboardSpecialActions.IsAppEscaping), the accessible names are the only pronounceable form
/// of a symbol caption, and a caption that grows past its key is a live fit failure the window
/// tests measure but this file pins at the source.
///
/// <para>
/// The expected tables below are the full picks from the T14 Phase-2 hand-off (PHASE2-README.md
/// sections 1 and 3).
/// The set equality against the layout is deliberate: adding a key action, renaming one, or
/// swapping a caption all fail here until the pick table is updated, which is the point.
/// </para>
/// </summary>
public class QwertzKeyCaptionDataTests
{
    private static KeyboardLayout Qwertz() => KeyboardLayoutLoader.Load(Path.Combine(
        SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Resources", "Layouts", "qwertz.json"));

    /// <summary>Every picked caption, keyed by specialAction: label, optional label frame and accessibleName (null = none).</summary>
    private static readonly Dictionary<string, (string Label, string? Frame, string? Name)> FinalPicks = BuildPicks();

    private static Dictionary<string, (string Label, string? Frame, string? Name)> BuildPicks()
    {
        var picks = new Dictionary<string, (string, string?, string?)>(StringComparer.Ordinal)
        {
            ["modifier:win"] = ("Win", null, null),
            ["modifier:alt"] = ("Alt", null, null),
            ["modifier:ctrl"] = ("Strg", null, null),
            ["modifier:shift"] = ("\u21E7", "shiftArrow", "Umschalttaste"),       // ⇧ wide drawn outline arrow
            ["toggle:capslock"] = ("\u2B06", "capsArrow", "Feststelltaste"),    // ⬆ wide filled arrow + lock bar
            ["backspace"] = ("\u232B", null, "R\u00FCcktaste"),                 // ⌫  Rücktaste
            ["deleteWord"] = ("Aa", "eraseLeft", "Wort l\u00F6schen"),          // Aa inside the erase arrow (C3+D3, 2026-10-08)
            ["space"] = ("\u2334", null, "Leertaste"),                          // ⌴
            ["enter"] = ("\u21B5", "enterArrow", "Eingabetaste"),               // ↵ drawn the same way (G4+)
            ["tab"] = ("\u21E5", null, "Tabulatortaste"),                       // ⇥
            ["switchEmoji"] = ("\uD83D\uDE00", null, "Emoji"),                  // 😀
            ["switchSub:letters"] = ("ABC", null, null),
            ["switchSub:numbers"] = ("123", null, null),
            ["switchSub:symbols"] = ("#+=", null, null),
            ["switchSub:function"] = ("Fn", null, null),
            ["switchSub:accents"] = ("\u00E0\u00E9", null, null),               // àé
            ["key:Escape"] = ("Esc", null, null),
            ["key:PrintScreen"] = ("Druck", null, null),
            ["key:Home"] = ("Pos1", null, null),
            ["key:End"] = ("Ende", null, null),
            ["key:PageUp"] = ("Bild\u2191", null, "Bild-auf-Taste"),            // Bild↑
            ["key:PageDown"] = ("Bild\u2193", null, "Bild-ab-Taste"),           // Bild↓
            ["key:Insert"] = ("Einfg", null, null),
            ["key:Delete"] = ("Entf", null, null),
            ["key:ArrowUp"] = ("\u2191", null, "Nach-oben-Taste"),              // ↑
            ["key:ArrowDown"] = ("\u2193", null, "Nach-unten-Taste"),           // ↓
            ["key:ArrowLeft"] = ("\u2190", null, "Nach-links-Taste"),           // ←
            ["key:ArrowRight"] = ("\u2192", null, "Nach-rechts-Taste"),         // →
            ["shortcut:Ctrl+C"] = ("Kopieren", null, null),
            ["shortcut:Ctrl+V"] = ("Einf\u00FCgen", null, null),                // Einfügen
            ["shortcut:Ctrl+X"] = ("\u2702", null, "Ausschneiden"),             // ✂
            ["shortcut:Ctrl+Z"] = ("\u21B6", null, "R\u00FCckg\u00E4ngig"),     // ↶  Rückgängig
            ["shortcut:Ctrl+Y"] = ("\u21B7", null, "Wiederholen"),              // ↷
            ["shortcut:Ctrl+A"] = ("Alles mark.", null, null),
            ["addWord"] = ("+ Wort", null, null),
        };
        for (var i = 1; i <= 12; i++) picks[$"key:F{i}"] = ($"F{i}", null, null);
        return picks;
    }

    /// <summary>The specialAction ids each sub-layout ships, in file order (T14 must not rename any).</summary>
    private static readonly Dictionary<string, string[]> ExpectedActionsBySubLayout =
        new(StringComparer.Ordinal)
        {
            ["letters"] =
            [
                "modifier:win", "switchSub:accents", "toggle:capslock", "modifier:alt",
                "modifier:shift", "backspace", "deleteWord", "modifier:ctrl", "switchSub:numbers",
                "switchSub:symbols", "switchEmoji", "switchSub:function", "space", "enter", "tab",
            ],
            ["numbers"] =
            [
                "modifier:win", "toggle:capslock", "modifier:alt", "modifier:shift", "backspace",
                "deleteWord", "modifier:ctrl", "switchSub:letters", "switchSub:symbols",
                "switchEmoji", "switchSub:function", "space", "enter", "tab",
            ],
            ["symbols"] =
            [
                "modifier:win", "toggle:capslock", "modifier:alt", "modifier:shift", "backspace",
                "deleteWord", "modifier:ctrl", "switchSub:letters", "switchSub:numbers",
                "switchEmoji", "switchSub:function", "space", "enter", "tab",
            ],
            ["accents"] =
            [
                "backspace", "deleteWord", "switchSub:letters", "switchSub:numbers",
                "switchSub:symbols", "switchEmoji", "switchSub:function", "space", "enter", "tab",
            ],
            ["function"] =
            [
                "key:Escape", "key:F1", "key:F2", "key:F3", "key:F4", "key:F5", "key:F6", "key:F7",
                "key:F8", "key:F9", "key:PrintScreen", "key:F10", "key:F11", "key:F12", "key:Home",
                "key:End", "key:PageUp", "key:PageDown", "key:Insert", "key:Delete",
                "shortcut:Ctrl+C", "shortcut:Ctrl+V", "shortcut:Ctrl+X", "shortcut:Ctrl+Z",
                "shortcut:Ctrl+Y", "shortcut:Ctrl+A", "addWord", "switchSub:letters",
                "key:ArrowUp", "space", "enter", "tab", "switchSub:numbers", "key:ArrowLeft",
                "key:ArrowDown", "key:ArrowRight", "backspace", "deleteWord",
            ],
        };

    [Fact]
    public void EverySpecialKey_CarriesItsPickedCaptionAndAccessibleName()
    {
        var layout = Qwertz();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (subId, sub) in layout.SubLayouts)
            foreach (var key in sub.Rows.SelectMany(r => r))
            {
                if (key.SpecialAction is null) continue;
                seen.Add(key.SpecialAction);

                Check.WithCustomMessage(
                        $"{subId}: action '{key.SpecialAction}' is not in the T14 pick table")
                     .That(FinalPicks.ContainsKey(key.SpecialAction)).IsTrue();
                var (label, frame, name) = FinalPicks[key.SpecialAction];
                Check.WithCustomMessage(
                        $"{subId}: '{key.SpecialAction}' captions '{key.Label}', expected '{label}'")
                     .That(key.Label).IsEqualTo(label);
                Check.WithCustomMessage(
                        $"{subId}: '{key.SpecialAction}' labelFrame '{key.LabelFrame}', expected '{frame}'")
                     .That(key.LabelFrame).IsEqualTo(frame);
                Check.WithCustomMessage(
                        $"{subId}: '{key.SpecialAction}' accessibleName '{key.AccessibleName}', expected '{name}'")
                     .That(key.AccessibleName).IsEqualTo(name);
            }

        Check.WithCustomMessage(
                "The pick table names actions the layout no longer ships: "
                + string.Join(", ", FinalPicks.Keys.Where(k => !seen.Contains(k))))
             .That(FinalPicks.Keys.Except(seen, StringComparer.Ordinal)).IsEmpty();
    }

    [Fact]
    public void EverySpecialActionId_StillBelongsToItsSubLayoutsShippedSet()
    {
        // T14 is a caption change and must not rename an id: IsAppEscaping classifies keys by id
        // and KeyboardViewModel holds the escaping keys' dwell floor under it. Per sub-layout, in
        // file order, so a removal, rename, addition, or a key moving between layouts fails.
        var layout = Qwertz();

        Check.WithCustomMessage("qwertz.json's sub-layout set changed")
             .That(layout.SubLayouts.Keys).HasSize(ExpectedActionsBySubLayout.Count);
        Check.That(layout.SubLayouts.Keys)
             .ContainsOnlyElementsThatMatch(id => ExpectedActionsBySubLayout.ContainsKey(id));

        foreach (var (subId, expected) in ExpectedActionsBySubLayout)
        {
            var actual = layout.SubLayouts[subId].Rows.SelectMany(r => r)
                .Where(k => k.SpecialAction is not null)
                .Select(k => k.SpecialAction!)
                .ToArray();
            Check.WithCustomMessage(
                    $"{subId}: specialAction ids changed — expected [{string.Join(", ", expected)}], "
                    + $"got [{string.Join(", ", actual)}]")
                 .That(actual).IsEqualTo(expected);
        }
    }

    [Fact]
    public void AccessibleNames_SitOnExactlyTheSpecialKeysThatNeedOne()
    {
        // The mechanism: a name is required when the caption is not its own name — a symbol
        // caption, or a framed caption whose letters are decoration ("Aa" inside the erase
        // arrow). And only then: a name on a plain word key ("Strg") fails too.
        var layout = Qwertz();
        var problems = new List<string>();

        foreach (var (subId, sub) in layout.SubLayouts)
            foreach (var key in sub.Rows.SelectMany(r => r))
            {
                var needsName = key.SpecialAction is not null
                                && (ContainsSymbol(key.Label) || key.LabelFrame is not null);
                var hasName = key.AccessibleName is not null;
                if (needsName != hasName)
                    problems.Add($"{subId}: '{key.Label}' ({key.SpecialAction ?? "no action"}) "
                                 + $"needsName={needsName} but accessibleName={hasName}");
            }

        Check.WithCustomMessage(string.Join("; ", problems)).That(problems).IsEmpty();
    }

    [Fact]
    public void EveryAccessibleName_IsNonEmptyAndNotTheRawGlyph()
    {
        var layout = Qwertz();
        var named = layout.SubLayouts.Values.SelectMany(s => s.Rows.SelectMany(r => r))
            .Where(k => k.AccessibleName is not null).ToList();

        Check.WithCustomMessage("no accessible names found — the test would be vacuous")
             .That(named).Not.IsEmpty();
        Check.That(named.All(k => !string.IsNullOrWhiteSpace(k.AccessibleName))).IsTrue();
        Check.That(named.All(k => k.AccessibleName != k.Label)).IsTrue();
    }

    /// <summary>True when any text element is a non-ASCII math or other symbol (the README's name trigger).</summary>
    private static bool ContainsSymbol(string label)
    {
        var elements = StringInfo.GetTextElementEnumerator(label);
        while (elements.MoveNext())
        {
            var codePoint = char.ConvertToUtf32((string)elements.Current, 0);
            if (codePoint > 0x7F
                && CharUnicodeInfo.GetUnicodeCategory(codePoint)
                    is UnicodeCategory.MathSymbol or UnicodeCategory.OtherSymbol)
                return true;
        }
        return false;
    }
}
