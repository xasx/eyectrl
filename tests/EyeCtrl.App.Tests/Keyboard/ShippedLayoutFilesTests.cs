namespace EyeCtrl.App.Tests.Keyboard;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using EyeCtrl.Core.Keyboard;

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
    public void Qwertz_LettersRow0_GainsAcc_WithZeroMovement()
    {
        var layout = Load("qwertz.json");
        var row0 = layout.SubLayouts["letters"].Rows[0];

        // D17 (supersedes D16; owner-defaulted): row 0 is Win 1.2 + Q..P = 11.2 — the same
        // zero-move trick as AZERTY. Win widens 1.2 -> 2.0 (consuming the 0.8 left margin),
        // Acc (0.8) consumes the right margin; every letter keeps its exact position and the
        // row hits the 12.8 standard. Pinned so a future edit cannot silently shift letters.
        Check.That(row0.Single(k => k.Label == "Win").Width).IsCloseTo(2.0, 0.01);
        var acc = row0.Single(k => k.SpecialAction == "switchSub:accents");
        Check.That(acc.Width).IsCloseTo(0.8, 0.01);
        Check.That(row0.IndexOf(acc)).IsEqualTo(row0.Count - 1);
        Check.That(row0.Sum(k => k.Width)).IsCloseTo(12.8, 0.01);

        // D16 explicitly NOT applied: the footer is untouched.
        var footer = layout.SubLayouts["letters"].Rows[^1];
        Check.That(footer.Sum(k => k.Width)).IsCloseTo(11.7, 0.01);
        Check.That(footer.Single(k => k.Label == "Space").Width).IsCloseTo(2.0, 0.01);
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
    public void Azerty_LettersRow0_GainsAcc_WithZeroMovement()
    {
        var layout = Load("azerty.json");
        var row0 = layout.SubLayouts["letters"].Rows[0];

        // D3 zero-move: Win widens 1.2 -> 2.0 (consuming the 0.8 left margin), Acc (0.8)
        // consumes the right margin; every letter keeps its exact position and the row hits
        // the 12.8 standard. Pinned so a future edit cannot silently shift the letters.
        Check.That(row0.Single(k => k.Label == "Win").Width).IsCloseTo(2.0, 0.01);
        var acc = row0.Single(k => k.SpecialAction == "switchSub:accents");
        Check.That(acc.Width).IsCloseTo(0.8, 0.01);
        Check.That(row0.IndexOf(acc)).IsEqualTo(row0.Count - 1);
        Check.That(row0.Sum(k => k.Width)).IsCloseTo(12.8, 0.01);
    }

    [Fact]
    public void Azerty_LettersAccentKeys_GainCapitals()
    {
        var layout = Load("azerty.json");
        var letters = layout.SubLayouts["letters"].Rows.SelectMany(r => r).ToList();

        // D6 data pins (spec I6): the four letters accent keys carry their capitals IN THE
        // FILE — deleting an attribute fails here, not just in a view-model echo.
        Check.That(letters.Single(k => k.Output == "é").ShiftOutput).IsEqualTo("É");
        Check.That(letters.Single(k => k.Output == "è").ShiftOutput).IsEqualTo("È");
        Check.That(letters.Single(k => k.Output == "à").ShiftOutput).IsEqualTo("À");
        Check.That(letters.Single(k => k.Output == "ç").ShiftOutput).IsEqualTo("Ç");
    }
}
