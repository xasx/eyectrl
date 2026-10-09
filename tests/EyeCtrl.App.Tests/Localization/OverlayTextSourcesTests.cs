namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Collections.Generic;
using System.Linq;
using EyeCtrl.App.Localization;
using EyeCtrl.Core.Models;

/// <summary>
/// T08 fixup R1 (mutant M3): the overlays-side preset map is hand-written against
/// <see cref="QuickActionPresets"/>' tables, so nothing else notices a missing case or a swapped
/// key — a new preset would silently fall back to its stored English. The theory walks every
/// preset and compares the mapped English to the table's own label, which kills one-way and
/// two-way swaps alike; the unknown-id case pins the deliberate null fallback.
/// </summary>
public class OverlayTextSourcesTests
{
    public static TheoryData<string, string> Presets()
    {
        var data = new TheoryData<string, string>();
        foreach (var preset in QuickActionPresets.KeyboardShortcuts
                     .Concat(QuickActionPresets.MediaControls)
                     .Concat(QuickActionPresets.WindowActions))
        {
            data.Add(preset.Id, preset.Label);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void EveryPreset_HasItsOverlaysEnglishLabel(string presetId, string englishLabel)
    {
        Check.WithCustomMessage($"preset '{presetId}'")
             .That(OverlayTextSources.PresetLabelFor(presetId)).IsEqualTo(englishLabel);
    }

    [Fact]
    public void AnUnknownPresetId_FallsBackToNull_SoTheStoredLabelShows()
    {
        Check.That(OverlayTextSources.PresetLabelFor("kbd.gone")).IsNull();
    }
}
