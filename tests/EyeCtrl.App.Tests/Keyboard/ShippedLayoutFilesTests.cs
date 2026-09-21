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
}
