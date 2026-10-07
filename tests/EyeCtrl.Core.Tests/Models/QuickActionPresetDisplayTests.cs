namespace EyeCtrl.Core.Tests.Models;

using EyeCtrl.Core.Models;

/// <summary>
/// The preset identity seam (D2): a preset tile follows the host's label function, a user-named
/// tile keeps its stored text, and the one-time match fills an empty <see cref="QuickAction.PresetId"/>
/// only on an exact (type, payload, English label) hit.
/// </summary>
public class QuickActionPresetDisplayTests
{
    private static QuickAction PresetTile(string presetId = "kbd.copy", string storedLabel = "Copy") => new()
    {
        Type = QuickActionType.KeyboardShortcut,
        Payload = "Ctrl+C",
        Label = storedLabel,
        PresetId = presetId,
    };

    [Fact]
    public void PresetTile_ShowsTheHostsLabel()
    {
        var shown = QuickActionPresets.DisplayLabel(PresetTile(), _ => "Kopieren");

        Check.That(shown).IsEqualTo("Kopieren");
    }

    [Fact]
    public void PresetTile_UnknownPresetId_FallsBackToTheStoredLabel()
    {
        var shown = QuickActionPresets.DisplayLabel(PresetTile("kbd.gone"), _ => null);

        Check.That(shown).IsEqualTo("Copy");
    }

    [Fact]
    public void UserNamedTile_ShowsItsStoredLabel_AndNeverAsksTheHost()
    {
        var tile = new QuickAction { Type = QuickActionType.CannedText, Label = "Mein Text", Payload = "hi" };

        var shown = QuickActionPresets.DisplayLabel(tile,
            _ => throw new InvalidOperationException("a user-named tile must not consult the preset map"));

        Check.That(shown).IsEqualTo("Mein Text");
    }

    [Fact]
    public void PickerRow_ShowsTheHostsLabel_OrItsEnglishTableLabel()
    {
        var preset = QuickActionPresets.KeyboardShortcuts[0];

        Check.That(QuickActionPresets.DisplayLabel(preset, _ => "Kopieren")).IsEqualTo("Kopieren");
        Check.That(QuickActionPresets.DisplayLabel(preset, _ => null)).IsEqualTo("Copy");
    }

    [Fact]
    public void EnglishLabelFor_NamesKnownIds_AndNullForUnknownOnes()
    {
        Check.That(QuickActionPresets.EnglishLabelFor("kbd.copy")).IsEqualTo("Copy");
        Check.That(QuickActionPresets.ById("media.play_pause")!.Label).IsEqualTo("Play / Pause");
        Check.That(QuickActionPresets.EnglishLabelFor("kbd.gone")).IsNull();
        Check.That(QuickActionPresets.ById("kbd.gone")).IsNull();
    }

    [Fact]
    public void TryAssignPresetId_MatchesOnTypePayloadAndEnglishLabel()
    {
        var tile = new QuickAction { Type = QuickActionType.KeyboardShortcut, Label = "Copy", Payload = "Ctrl+C" };

        Check.That(QuickActionPresets.TryAssignPresetId(tile)).IsTrue();

        Check.That(tile.PresetId).IsEqualTo("kbd.copy");
    }

    [Fact]
    public void TryAssignPresetId_AssignsAnIdExactlyOnce()
    {
        var tile = new QuickAction { Type = QuickActionType.KeyboardShortcut, Label = "Copy", Payload = "Ctrl+C" };

        Check.That(QuickActionPresets.TryAssignPresetId(tile)).IsTrue();
        Check.That(QuickActionPresets.TryAssignPresetId(tile)).IsFalse();
        Check.That(tile.PresetId).IsEqualTo("kbd.copy");
    }

    [Fact]
    public void TryAssignPresetId_LeavesNonMatchingTilesAlone()
    {
        // Three ways to miss: the label drifted (user renamed), the payload differs, the type differs.
        var renamed = new QuickAction { Type = QuickActionType.KeyboardShortcut, Label = "Mein Kopieren", Payload = "Ctrl+C" };
        var otherPayload = new QuickAction { Type = QuickActionType.KeyboardShortcut, Label = "Copy", Payload = "Ctrl+Alt+C" };
        var otherType = new QuickAction { Type = QuickActionType.CannedText, Label = "Copy", Payload = "Ctrl+C" };

        Check.That(QuickActionPresets.TryAssignPresetId(renamed)).IsFalse();
        Check.That(QuickActionPresets.TryAssignPresetId(otherPayload)).IsFalse();
        Check.That(QuickActionPresets.TryAssignPresetId(otherType)).IsFalse();

        Check.That(renamed.PresetId).IsNull();
        Check.That(otherPayload.PresetId).IsNull();
        Check.That(otherType.PresetId).IsNull();
    }

    [Fact]
    public void TryAssignPresetId_NeverOverwritesAnExistingId()
    {
        var tile = PresetTile("media.mute");

        Check.That(QuickActionPresets.TryAssignPresetId(tile)).IsFalse();
        Check.That(tile.PresetId).IsEqualTo("media.mute");
    }

    [Fact]
    public void EveryPreset_RoundTripsThroughTheMatch()
    {
        var tables = new (QuickActionType Type, IReadOnlyList<QuickActionPreset> Presets)[]
        {
            (QuickActionType.KeyboardShortcut, QuickActionPresets.KeyboardShortcuts),
            (QuickActionType.MediaControl, QuickActionPresets.MediaControls),
            (QuickActionType.WindowAction, QuickActionPresets.WindowActions),
        };

        foreach (var (type, presets) in tables)
        {
            foreach (var preset in presets)
            {
                var tile = new QuickAction { Type = type, Label = preset.Label, Payload = preset.Payload };

                Check.WithCustomMessage($"{preset.Id} must match its own table entry")
                     .That(QuickActionPresets.TryAssignPresetId(tile)).IsTrue();
                Check.That(tile.PresetId).IsEqualTo(preset.Id);
            }
        }
    }
}
