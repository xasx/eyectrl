namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Automation;
using System.Windows.Controls;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Localization;

/// <summary>
/// The emoji skin-tone selector's back key (holistic review F4): it drew a hard-coded English
/// "Back" under every language. It now draws the language-free ◀ (the same glyph as the emoji
/// page-back key) and speaks its name from the <c>overlays</c> surface, so a German overlay
/// announces "Zurück". Exclusive with the other table-switching tests: it swaps the process table.
/// </summary>
[Collection("StringTable Tests (App)")]
public class EmojiSkinToneBackTests
{
    private sealed record Registration(string Id, KeyDefinition Key, Border Container, TextBlock? Content);

    private static List<Registration> ShowSkinToneSelector()
    {
        var registrations = new List<Registration>();
        var manager = new EmojiGridManager(
            keyRows: new StackPanel(),
            categoryBar: new StackPanel(),
            registerKeyVisual: (id, key, container, content) =>
                registrations.Add(new Registration(id, key, container, content)),
            getKeySize: () => 70,
            clearKeyState: () => { });
        manager.SetEmojiData(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Emojis", "emojis.json"),
            Path.Combine(Path.GetTempPath(), "eyectrl_t15_emoji_" + Guid.NewGuid().ToString("N") + ".json"));
        manager.Enter();
        registrations.Clear();

        manager.HandleAction("skinTone");
        return registrations;
    }

    private static Registration BackKey(List<Registration> registrations)
    {
        Check.WithCustomMessage("The skin-tone selector registered no swatches: the emoji data did not load.")
            .That(registrations.Count(r => r.Id.StartsWith("skin_", StringComparison.Ordinal))).IsEqualTo(6);
        return registrations.Single(r => r.Id == "special_skinToneBack");
    }

    [StaFact]
    public void TheBackKey_DrawsTheGlyph_AndSpeaksItsNameInEnglish()
    {
        StringTable.ResetToEmbeddedEnglish();
        var back = BackKey(ShowSkinToneSelector());

        Check.That(back.Content!.Text).IsEqualTo("\u25C0");
        Check.That(AutomationProperties.GetName(back.Content)).IsEqualTo("Back");
        Check.That(back.Key.SpecialAction).IsEqualTo("skinToneBack");
    }

    [StaFact]
    public void TheBackKey_DrawsTheSameGlyph_AndSpeaksGerman_UnderTheShippedGerman()
    {
        try
        {
            ShippedLanguageRecoveryTests.ActivateShippedGerman();
            var back = BackKey(ShowSkinToneSelector());

            // The caption carries no language; only the spoken name does. A hard-coded "Back"
            // (or a caption equal to the English name) would show English on this German overlay.
            Check.That(back.Content!.Text).IsEqualTo("\u25C0");
            Check.That(AutomationProperties.GetName(back.Content)).IsEqualTo("Zurück");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [StaFact]
    public void TheBackKey_KeepsItsSizeAndItsDwellTargetId()
    {
        StringTable.ResetToEmbeddedEnglish();
        var back = BackKey(ShowSkinToneSelector());

        // keySize 70 × 2.0 + the 4 DIP the 2.0-wide specials add; unchanged by the glyph.
        Check.That(back.Container.Width).IsEqualTo(70 * 2.0 + 4);
        Check.That(back.Container.Height).IsEqualTo(70);
        Check.That(back.Id).IsEqualTo("special_skinToneBack");
    }
}
