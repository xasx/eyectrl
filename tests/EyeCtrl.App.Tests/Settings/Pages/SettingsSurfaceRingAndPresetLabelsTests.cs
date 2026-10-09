namespace EyeCtrl.App.Tests.Settings.Pages;

using System;
using System.Collections.Generic;
using System.Linq;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.ActionMenu;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;

/// <summary>
/// T07 (D3): the Settings preview and picker speak the <c>settings</c> surface's keys, and their
/// English must stay word for word the live ring's and the grid's — the mirror pair only keeps
/// the two surfaces from diverging; this pins the English side of that pair headlessly, since
/// nothing else can construct the settings window to read it.
/// </summary>
public class SettingsSurfaceRingAndPresetLabelsTests
{
    [Fact]
    public void PreviewRingLabels_MatchTheLiveRingsEnglish()
    {
        // The preview's action names, Back and More must read exactly as the live ring's English, or
        // the miniature lies about what the live ring says. Since T08, RingLabelMap returns the
        // overlays KEYS, so the live ring's English is its embedded-English source.
        var live = RingLabelSource.English;
        foreach (var action in Enum.GetValues<InteractionAction>())
        {
            Check.WithCustomMessage(
                    $"the settings preview names {action} \"{ActionMenuPageSource.RingLabelFor(action)}\" "
                    + $"but the live ring says \"{live.ActionLabel(action)}\".")
                .That(ActionMenuPageSource.RingLabelFor(action)).IsEqualTo(live.ActionLabel(action));
        }

        Check.That(ActionMenuPageSource.RingBackLabel()).IsEqualTo(live.BackLabel);
        Check.That(ActionMenuPageSource.RingMoreLabel()).IsEqualTo(live.MoreLabel);
        Check.That(ActionMenuPageSource.RingMoreLabel()).IsEqualTo("More");
    }

    [Fact]
    public void EveryPresetId_ResolvesItsSettingsKeyToThePresetEnglish()
    {
        var presets = QuickActionPresets.KeyboardShortcuts
            .Concat(QuickActionPresets.MediaControls)
            .Concat(QuickActionPresets.WindowActions)
            .ToList();

        // The count guard: a preset list that shrank would silently stop covering the table.
        Check.That(presets).HasSize(24);

        foreach (var preset in presets)
        {
            Check.WithCustomMessage(
                    $"preset \"{preset.Id}\" does not resolve through a settings key — its picker "
                    + "label silently falls back to the stored English, or the key's text drifted "
                    + "from the preset's English (English on screen must not change).")
                .That(QuickActionsPageSource.PresetLabelFor(preset.Id)).IsEqualTo(preset.Label);
        }
    }

    [Fact]
    public void ClearLearnedWords_KeepsTheEnglishWordsForEveryCount()
    {
        // The key is a real {one, other} plural so German can inflect, but English on screen must
        // not change: today's sentence says "words" at every count, including 1, so BOTH forms
        // carry it. Pinned through the resolver, which also proves the count picks the one form.
        var table = StringTable.EmbeddedEnglish;
        Check.That(table.T("settings.keyboard.dictionary.clear.message", 1))
            .IsEqualTo("Remove all 1 learned words?");
        Check.That(table.T("settings.keyboard.dictionary.clear.message", 2))
            .IsEqualTo("Remove all 2 learned words?");
    }
}
