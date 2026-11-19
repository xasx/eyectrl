namespace EyeCtrl.Core.Tests.Settings;

using System.Text.Json.Nodes;
using EyeCtrl.Core.Settings;
using Xunit;

/// <summary>
/// The note body text size default (R3): 36 DIP, the owner's pick (19.11), after sitting 9, from
/// the comparison of 28/36/40 DIP at SemiBold. These are PINS: they assert the literal 36 so a
/// revert to the old provisional 28 goes RED. The clamp tests next door
/// (<see cref="SettingsServiceNoteTextSizeClampTests"/>) read the constant and would otherwise
/// follow it silently.
/// </summary>
public class NoteSettingsDefaultTextSizeTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"eyectrl_note_default_{Guid.NewGuid()}.json");

    // Box-wide named-kernel-object rule (AGENTS.md): tests that persist never take the
    // production writer mutex - each constructs its service with a GUID-suffixed name.
    private static string TestMutexName() =>
        @"Local\EyeCtrl.Settings.Writer.Test." + Guid.NewGuid().ToString("N");

    /// <summary>Saves a fresh settings file, lets <paramref name="edit"/> rewrite the parsed
    /// root, then loads it back through the same service.</summary>
    private static EyeCtrlSettings LoadAfterEditing(Action<JsonObject> edit)
    {
        var path = TempPath();
        var settings = new EyeCtrlSettings();
        settings.Dwell.ActivationButtonDwellMs = 600;
        var service = new SettingsService(path, SettingsWriter.App, TestMutexName());
        service.Save(settings);

        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(root);
        File.WriteAllText(path, root.ToJsonString());

        try
        {
            return service.Load();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheDefaultTextSizeConstant_Is36Dip()
    {
        Check.That(NoteSettings.DefaultTextSizeDip).IsEqualTo(36);
    }

    [Fact]
    public void ANewNoteSettings_StartsAt36Dip()
    {
        Check.That(new NoteSettings().TextSizeDip).IsEqualTo(36);
    }

    [Fact]
    public void ANoteSectionWithoutTextSizeDip_Loads36()
    {
        var loaded = LoadAfterEditing(root => ((JsonObject)root["note"]!).Remove("textSizeDip"));

        Check.That(loaded.Note.TextSizeDip).IsEqualTo(36);
        Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(600);
    }

    [Fact]
    public void AFileWithoutANoteSection_Loads36()
    {
        var loaded = LoadAfterEditing(root => root.Remove("note"));

        Check.That(loaded.Note.TextSizeDip).IsEqualTo(36);
        Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(600);
    }

    [Fact]
    public void AStoredTextSizeOf28_StillLoads28()
    {
        // A user's explicit value wins over the new default: the owner's own settings.json
        // stores 28 until the owner sets 36 in Settings, and this default must not move it.
        var loaded = LoadAfterEditing(root => root["note"] = new JsonObject { ["textSizeDip"] = 28 });

        Check.That(loaded.Note.TextSizeDip).IsEqualTo(28);
        Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(600);
    }
}
