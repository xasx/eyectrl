namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// The edit-sync fuzz (keyboard release 1, PR d). The keyboard is a blind keystroke injector with a
/// local text mirror (<c>_recentText</c> + <c>CurrentInput</c>); the only way the mirror can CORRUPT
/// text is a prediction pick, which deletes <c>CurrentInput</c>'s length from whatever has focus.
/// So random typing/editing is run against a <see cref="SimulatedTextTarget"/> that applies exactly
/// what the keyboard sends, and after EVERY step the mirror is held to three invariants:
///
/// <list type="bullet">
/// <item><b>I1</b> — every Backspace activation sends exactly one plain <c>\b</c> and deletes exactly
/// one visible grapheme (no press "does nothing visible", none deletes a word or an Undo).</item>
/// <item><b>I2</b> — after every deletion <c>CurrentInput</c> is the trailing letter/digit run of the
/// mirror, <c>_recentText</c> is everything before it, and <c>_previousWord</c> is the last
/// letter/digit run before that (its attached trailing punctuation aside, which the sentence-end
/// guard needs), or null.</item>
/// <item><b>Trust</b> — the mirror only ever holds characters the keyboard placed in the current
/// session (it equals the target's keyboard-placed tail before the caret, or is a word-aligned
/// suffix of it once the 100-char horizon cut the front), so a pick's <c>deleteCount</c> only ever
/// deletes keyboard-placed text, and exactly <c>CurrentInput</c>.</item>
/// </list>
///
/// Every event that ends an editing session (Enter, hide, caret/clipboard keys) must leave the mirror
/// empty. Seeds are fixed, so a failure names a seed and a step and replays exactly.
/// </summary>
public class KeyboardEditSyncFuzzTests
{
    private const string Letters = "abcdefghijklmnopqrstuvwxyzäöüßABCDEÄ";
    private static readonly string[] Digits = ["0", "1", "7", "42"];
    private static readonly string[] Punctuation = [".", ",", "-", "!", "?", "("];
    private static readonly string[] Emoji = ["😀", "👍🏽", "🎉"];
    private static readonly string[] PickWords = ["hallo", "Haus", "Ende.", "über", "ß", "x1"];
    private static readonly string[] Foreign = ["", "Hallo Welt", "x", "foo-bar ", "Zeile eins"];

    private enum Op
    {
        Letter, Digit, Punctuation, Emoji, Space, DoubleSpace, Backspace, WordBackspace, Pick, Enter,
        HideShow, Arrow, CtrlV, CtrlZ, ModifiedBackspace, ModifiedWordBackspace, Tab, DeleteKey, PrintScreen, CtrlC,
    }

    private static readonly (Op Op, int Weight)[] FullMix =
    [
        (Op.Letter, 60), (Op.Digit, 6), (Op.Punctuation, 10), (Op.Emoji, 6), (Op.Space, 24), (Op.DoubleSpace, 6),
        (Op.Backspace, 36), (Op.WordBackspace, 8), (Op.Pick, 12), (Op.Enter, 4), (Op.HideShow, 3),
        (Op.Arrow, 4), (Op.CtrlV, 2), (Op.CtrlZ, 2), (Op.ModifiedBackspace, 4), (Op.ModifiedWordBackspace, 2),
        (Op.Tab, 2), (Op.DeleteKey, 1), (Op.PrintScreen, 1), (Op.CtrlC, 1),
    ];

    // Hide/show is the base's gap: only typing, Space, Enter and hide/show — no editing keys.
    private static readonly (Op Op, int Weight)[] HideMix =
    [
        (Op.Letter, 40), (Op.Space, 15), (Op.Enter, 5), (Op.HideShow, 12),
    ];

    // No session boundary at all and few deletions: sessions outgrow the 100-char horizon.
    private static readonly (Op Op, int Weight)[] LongSessionMix =
    [
        (Op.Letter, 80), (Op.Digit, 4), (Op.Punctuation, 6), (Op.Space, 30), (Op.DoubleSpace, 2),
        (Op.Backspace, 14), (Op.WordBackspace, 3), (Op.Pick, 5), (Op.Emoji, 3),
    ];

    [Fact]
    public void RandomEditing_NeverDivergesFromTheTarget()
    {
        // 60 seeds x 400 steps = 24,000 steps.
        for (var seed = 1; seed <= 60; seed++)
            Run(seed, steps: 400, FullMix);
    }

    [Fact]
    public void HideAndEnter_AloneAlwaysEndTheSession()
    {
        // 25 seeds x 200 steps. The mirror may not survive a hide: focus may have changed while the
        // keyboard was away, and a pick would then delete the new app's text.
        for (var seed = 1000; seed < 1025; seed++)
            Run(seed, steps: 200, HideMix);
    }

