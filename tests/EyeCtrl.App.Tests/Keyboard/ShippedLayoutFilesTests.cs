namespace EyeCtrl.App.Tests.Keyboard;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;

/// <summary>
/// Loads the three SHIPPED layout files — not synthetic JSON — and checks the specialAction
/// grammar they use. A typo in a layout file otherwise surfaces only at runtime, as a dead key
/// that dwells forever and does nothing.
/// </summary>
public class ShippedLayoutFilesTests
{
    private static readonly string[] LayoutFiles = ["qwerty.json", "azerty.json", "qwertz.json"];

    // Exact-case as the layouts spell them. KeyboardWindow matches case-insensitively at
    // runtime, but a shipped file spelling anything differently is exactly the drift this
    // test exists to catch.
    private static readonly string[] KnownActions =
    [
        "backspace", "enter", "tab", "space", "deleteWord", "switchEmoji", "addWord",
        "modifier:shift", "modifier:ctrl", "modifier:alt", "modifier:win", "toggle:capslock"
    ];

    private static string LayoutDir() =>
        Path.Combine(SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Resources", "Layouts");

    public static IEnumerable<object[]> LayoutNames() =>
        LayoutFiles.Select(f => new object[] { f });

    private static KeyboardLayout Load(string file) =>
        KeyboardLayoutLoader.Load(Path.Combine(LayoutDir(), file));

    // Impl review M4: the EXACT ordered contents of the accents content rows (spec D2/D3) —
    // the concatenated Output values of the non-special keys per row, in file order. The
    // shape theory above cannot see a content edit: deleting the `ç Ç` pair or swapping
    // `« »` for the declined German quotes `„ “` (D12) passes shape but fails here.
    private static readonly Dictionary<string, string[]> AccentsRowSequences = new()
    {
        ["qwertz.json"] = ["éÉèÈêÊëËàÀ", "âÂçÇîÎïÏôÔ", "ùÙûÛœŒ«»"],
        // T6: à moved off the AZERTY letters footer (a footer plain-key budget of two), so its
        // accents page gained the à/À pair at row 0's end — the row reflowed to the QWERTZ-style
        // ten 1.28-unit keys to keep the 12.8 standard.
        ["azerty.json"] = ["êÊâÂîÎôÔàÀ", "ûÛëËïÏùÙ", "œŒ«»"],
    };

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public void TheLayout_ParsesAndUsesOnlyKnownSpecialActions(string file)
    {
        var layout = Load(file);

        var unknown = new List<string>();
        foreach (var key in layout.SubLayouts.Values.SelectMany(sub => sub.Rows.SelectMany(r => r)))
        {
            var action = key.SpecialAction;
            if (action is null) continue;
            if (KnownActions.Contains(action)
                || action.StartsWith("switchSub:")
                || action.StartsWith("key:")
                || action.StartsWith("shortcut:")) continue;
            unknown.Add(action);
        }

        Check.That(unknown).IsEmpty();
    }

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public void EverySubLayout_CarriesBackspaceAndDeleteWord(string file)
    {
        var layout = Load(file);

        foreach (var sub in layout.SubLayouts.Values)
        {
            var actions = sub.Rows.SelectMany(r => r).Select(k => k.SpecialAction).ToList();
            Check.WithCustomMessage($"{layout.Name}/{sub.Label} backspace")
                 .That(actions).Contains("backspace");
            Check.WithCustomMessage($"{layout.Name}/{sub.Label} deleteWord")
                 .That(actions).Contains("deleteWord");
        }
    }

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public void EveryNonAutoReturnPage_CarriesEveryModifier(string file)
    {
        // T5 + fixup 01 (keyboard-one-look): every page a user can dwell onto must carry every
        // `modifier:*` key ANY page of the layout carries, so an armed modifier is never
        // invisible. KeyboardViewModel keeps all of them armed across non-autoReturn page
        // switches (D15: only the SYSTEM modifiers are cleared, and only when entering an
        // autoReturn page) — and an armed Shift on a textless page changes what its keys send
        // (review 01 F1: Entf becomes Shift+Del — a permanent delete in Explorer — Kopieren
        // becomes Ctrl+Shift+C). The set is computed dynamically from the shipped files, so
        // adding a modifier to one page without spreading it to the others fails here.
        //
        // The match is case-INSENSITIVE because the window lowercases special actions before
        // dispatching (KeyboardWindow.GetModifierFromAction switches on ToLowerInvariant): a
        // key spelled "Modifier:Alt" would work in the app and must not be invisible to this
        // theory. The non-empty guard keeps it from passing vacuously if the files ever stop
        // carrying modifiers at all (fixup 01 F2).
        //
        // AutoReturn pages (the accents picker) remain exempt: entering them clears the system
        // modifiers (KeyboardViewModel.SwitchSubLayout), so those cannot be armed there — and
        // Shift, which survives entry, is visible through the keys themselves: every accents
        // text key carries its shiftOutput and UpdateShiftDisplay shows the capital while
        // Shift is armed (a T5-era scope note claimed Shift needed no key anywhere; that
        // generalised the accents case wrongly and was reversed by the owner's fixup decision).
        var layout = Load(file);

        var modifiers = layout.SubLayouts.Values.SelectMany(s => s.Rows.SelectMany(r => r))
            .Select(k => k.SpecialAction)
            .Where(a => a is not null && a.StartsWith("modifier:", StringComparison.OrdinalIgnoreCase))
            .Select(a => a!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Check.WithCustomMessage(
                $"{file} carries no modifier:* keys at all — the per-page assertions below would assert nothing.")
            .That(modifiers).Not.IsEmpty();

        foreach (var (page, sub) in layout.SubLayouts)
        {
            if (sub.AutoReturn) continue;
            var actions = sub.Rows.SelectMany(r => r)
                .Select(k => k.SpecialAction)
                .Where(a => a is not null)
                .Select(a => a!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var modifier in modifiers)
                Check.WithCustomMessage(
                        $"{file}/{page} does not carry {modifier}: an armed modifier survives the " +
                        "page switch (D15), so it would be armed and INVISIBLE here — add its key to the page.")
                    .That(actions.Contains(modifier)).IsTrue();
        }
    }

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public void TheFunctionSubLayout_CarriesTheClipboardRow(string file)
    {
        var layout = Load(file);

        var actions = layout.SubLayouts["function"].Rows.SelectMany(r => r)
            .Select(k => k.SpecialAction).ToList();
        foreach (var chord in new[]
                 {
                     "shortcut:Ctrl+C", "shortcut:Ctrl+V", "shortcut:Ctrl+X",
                     "shortcut:Ctrl+Z", "shortcut:Ctrl+Y", "shortcut:Ctrl+A"
                 })
            Check.WithCustomMessage(chord).That(actions).Contains(chord);
    }

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public void TheFunctionSubLayout_CarriesAddWord(string file)
    {
        // The explicit teach-the-predictor key (prediction controls #22). It lives on the Fn
        // clipboard row — the utility-action family — not beside Bksp: adding there would have
        // squeezed the already-narrowed Shift/Bksp pair on the 960-DIP screen.
        var layout = Load(file);

        var actions = layout.SubLayouts["function"].Rows.SelectMany(r => r)
            .Select(k => k.SpecialAction).ToList();
        Check.WithCustomMessage($"{layout.Name} function addWord")
             .That(actions).Contains("addWord");
    }

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public void AddWord_DoesNotWidenTheKeyboard(string file)
    {
        // The row carrying Add Word must stay under the letters sublayout's widest row, which
        // defines the keyboard's width: a wider utility row would widen the whole SizeToContent
        // window and shift every dwell target.
        var layout = Load(file);

        var lettersWidest = layout.SubLayouts["letters"].Rows
            .Max(r => r.Sum(k => k.Width));
        var addWordRow = layout.SubLayouts["function"].Rows
            .Single(r => r.Any(k => k.SpecialAction == "addWord"));
        Check.WithCustomMessage(
                 $"{layout.Name}: add-word row width {addWordRow.Sum(k => k.Width)} vs letters widest {lettersWidest}")
             .That(addWordRow.Sum(k => k.Width)).IsLessOrEqualThan(lettersWidest);
    }

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public void SpecialActions_AreUniqueWithinASubLayout(string file)
    {
        // KeyboardWindow ids special-action keys BY their SpecialAction, so a duplicate would
        // make two keys share one dwell target id. The window's comment says "verified per
        // layout" — this is that verification.
        var layout = Load(file);

        foreach (var sub in layout.SubLayouts.Values)
        {
            var ids = sub.Rows.SelectMany(r => r)
                .Where(k => !string.IsNullOrEmpty(k.SpecialAction))
                .Select(k => k.SpecialAction).ToList();
            Check.WithCustomMessage($"{layout.Name}/{sub.Label} repeats an id")
                 .That(ids.Distinct().Count()).IsEqualTo(ids.Count);
        }
    }

    [Theory]
    [InlineData("qwertz.json")]
    [InlineData("azerty.json")]
    public void AccentsSubLayout_IsAutoReturn_WithWellFormedKeys(string file)
    {
        var layout = Load(file);
        Check.That(layout.SubLayouts.ContainsKey("accents")).IsTrue();
        var accents = layout.SubLayouts["accents"];

        Check.WithCustomMessage("accents must auto-return (spec D5)")
            .That(accents.AutoReturn).IsTrue();

        var keys = accents.Rows.SelectMany(r => r).ToList();
        Check.WithCustomMessage("every non-special accents key must emit text (spec M2)")
            .That(keys.Where(k => k.SpecialAction is null).All(k => !string.IsNullOrEmpty(k.Output)))
            .IsTrue();
        Check.WithCustomMessage(
                "every lower-case accents key must carry EXACTLY its invariant upper-case as " +
                "shiftOutput (spec D11; a wrong-but-non-empty capital fails — plan-r2)")
            .That(keys.Where(k => k.SpecialAction is null
                                   && k.Output.Length == 1
                                   && char.IsLetter(k.Output[0])
                                   && char.IsLower(k.Output[0]))
                      .All(k => k.ShiftOutput == char.ToUpperInvariant(k.Output[0]).ToString()))
            .IsTrue();

        // Widest row must equal the 12.8 standard so the SizeToContent window's WIDTH never
        // changes between sub-layouts (spec shape rules).
        var widest = accents.Rows.Max(r => r.Sum(k => k.Width));
        Check.WithCustomMessage($"widest accents row is {widest:0.##}, expected 12.8")
            .That(Math.Abs(widest - 12.8) < 0.01).IsTrue();

        // Row count must EQUAL the letters sub-layout's (both layouts ship 3 content rows +
        // footer = 4) so the bottom-docked window's HEIGHT never changes and no row shifts
        // under the gaze (spec shape rules, plan review H2).
        Check.WithCustomMessage(
                $"accents has {accents.Rows.Count} rows, letters has " +
                $"{layout.SubLayouts[layout.DefaultSubLayout].Rows.Count} — must match (plan H2)")
            .That(accents.Rows.Count)
            .IsEqualTo(layout.SubLayouts[layout.DefaultSubLayout].Rows.Count);

        // Impl review M4: pin the exact ordered Output sequence of every content row (the
        // footer follows and carries only special keys, so it concatenates to "").
        var expectedSequences = AccentsRowSequences[file];
        Check.WithCustomMessage(
                $"{file}: accents ships {accents.Rows.Count - 1} content rows, " +
                $"{expectedSequences.Length} pinned")
            .That(accents.Rows.Count - 1).IsEqualTo(expectedSequences.Length);
        for (var i = 0; i < expectedSequences.Length; i++)
        {
            var actual = string.Concat(accents.Rows[i]
                .Where(k => k.SpecialAction is null)
                .Select(k => k.Output));
            Check.WithCustomMessage(
                    $"{file} accents row {i}: expected the exact ordered set " +
                    $"'{expectedSequences[i]}', got '{actual}'")
                .That(actual).IsEqualTo(expectedSequences[i]);
        }
    }

    [Theory]
    [InlineData("qwerty.json")]
    [InlineData("azerty.json")]
    [InlineData("qwertz.json")]
    public void EverySwitchSubTarget_NamesAnExistingSubLayout(string file)
    {
        // Spec M5 over the REAL shipped data, ALL THREE files including QWERTY (plan-r2:
        // the accents theory only covers the two layouts that carry accents).
        var layout = Load(file);
        var ids = layout.SubLayouts.Keys.ToHashSet();
        var targets = layout.SubLayouts.Values.SelectMany(s => s.Rows.SelectMany(r => r))
            .Select(k => k.SpecialAction)
            .Where(a => a is not null && a.StartsWith("switchSub:", StringComparison.Ordinal))
            .Select(a => a!["switchSub:".Length..]);
        Check.WithCustomMessage($"{file} dangles a switchSub: target")
            .That(targets.All(t => ids.Contains(t))).IsTrue();
    }

    [Fact]
    public void Qwertz_Letters_IsVariantA_ExactlyAsApproved()
    {
        // T6 variant A (owner-approved 2026-11-11): the letters page, exactly as the ticket pins
        // it. Wort löschen takes the top-left slot at Win's old 2.0 width (today's label, frame
        // and accessible name), `?` takes row 0's right end as a plain text key, ß takes the
        // third row's right end at 1.2 (today's output/shiftOutput), and the footer is the
        // owner's exact ten-key row: Win Strg ⇥ 123 Fn #+= , ⌴ . ↵. Rows are centred, so the
        // R1–R3 sums 12.8/12.2/12.8 keep every letter at its pre-edit position (see
        // Letters_NoLetterMoves_ByUnitOffset).
        var layout = Load("qwertz.json");
        var letters = layout.SubLayouts["letters"];

        // Row 0: Aa 2.0 | Q..P | ? 0.8 = 12.8.
        var row0 = letters.Rows[0];
        var delWd = row0[0];
        Check.That(delWd.SpecialAction).IsEqualTo("deleteWord");
        Check.That(delWd.Width).IsCloseTo(2.0, 0.01);
        Check.That(delWd.Label).IsEqualTo("Aa");
        Check.That(delWd.LabelFrame).IsEqualTo("eraseLeft");
        Check.That(delWd.AccessibleName).IsEqualTo("Wort löschen");
        var question = row0[^1];
        Check.That(question.Output).IsEqualTo("?");
        Check.That(question.SpecialAction).IsNull();
        Check.That(question.Width).IsCloseTo(0.8, 0.01);
        Check.That(row0.Sum(k => k.Width)).IsCloseTo(12.8, 0.01);

        // Row 2: Alt ⇧ | Y..Ü | ⌫ 1.2 (today's position) | ß 1.2 = 12.8.
        var row2 = letters.Rows[2];
        var sharpS = row2[^1];
        Check.That(sharpS.Output).IsEqualTo("ß");
        Check.That(sharpS.ShiftOutput).IsEqualTo("ẞ");
        Check.That(sharpS.SpecialAction).IsNull();
        Check.That(sharpS.Width).IsCloseTo(1.2, 0.01);
        Check.That(row2.Single(k => k.SpecialAction == "backspace").Width).IsCloseTo(1.2, 0.01);
        Check.That(row2.IndexOf(row2.Single(k => k.SpecialAction == "backspace"))).IsEqualTo(row2.Count - 2);
        Check.That(row2.Sum(k => k.Width)).IsCloseTo(12.8, 0.01);

        // Row 3: the owner's footer, in order, at the approved widths — 12.8.
        var footer = letters.Rows[3];
        var expected = new (string Label, string? Action, double Width)[]
        {
            ("Win", "modifier:win", 1.0),
            ("Strg", "modifier:ctrl", 1.0),
            ("⇥", "tab", 1.2),
            ("123", "switchSub:numbers", 1.3),
            ("Fn", "switchSub:function", 1.0),
            ("#+=", "switchSub:symbols", 1.3),
            (",", null, 1.0),
            ("⌴", "space", 2.5),
            (".", null, 1.0),
            ("↵", "enter", 1.5),
        };
        Check.That(footer).HasSize(expected.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            var (label, action, width) = expected[i];
            var key = footer[i];
            Check.WithCustomMessage($"footer[{i}] label '{key.Label}', expected '{label}'")
                .That(key.Label).IsEqualTo(label);
            Check.WithCustomMessage($"footer[{i}] '{label}' action '{key.SpecialAction}', expected '{action}'")
                .That(key.SpecialAction).IsEqualTo(action);
            Check.WithCustomMessage($"footer[{i}] '{label}' width {key.Width}, expected {width}")
                .That(key.Width).IsCloseTo(width, 0.01);
        }
        Check.That(footer.Sum(k => k.Width)).IsCloseTo(12.8, 0.01);
    }

    [Fact]
    public void Qwertz_SharpS_GainsCapitalShiftOutput()
    {
        var layout = Load("qwertz.json");
        // D7: ẞ (U+1E9E). Data-pinned, not view-model echo (spec I6): deleting the JSON
        // attribute must fail right here.
        var sharpS = layout.SubLayouts["letters"].Rows.SelectMany(r => r)
            .Single(k => k.Output == "ß");
        Check.That(sharpS.ShiftOutput).IsEqualTo("ẞ");
    }

    [Fact]
    public void Azerty_Letters_Row0_TakesWortLoschenAndQuestionMark_WithZeroMovement()
    {
        // T6's rule on AZERTY: Wort löschen takes Win's top-left slot at Win's width (2.0), the
        // accents switch at row 0's right end becomes `?`, and the slot Wort löschen vacated in
        // the third row takes ç — so every letter keeps its position (the no-letter-moves
        // theory below pins each offset).
        var layout = Load("azerty.json");
        var row0 = layout.SubLayouts["letters"].Rows[0];

        var delWd = row0[0];
        Check.That(delWd.SpecialAction).IsEqualTo("deleteWord");
        Check.That(delWd.Label).IsEqualTo("DelWd");
        Check.That(delWd.Width).IsCloseTo(2.0, 0.01);
        var question = row0[^1];
        Check.That(question.Output).IsEqualTo("?");
        Check.That(question.SpecialAction).IsNull();
        Check.That(question.Width).IsCloseTo(0.8, 0.01);
        Check.That(row0.Sum(k => k.Width)).IsCloseTo(12.8, 0.01);

        var row2 = layout.SubLayouts["letters"].Rows[2];
        var cedilla = row2[^1];
        Check.That(cedilla.Output).IsEqualTo("ç");
        Check.That(cedilla.ShiftOutput).IsEqualTo("Ç");
        Check.That(cedilla.SpecialAction).IsNull();
        Check.That(cedilla.Width).IsCloseTo(1.2, 0.01);
        Check.That(row2.Single(k => k.SpecialAction == "backspace").Width).IsCloseTo(1.2, 0.01);
        Check.That(row2.Sum(k => k.Width)).IsCloseTo(12.8, 0.01);
    }

    [Fact]
    public void Azerty_LettersAccentKeys_GainCapitals()
    {
        var layout = Load("azerty.json");
        var letters = layout.SubLayouts["letters"].Rows.SelectMany(r => r).ToList();

        // D6 data pins (spec I6): the letters accent keys carry their capitals IN THE
        // FILE — deleting an attribute fails here, not just in a view-model echo. T6 moved à
        // off the letters page (its plain-key budget in the footer is two), so it is pinned on
        // its new accents-page home below.
        Check.That(letters.Single(k => k.Output == "é").ShiftOutput).IsEqualTo("É");
        Check.That(letters.Single(k => k.Output == "è").ShiftOutput).IsEqualTo("È");
        Check.That(letters.Single(k => k.Output == "ç").ShiftOutput).IsEqualTo("Ç");
        Check.That(letters.All(k => k.Output != "à")).IsTrue();

        var accents = layout.SubLayouts["accents"].Rows.SelectMany(r => r).ToList();
        var aGrave = accents.Single(k => k.Output == "à");
        Check.WithCustomMessage("à's capital must ride its accents key's shiftOutput")
            .That(aGrave.ShiftOutput).IsEqualTo("À");
        Check.That(accents.Count(k => k.Output == "À")).IsEqualTo(1);
    }

    // ---- T6: the letters-page geometry rules ----------------------------------------------------
    //
    // Two pins over the shipped letters pages, both in UNIT space (a key's x offset is the row's
    // centring margin plus the widths before it; rows are 1 unit tall) — the exact proxy of the
    // window's DIP layout, because key slots are laid out left to right in width order and each
    // row is centred in the page's widest row.
    //
    //   - No letter moves: every letter's (row, offset) is pinned to its pre-edit value. Rows are
    //     centred, so "the row sums stay 12.8/12.2/12.8" and "no width before a letter changes"
    //     are the same statement as "no letter moves by a single DIP" — this theory states it in
    //     data instead of prose. ß (QWERTZ) and ç/à (AZERTY) are deliberately absent: their move
    //     IS the ticket.
    //   - Tab is never adjacent to Enter, and no app-escaping key is adjacent to Space, on every
    //     letters page. Adjacency is KeyboardE1Slip's own rule (boxes inflated by 5 % of the key
    //     pitch — corner contact included), evaluated in unit space against pitch measured the
    //     same way E1 measures it: the smallest distance between any two key centres of the page.
    //     The owner's E1 log showed Enter and Tab sharing a corner and the escaping keys beside
    //     Space; this pins the fix everywhere, not just on QWERTZ.
    //
    // Mutants: (a) put Tab back beside Enter (the old `… ⌴ ↵ ⇥` footer ending) → the adjacency
    // theory red; (b) widen one key 0.1 anywhere left of a letter → the offsets theory red.

    private const double PageStandardUnits = 12.8;

    /// <summary>
    /// The pinned (row, unit offset) of every letter that must not move, captured from the
    /// pre-edit files (the layout this ticket was cut from). Offsets are the centring margin
    /// plus the widths of the keys before the letter in its row.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, (int Row, double Offset)>> LetterPositions = new()
    {
        ["qwerty.json"] = new()
        {
            ["q"] = (0, 2.0), ["w"] = (0, 3.0), ["e"] = (0, 4.0), ["r"] = (0, 5.0), ["t"] = (0, 6.0),
            ["y"] = (0, 7.0), ["u"] = (0, 8.0), ["i"] = (0, 9.0), ["o"] = (0, 10.0), ["p"] = (0, 11.0),
            ["a"] = (1, 2.5), ["s"] = (1, 3.5), ["d"] = (1, 4.5), ["f"] = (1, 5.5),
            ["g"] = (1, 6.5), ["h"] = (1, 7.5), ["j"] = (1, 8.5), ["k"] = (1, 9.5), ["l"] = (1, 10.5),
            ["z"] = (2, 2.9), ["x"] = (2, 3.9), ["c"] = (2, 4.9), ["v"] = (2, 5.9),
            ["b"] = (2, 6.9), ["n"] = (2, 7.9), ["m"] = (2, 8.9),
        },
        ["azerty.json"] = new()
        {
            ["a"] = (0, 2.0), ["z"] = (0, 3.0), ["e"] = (0, 4.0), ["r"] = (0, 5.0), ["t"] = (0, 6.0),
            ["y"] = (0, 7.0), ["u"] = (0, 8.0), ["i"] = (0, 9.0), ["o"] = (0, 10.0), ["p"] = (0, 11.0),
            ["q"] = (1, 2.0), ["s"] = (1, 3.0), ["d"] = (1, 4.0), ["f"] = (1, 5.0),
            ["g"] = (1, 6.0), ["h"] = (1, 7.0), ["j"] = (1, 8.0), ["k"] = (1, 9.0), ["l"] = (1, 10.0),
            ["m"] = (1, 11.0),
            ["w"] = (2, 2.4), ["x"] = (2, 3.4), ["c"] = (2, 4.4), ["v"] = (2, 5.4),
            ["b"] = (2, 6.4), ["n"] = (2, 7.4), ["é"] = (2, 8.4), ["è"] = (2, 9.4),
        },
        ["qwertz.json"] = new()
        {
            ["q"] = (0, 2.0), ["w"] = (0, 3.0), ["e"] = (0, 4.0), ["r"] = (0, 5.0), ["t"] = (0, 6.0),
            ["z"] = (0, 7.0), ["u"] = (0, 8.0), ["i"] = (0, 9.0), ["o"] = (0, 10.0), ["p"] = (0, 11.0),
            ["a"] = (1, 1.5), ["s"] = (1, 2.5), ["d"] = (1, 3.5), ["f"] = (1, 4.5),
            ["g"] = (1, 5.5), ["h"] = (1, 6.5), ["j"] = (1, 7.5), ["k"] = (1, 8.5),
            ["l"] = (1, 9.5), ["ö"] = (1, 10.5), ["ä"] = (1, 11.5),
            ["y"] = (2, 2.4), ["x"] = (2, 3.4), ["c"] = (2, 4.4), ["v"] = (2, 5.4),
            ["b"] = (2, 6.4), ["n"] = (2, 7.4), ["m"] = (2, 8.4), ["ü"] = (2, 9.4),
        },
    };

    /// <summary>
    /// Letters that deliberately moved WITHIN the letters page (ß on QWERTZ took Wort löschen's
    /// third-row slot; ç on AZERTY did the same) and are therefore on the page but absent from
    /// the pin table. à on AZERTY is not counted: it left the letters page entirely.
    /// </summary>
    private static readonly Dictionary<string, int> DeliberateInPageMoves = new()
    {
        ["qwerty.json"] = 0,
        ["azerty.json"] = 1,   // ç
        ["qwertz.json"] = 1,   // ß
    };

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public void Letters_NoLetterMoves_ByUnitOffset(string file)
    {
        var layout = Load(file);
        var expected = LetterPositions[file];
        var letters = CountLetters(layout);

        Check.WithCustomMessage(
                $"{file}: the pin table covers {expected.Count} letters, the letters page carries "
                + $"{letters} ({DeliberateInPageMoves[file]} deliberately moved) — a letter was added "
                + "or removed without updating the pin. Deliberate moves (ß, ç; à left the page) are "
                + "the exception; anything else must be pinned.")
            .That(expected.Count + DeliberateInPageMoves[file]).IsEqualTo(letters);

        foreach (var (subId, sub) in layout.SubLayouts)
        {
            if (subId != "letters") continue;
            for (var r = 0; r < sub.Rows.Count; r++)
            {
                var offset = (PageStandardUnits - sub.Rows[r].Sum(k => k.Width)) / 2.0;
                foreach (var key in sub.Rows[r])
                {
                    if (key.SpecialAction is null && expected.TryGetValue(key.Output, out var want))
                    {
                        Check.WithCustomMessage(
                                $"'{key.Output}' sits at row {r}, offset {offset:0.###}; before the T6 edit "
                                + $"it sat at row {want.Row}, offset {want.Offset:0.###}. Rows are centred, so "
                                + "this letter MOVED — restore the widths before it (or the row sum), or make "
                                + "the move a deliberate, owner-reviewed pin change.")
                            .That(r).IsEqualTo(want.Row);
                        Check.WithCustomMessage(
                                $"'{key.Output}' sits at row {r}, offset {offset:0.###}; before the T6 edit "
                                + $"it sat at row {want.Row}, offset {want.Offset:0.###}. Rows are centred, so "
                                + "this letter MOVED — restore the widths before it (or the row sum), or make "
                                + "the move a deliberate, owner-reviewed pin change.")
                            .That(offset).IsCloseTo(want.Offset, 0.001);
                    }
                    offset += key.Width;
                }
            }
        }
    }

    private static int CountLetters(KeyboardLayout layout) =>
        layout.SubLayouts["letters"].Rows.SelectMany(r => r)
            .Count(k => k.SpecialAction is null && k.Output.Length == 1 && char.IsLetter(k.Output[0]));

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public void Letters_TabNeverAdjacentToEnter_NoEscapingKeyAdjacentToSpace(string file)
    {
        var layout = Load(file);
        var letters = layout.SubLayouts["letters"];

        // Unit-space boxes: x = per-row centring margin + the widths before the key, y = the row
        // index, height 1. The pitch is measured exactly as E1 measures it (the smallest centre
        // distance of the page), and adjacency is KeyboardE1Slip's own inflated-box rule, so the
        // test sees the same corner contacts the running app does.
        var centres = new List<PhysicalPoint>();
        var boxes = new List<(string Id, PhysicalRect Box)>();
        for (var r = 0; r < letters.Rows.Count; r++)
        {
            var offset = (PageStandardUnits - letters.Rows[r].Sum(k => k.Width)) / 2.0;
            foreach (var key in letters.Rows[r])
            {
                var id = key.SpecialAction ?? $"key_{r}_{offset:0.###}";
                boxes.Add((id, new PhysicalRect(offset, r, key.Width, 1.0)));
                centres.Add(new PhysicalPoint(offset + key.Width / 2, r + 0.5));
                offset += key.Width;
            }
        }

        var pitch = KeyboardE1Slip.Pitch(centres);
        Check.WithCustomMessage($"{file} letters: no measurable pitch — the geometry collapsed.")
            .That(pitch).IsStrictlyGreaterThan(0.0);

        var offences = new List<string>();
        for (var i = 0; i < boxes.Count; i++)
        {
            for (var j = i + 1; j < boxes.Count; j++)
            {
                var a = boxes[i];
                var b = boxes[j];
                if (!KeyboardE1Slip.Adjacent(a.Box, b.Box, pitch)) continue;
                if (IsTabEnter(a.Id, b.Id))
                    offences.Add($"tab ('{a.Id}'/'{b.Id}') is adjacent to enter");
                if (IsEscapingSpace(layout, a.Id, b.Id))
                    offences.Add($"an app-escaping key ('{a.Id}'/'{b.Id}') is adjacent to space");
            }
        }

        Check.WithCustomMessage(
                $"{file} letters page: {string.Join("; ", offences.Distinct())}. The owner's E1 log "
                + "shows Enter+Tab sharing a corner and the escaping keys firing beside Space; the "
                + "letters pages must never sit an escaping key next to either.")
            .That(offences).IsEmpty();
    }

    private static bool IsTabEnter(string a, string b) =>
        (a.Equals("tab", StringComparison.OrdinalIgnoreCase) && b.Equals("enter", StringComparison.OrdinalIgnoreCase))
        || (a.Equals("enter", StringComparison.OrdinalIgnoreCase) && b.Equals("tab", StringComparison.OrdinalIgnoreCase));

    private static bool IsEscapingSpace(KeyboardLayout layout, string a, string b) =>
        (KeyboardSpecialActions.IsAppEscaping(Normalize(layout, a)) && b.Equals("space", StringComparison.Ordinal))
        || (KeyboardSpecialActions.IsAppEscaping(Normalize(layout, b)) && a.Equals("space", StringComparison.Ordinal));

    /// <summary>Plain keys mint positional ids in the real window; the classifier takes null for them.</summary>
    private static string? Normalize(KeyboardLayout layout, string id) =>
        id.StartsWith("key_", StringComparison.Ordinal) ? null : id;


    // ---- E1 step 0a: the size budget over every page of every layout ---------------------------
    //
    // The 12.8-unit row standard was pinned only for row 0 and the accents page; at the 2.0×
    // setting maximum only ~76 DIP of a 1920-DIP work area are spare, so ANY widened row on ANY
    // page overflows the window off-screen for a gaze user who cannot scroll to it. These
    // theories pin the whole budget, per page, over the three shipped files:
    //   - widest row <= 12.8 units (the row standard, everywhere);
    //   - rows <= 5 (the height half of the budget);
    //   - every page can be LEFT: switchSub:letters, autoReturn, or it IS the default page
    //     (a page without a way back is a one-way trap, since ActiveSubLayoutId survives hide/show);
    //   - the text-row count per page pinned as a RATCHET (rows with >= 3 plain keys): adding a
    //     text row re-bands every E1 slip region (KeyboardE1Slip.RowBand), silently invalidating
    //     the log's before/after comparison. Reducing a count is a deliberate, reviewed edit.
    //
    // Fit uses the window's own arithmetic (KeyboardWindow.xaml.cs): a row is
    // `size*w + (w>1 ? (w-1)*4 : 0) + 4` per key with key size 70 × multiplier (:22, :589), and
    // the SizeToContent window is the widest row PLUS the window chrome — Padding="8" and
    // BorderThickness="3" per side (KeyboardWindow.xaml:12-13), 22 DIP on top of the grid. The
    // work area is 1920 DIP. The corrected spare at 2.0x is 54 DIP (1920 − 1844 − 22), not the
    // 76 the debate's table quotes for the grid alone (review 01 F4). Mutants: widen the widest
    // row to 13+ units (row standard AND 2.0× fit), add a 6th row, delete a page's switch-back
    // key, add a fourth text row.

    private const double MaxRowUnits = 12.8;
    private const int MaxRowsPerPage = 5;
    private const double WorkAreaWidthDip = 1920;

    /// <summary>
    /// The window chrome around the key grid, 2 × (Padding 8 + BorderThickness 3) per side —
    /// the literal values of KeyboardWindow.xaml:12-13. If those change, this constant must
    /// change with them (a source pin on XAML values is not worth the drift risk; the fit
    /// theory below fails loudly the other way, by admitting 22 DIP too much).
    /// </summary>
    private const double WindowChromeWidthDip = 2 * (8 + 3);

    private static readonly double[] BudgetKeyMultipliers = [1.5, 2.0];

    /// <summary>The window's own row arithmetic, verbatim from KeyboardWindow.xaml.cs:589.</summary>
    private static double RowWidthDip(IEnumerable<KeyDefinition> row, double keySize)
    {
        double width = 0;
        foreach (var key in row)
            width += keySize * key.Width + (key.Width > 1 ? (key.Width - 1) * 4 : 0) + 4;
        return width;
    }

    /// <summary>A TEXT row in E1's sense: at least <see cref="KeyboardE1Slip.MinTextKeysPerTextRow"/>
    /// plain keys, so it takes part in the slip-line's row banding.</summary>
    private static int TextRowCount(SubLayout sub) =>
        sub.Rows.Count(r => r.Count(k => string.IsNullOrEmpty(k.SpecialAction))
                            >= KeyboardE1Slip.MinTextKeysPerTextRow);

    /// <summary>
    /// The pinned text-row ratchet, per file and page: today's counts. The letters pages carry 3
    /// text rows (plus the footer, which is all special keys), the accents pages 3, numbers and
    /// symbols 3, and the Fn page 0 (its rows are utility keys). A page missing from the table is
    /// a new page: add it with its count, or the theory below fails on purpose.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, int>> TextRowRatchet = new()
    {
        ["qwerty.json"] = new() { ["letters"] = 3, ["numbers"] = 3, ["symbols"] = 3, ["function"] = 0 },
        ["azerty.json"] = new() { ["letters"] = 3, ["numbers"] = 3, ["symbols"] = 3, ["accents"] = 3, ["function"] = 0 },
        ["qwertz.json"] = new() { ["letters"] = 3, ["numbers"] = 3, ["symbols"] = 3, ["accents"] = 3, ["function"] = 0 },
    };

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public void EveryPage_WidestRowStaysWithinTheStandard(string file)
    {
        var layout = Load(file);

        foreach (var (page, sub) in layout.SubLayouts)
        {
            var widest = sub.Rows.Max(r => r.Sum(k => k.Width));
            Check.WithCustomMessage(
                    $"{file}/{page}: widest row is {widest:0.##} units, over the {MaxRowUnits} standard " +
                    "that every shipped page must keep (at 2.0x this already overflows a 1920-DIP work area).")
                .That(widest).IsLessOrEqualThan(MaxRowUnits + 0.01);
        }
    }

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public void EveryPage_StaysWithinTheRowBudget(string file)
    {
        var layout = Load(file);

        foreach (var (page, sub) in layout.SubLayouts)
            Check.WithCustomMessage(
                    $"{file}/{page} ships {sub.Rows.Count} rows, over the {MaxRowsPerPage} a page may carry " +
                    "(the height half of the size budget).")
                .That(sub.Rows.Count).IsLessOrEqualThan(MaxRowsPerPage);
    }

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public void EveryPage_CanBeLeft_ByASwitchBackOrAutoReturn(string file)
    {
        var layout = Load(file);

        foreach (var (page, sub) in layout.SubLayouts)
        {
            // The default page is where the keyboard opens: nothing to come back TO.
            if (page == layout.DefaultSubLayout || sub.AutoReturn) continue;
            var actions = sub.Rows.SelectMany(r => r).Select(k => k.SpecialAction).ToHashSet();
            Check.WithCustomMessage(
                    $"{file}/{page} has neither switchSub:letters nor autoReturn: a gaze user who lands " +
                    "there has no way back (ActiveSubLayoutId survives hide/show, so the trap persists).")
                .That(actions.Contains("switchSub:letters")).IsTrue();
        }
    }

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public void EveryPage_TextRowCount_IsPinnedAsARatchet(string file)
    {
        var layout = Load(file);
        var ratchet = TextRowRatchet[file];

        foreach (var (page, sub) in layout.SubLayouts)
        {
            Check.WithCustomMessage(
                    $"{file} has no ratchet entry for page '{page}' (a new page): add it to TextRowRatchet " +
                    "with its text-row count, deliberately.")
                .That(ratchet.ContainsKey(page)).IsTrue();
            var actual = TextRowCount(sub);
            Check.WithCustomMessage(
                    $"{file}/{page} now carries {actual} text rows, over the pinned {ratchet[page]}: adding a " +
                    $"text row (>= {KeyboardE1Slip.MinTextKeysPerTextRow} plain keys) re-bands every E1 slip " +
                    "region and silently invalidates the log's before/after comparison. Reduce this pin only " +
                    "as a deliberate, reviewed change.")
                .That(actual).IsLessOrEqualThan(ratchet[page]);
        }
    }

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public void EveryPage_FitsA1920WorkArea_At1_5xAnd2_0x(string file)
    {
        var layout = Load(file);

        foreach (var (page, sub) in layout.SubLayouts)
        {
            foreach (var multiplier in BudgetKeyMultipliers)
            {
                var keySize = 70 * multiplier;
                var gridWidth = sub.Rows.Max(r => RowWidthDip(r, keySize));
                var windowWidth = gridWidth + WindowChromeWidthDip;
                Check.WithCustomMessage(
                        $"{file}/{page} at {multiplier}x needs a {windowWidth:0.#} DIP window (grid " +
                        $"{gridWidth:0.#} + {WindowChromeWidthDip} chrome), over a {WorkAreaWidthDip} " +
                        "DIP work area: the SizeToContent window would run off-screen, and a gaze user cannot " +
                        "scroll to the keys that left it.")
                    .That(windowWidth).IsLessOrEqualThan(WorkAreaWidthDip);
            }
        }
    }
}
