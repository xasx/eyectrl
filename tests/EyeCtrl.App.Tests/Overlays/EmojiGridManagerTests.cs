namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Keyboard;

public class EmojiGridManagerTests
{
    /// <summary>
    /// What the manager handed the registrar for one dwell target. The registrar is the real
    /// <c>KeyboardWindow.RegisterKeyVisual</c> in production, and the content is what its outline
    /// wraps: passing <see langword="null"/> leaves the glyph out of the tree entirely, while
    /// parenting the content on the Border first makes the outline's Attach throw.
    /// </summary>
    private sealed record Registration(string Id, KeyDefinition Key, Border Container, TextBlock? Content);

    private static (EmojiGridManager Manager, List<Registration> Registrations) NewManager()
    {
        var registrations = new List<Registration>();
        var manager = new EmojiGridManager(
            keyRows: new StackPanel(),
            categoryBar: new StackPanel(),
            registerKeyVisual: (id, key, container, content) =>
                registrations.Add(new Registration(id, key, container, content)),
            getKeySize: () => 70,
            clearKeyState: () => { });
        return (manager, registrations);
    }

    private static string EmojiJsonPath() =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Emojis", "emojis.json");

    private static string TempRecentPath() =>
        Path.Combine(Path.GetTempPath(), "eyectrl_emoji_grid_test_" + Guid.NewGuid().ToString("N") + ".json");

    [StaFact]
    public void SetSkinToneAction_RaisesSkinToneChanged()
    {
        var (manager, _) = NewManager();
        int? raised = null;
        manager.SkinToneChanged += tone => raised = tone;
        manager.HandleAction("setSkinTone:3");
        Check.That(raised).IsEqualTo(3);
        Check.That(manager.SkinTone).IsEqualTo(3);
    }

    [StaFact]
    public void SetSkinToneAction_ClampsOutOfRange()
    {
        var (manager, _) = NewManager();
        int? raised = null;
        manager.SkinToneChanged += tone => raised = tone;
        manager.HandleAction("setSkinTone:9");
        Check.That(raised).IsEqualTo(5);
    }

    [StaFact]
    public void SetSkinTone_ProgrammaticInitialization_DoesNotRaiseEvent()
    {
        var (manager, _) = NewManager();
        var raised = false;
        manager.SkinToneChanged += _ => raised = true;
        manager.SetSkinTone(2);
        Check.That(raised).IsFalse();
    }

    /// <summary>
    /// Every grid surface must hand its content to the registrar unwrapped. With the outline
    /// attached inside <c>RegisterKeyVisual</c>, the 4th argument IS the cell's glyph: <c>null</c>
    /// installs an empty outline and the emoji vanishes from a grid that still accumulates dwell,
    /// and leaving the Border's own Child set makes Attach throw on an already-parented element.
    /// </summary>
    [StaFact]
    public void Enter_HandsEachGridSurfaceItsContent_WithoutParentingIt()
    {
        var (manager, registrations) = NewManager();
        manager.SetEmojiData(EmojiJsonPath(), TempRecentPath());

        manager.Enter();

        Check.WithCustomMessage(
                "The emoji grid registered no dwell targets at all — the shipped emoji data did not "
                + "load, so every assertion below would pass vacuously.")
            .That(registrations).Not.IsEmpty();

        var cells = registrations.Where(r => r.Id.StartsWith("emoji_")).ToList();
        Check.WithCustomMessage("No emoji cell was registered.").That(cells).Not.IsEmpty();
        foreach (var cell in cells)
        {
            Check.WithCustomMessage($"{cell.Id} handed the registrar no content, so the outline "
                    + "would wrap nothing and the glyph would silently vanish.")
                .That(cell.Content).IsInstanceOf<Emoji.Wpf.TextBlock>();
            Check.WithCustomMessage($"{cell.Id} handed over its glyph but also parented it on the "
                    + "cell Border, so the registrar's outline Attach would throw.")
                .That(cell.Container.Child).IsNull();
        }

        var specials = registrations.Where(r => r.Id.StartsWith("special_")).ToList();
        Check.WithCustomMessage("No special button (ABC / skin tone / paging) was registered.")
            .That(specials).Not.IsEmpty();
        foreach (var special in specials)
        {
            Check.WithCustomMessage($"{special.Id} handed the registrar no content, so its label "
                    + "would silently vanish under the outline.")
                .That(special.Content).IsNotNull();
            Check.That(special.Container.Child).IsNull();
        }
    }

    [StaFact]
    public void ShowSkinToneSelector_HandsEachSwatchItsContent_WithoutParentingIt()
    {
        var (manager, registrations) = NewManager();
        manager.SetEmojiData(EmojiJsonPath(), TempRecentPath());
        manager.Enter();
        registrations.Clear();

        manager.HandleAction("skinTone");

        var swatches = registrations.Where(r => r.Id.StartsWith("skin_")).ToList();
        Check.WithCustomMessage(
                "Expected the six skin-tone swatches; the selector did not build or the emoji data "
                + "did not load.")
            .That(swatches).HasSize(6);
        foreach (var swatch in swatches)
        {
            Check.WithCustomMessage($"{swatch.Id} handed the registrar no content, so the preview "
                    + "emoji would silently vanish under the outline.")
                .That(swatch.Content).IsInstanceOf<Emoji.Wpf.TextBlock>();
            Check.That(swatch.Container.Child).IsNull();
        }
    }
}
