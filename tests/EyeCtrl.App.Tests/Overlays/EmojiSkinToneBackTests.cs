namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Shapes;
using Path = System.IO.Path;
using ArrowPath = System.Windows.Shapes.Path;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Localization;

/// <summary>
/// The emoji skin-tone selector's back key (holistic review F4): it drew a hard-coded English
/// "Back" under every language. It now draws the keyboard's own left arrow (T14's drawn arrows, a
/// third of the key's width, in the keyboard's stroke) and speaks its name from the
/// <c>overlays</c> surface, so a German overlay announces "Zurück". Exclusive with the other
/// table-switching tests: it swaps the process table.
/// </summary>
[Collection("StringTable Tests (App)")]
public class EmojiSkinToneBackTests
{
    private sealed record Registration(
        string Id, KeyDefinition Key, Border Container, TextBlock? Label, FrameworkElement? Caption);

    private static List<Registration> ShowSkinToneSelector(double keySize = 70)
    {
        var registrations = new List<Registration>();
        var manager = new EmojiGridManager(
            keyRows: new StackPanel(),
            categoryBar: new StackPanel(),
            registerKeyVisual: (id, key, container, label, caption) =>
                registrations.Add(new Registration(id, key, container, label, caption)),
            getKeySize: () => keySize,
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

    /// <summary>The arrow's ink box: the canvas the frame draws in, inside the caption holder.</summary>
    private static Canvas ArrowCanvas(Registration back) =>
        ((Grid)back.Caption!).Children.OfType<Canvas>().Single();

    [StaFact]
    public void TheBackKey_DrawsTheKeyboardsLeftArrow_AndSpeaksItsNameInEnglish()
    {
        StringTable.ResetToEmbeddedEnglish();
        var back = BackKey(ShowSkinToneSelector());

        // The label is the invisible automation anchor; the caption the outline wraps is the drawing.
        Check.That(back.Label!.Opacity).IsEqualTo(0.0);
        Check.That(AutomationProperties.GetName(back.Label)).IsEqualTo("Back");
        Check.That(back.Key.SpecialAction).IsEqualTo("skinToneBack");
        Check.That(back.Key.LabelFrame).IsEqualTo(ArrowLabelFrame.CursorLeft);
        Check.That(back.Caption).IsInstanceOf<Grid>();
        Check.That(ArrowCanvas(back).Children.OfType<ArrowPath>().Single().Data).IsNotNull();
    }

    [StaFact]
    public void TheBackKey_DrawsTheSameArrow_AndSpeaksGerman_UnderTheShippedGerman()
    {
        try
        {
            ShippedLanguageRecoveryTests.ActivateShippedGerman();
            var back = BackKey(ShowSkinToneSelector());

            // The drawing carries no language; only the spoken name does. A hard-coded "Back"
            // (or a caption equal to the English name) would show English on this German overlay.
            Check.That(AutomationProperties.GetName(back.Label!)).IsEqualTo("Zurück");
            Check.That(ArrowCanvas(back).Children.OfType<ArrowPath>()).HasSize(1);
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// "Bigger, like T14": the arrow is a third of the key's width (the keyboard's wide arrows are a
    /// third of THEIR keys), at every key size, and sits inside the key's height.
    /// </summary>
    [StaTheory]
    [InlineData(35.0)]
    [InlineData(70.0)]
    [InlineData(105.0)]
    public void TheArrow_IsAThirdOfTheKeysWidth_AndFitsTheKey(double keySize)
    {
        StringTable.ResetToEmbeddedEnglish();
        var back = BackKey(ShowSkinToneSelector(keySize));
        var canvas = ArrowCanvas(back);

        var boxWidth = keySize * 2.0 + 4;
        Check.That(back.Container.Width).IsEqualTo(boxWidth);
        Check.That(canvas.Width).IsCloseTo(boxWidth / 3, 1e-9);
        Check.WithCustomMessage("the arrow must lie inside the key").That(
            canvas.Height < keySize && canvas.Width < boxWidth).IsTrue();
        // The shaft-and-head left arrow is wider than tall (the keyboard's ← is 1 : 0.48).
        Check.That(canvas.Height).IsCloseTo(canvas.Width * 2 * ArrowLabelFrame.EnterHeadHalf, 1e-9);
    }

    [StaFact]
    public void TheBackKey_KeepsItsSizeAndItsDwellTargetId()
    {
        StringTable.ResetToEmbeddedEnglish();
        var back = BackKey(ShowSkinToneSelector());

        // keySize 70 × 2.0 + the 4 DIP the 2.0-wide specials add; unchanged by the drawing.
        Check.That(back.Container.Width).IsEqualTo(70 * 2.0 + 4);
        Check.That(back.Container.Height).IsEqualTo(70);
        Check.That(back.Id).IsEqualTo("special_skinToneBack");
    }
}