    [Fact]
    public void LongSessions_PastTheHorizon_KeepTheMirrorWordAligned()
    {
        // 25 seeds x 600 steps of one unbroken session: the 100-char mirror gets cut at its front
        // again and again, and a cut must never leave half a word to be "re-opened".
        for (var seed = 2000; seed < 2025; seed++)
            Run(seed, steps: 600, LongSessionMix);
    }

    private static void Run(int seed, int steps, (Op Op, int Weight)[] mix)
    {
        var rng = new Random(seed);
        var messenger = new WeakReferenceMessenger();
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), messenger);
        var target = new SimulatedTextTarget(messenger);
        vm.IsVisible = true;
        target.SeedForeign(Foreign[rng.Next(Foreign.Length)]);

        var log = new List<string>();
        var sessionExceededHorizon = false;
        var total = mix.Sum(m => m.Weight);

        for (var step = 0; step < steps; step++)
        {
            var op = Pick(rng, mix, total);
            var boundariesBefore = target.Boundaries;
            var messagesBefore = target.KeyMessages.Count;
            var caretBefore = target.Caret;
            var currentBefore = vm.CurrentInput;
            var deletionOp = op is Op.Backspace or Op.WordBackspace or Op.ModifiedBackspace or Op.ModifiedWordBackspace;

            var what = Apply(op, rng, vm, target);
            log.Add(what);

            string Fail(string problem) =>
                $"seed {seed}, step {step} ({what}): {problem}\n"
                + $"  mirror = [{vm.RecentText}] + [{vm.CurrentInput}], previousWord = {Show(vm.PreviousWord)}\n"
                + $"  target tail = [{target.PlacedTail()}], caret {target.Caret}/{target.Count}\n"
                + $"  last ops: {string.Join(" | ", log.TakeLast(14))}";

            if (target.Violations.Count > 0)
                Check.WithCustomMessage(Fail(target.Violations[0])).That(target.Violations).IsEmpty();

            if (target.Boundaries != boundariesBefore)
            {
                sessionExceededHorizon = false;
                Check.WithCustomMessage(Fail("the session ended but the mirror was not cleared"))
                    .That(vm.RecentText.Length == 0 && vm.CurrentInput.Length == 0 && vm.PreviousWord is null).IsTrue();
            }

            var mirror = vm.RecentText + vm.CurrentInput;
            var tail = target.PlacedTail();
            if (tail.Length > 100) sessionExceededHorizon = true;

            if (!sessionExceededHorizon)
            {
                Check.WithCustomMessage(Fail("the mirror differs from the text the keyboard placed")).That(mirror).IsEqualTo(tail);
            }
            else
            {
                Check.WithCustomMessage(Fail("the mirror is not a suffix of the text the keyboard placed")).That(tail.EndsWith(mirror, StringComparison.Ordinal)).IsTrue();
                if (mirror.Length > 0 && mirror.Length < tail.Length)
                {
                    var cutChar = tail[tail.Length - mirror.Length - 1];
                    Check.WithCustomMessage(Fail("the horizon cut left half a word at the front of the mirror"))
                        .That(char.IsLetterOrDigit(cutChar) && char.IsLetterOrDigit(mirror[0])).IsFalse();
                }
            }

            if (op == Op.Pick)
                Check.WithCustomMessage(Fail("a pick must delete exactly the keyboard's CurrentInput"))
                    .That(target.LastPickDeletedText).IsEqualTo(currentBefore);

            if (!deletionOp) continue;

            // I1: exactly one message; plain for a Backspace, Ctrl alone for Word-Backspace.
            var sent = target.KeyMessages.Skip(messagesBefore).ToList();
            Check.WithCustomMessage(Fail("a deletion must send exactly one key message")).That(sent.Count).IsEqualTo(1);
            Check.That(sent[0].Key).IsEqualTo("\b");
            var mods = sent[0].Modifiers ?? new HashSet<ModifierKey>();
            if (op is Op.Backspace or Op.ModifiedBackspace)
            {
                Check.WithCustomMessage(Fail("Backspace must be sent PLAIN, whatever modifier is armed")).That(mods.Count).IsEqualTo(0);
                Check.WithCustomMessage(Fail("I1: Backspace must delete exactly one visible character"))
                    .That(target.LastDeletedCount).IsEqualTo(caretBefore > 0 ? 1 : 0);
            }
            else
            {
                Check.WithCustomMessage(Fail("Word-Backspace must be exactly Ctrl+Backspace"))
                    .That(mods.Count == 1 && mods.Contains(ModifierKey.Ctrl)).IsTrue();
            }

            // I2.
            var run = TrailingRunStart(mirror);
            Check.WithCustomMessage(Fail("I2: CurrentInput is not the trailing letter/digit run of the mirror"))
                .That(vm.CurrentInput).IsEqualTo(mirror[run..]);
            Check.WithCustomMessage(Fail("I2: _recentText is not everything before the CurrentInput run"))
                .That(vm.RecentText).IsEqualTo(mirror[..run]);
            Check.WithCustomMessage(Fail("I2: _previousWord is not the last letter/digit run before the CurrentInput"))
                .That(CoreOf(vm.PreviousWord)).IsEqualTo(LastRun(vm.RecentText));
        }
    }

    private static string Apply(Op op, Random rng, KeyboardViewModel vm, SimulatedTextTarget target)
    {
        switch (op)
        {
            case Op.Letter:
                var letter = Letters[rng.Next(Letters.Length)].ToString();
                vm.ActivateKey(letter, null);
                return $"letter {letter}";
            case Op.Digit:
                var digit = Digits[rng.Next(Digits.Length)];
                vm.ActivateKey(digit, null);
                return $"digit {digit}";
            case Op.Punctuation:
                var p = Punctuation[rng.Next(Punctuation.Length)];
                vm.ActivateKey(p, null);
                return $"punct {p}";
            case Op.Emoji:
                var e = Emoji[rng.Next(Emoji.Length)];
                vm.ActivateKey(e, null);
                return $"emoji {e}";
            case Op.Space:
                vm.HandleSpace();
                return "space";
            case Op.DoubleSpace:
                vm.HandleSpace();
                vm.HandleSpace();
                return "double-space";
            case Op.Backspace:
                vm.HandleBackspace();
                return "backspace";
            case Op.WordBackspace:
                vm.HandleDeleteWord();
                return "word-backspace";
            case Op.Pick:
                var word = PickWords[rng.Next(PickWords.Length)];
                vm.HandlePredictionSelected(word);
                return $"pick {word}";
            case Op.Enter:
                vm.HandleEnter();
                return "enter";
            case Op.HideShow:
                vm.IsVisible = false;
                // Focus may or may not have moved while the keyboard was away.
                if (rng.Next(2) == 0)
                    target.FocusMovedToForeignText(Foreign[rng.Next(Foreign.Length)]);
                else
                    target.MarkAllForeign();
                vm.IsVisible = true;
                return "hide/show";
            case Op.Arrow:
                var key = new[] { "arrowleft", "arrowright", "home", "end", "arrowup" }[rng.Next(5)];
                vm.HandleSpecialKey(key);
                return $"key {key}";
            case Op.CtrlV:
                vm.HandleShortcut("Ctrl+V");
                return "Ctrl+V";
            case Op.CtrlZ:
                vm.HandleShortcut("Ctrl+Z");
                return "Ctrl+Z";
            case Op.ModifiedBackspace:
                var armed = Arm(rng, vm);
                vm.HandleBackspace();
                return $"{armed}+backspace";
            case Op.ModifiedWordBackspace:
                var armed2 = Arm(rng, vm);
                vm.HandleDeleteWord();
                return $"{armed2}+word-backspace";
            case Op.Tab:
                vm.HandleTab();
                return "tab";
            case Op.DeleteKey:
                vm.HandleSpecialKey("delete");
                return "key delete";
            case Op.PrintScreen:
                vm.HandleSpecialKey("printscreen");
                return "key printscreen";
            default:
                vm.HandleShortcut("Ctrl+C");
                return "Ctrl+C";
        }
    }

    private static string Arm(Random rng, KeyboardViewModel vm)
    {
        var mod = new[] { ModifierKey.Ctrl, ModifierKey.Alt, ModifierKey.Win, ModifierKey.Shift }[rng.Next(4)];
        vm.ToggleModifier(mod);
        return mod.ToString();
    }

    private static Op Pick(Random rng, (Op Op, int Weight)[] mix, int total)
    {
        var roll = rng.Next(total);
        foreach (var (op, weight) in mix)
        {
            if (roll < weight) return op;
            roll -= weight;
        }
        return mix[^1].Op;
    }

    private static int TrailingRunStart(string text)
    {
        var start = text.Length;
        while (start > 0 && char.IsLetterOrDigit(text[start - 1])) start--;
        return start;
    }

    /// <summary>The last letter/digit run of the text, ignoring trailing separators; null when there is none.</summary>
    private static string? LastRun(string text)
    {
        var end = text.Length;
        while (end > 0 && !char.IsLetterOrDigit(text[end - 1])) end--;
        if (end == 0) return null;
        var start = end;
        while (start > 0 && char.IsLetterOrDigit(text[start - 1])) start--;
        return text[start..end];
    }

    /// <summary>The previous word without the punctuation attached after it ("Ende." → "Ende").</summary>
    private static string? CoreOf(string? previousWord) => previousWord is null ? null : LastRun(previousWord);

    private static string Show(string? s) => s is null ? "null" : $"\"{s}\"";
}
