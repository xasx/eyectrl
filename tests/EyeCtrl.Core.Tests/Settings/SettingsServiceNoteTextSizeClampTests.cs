namespace EyeCtrl.Core.Tests.Settings;

using System.Text.Json.Nodes;
using EyeCtrl.Core.Settings;
using Xunit;

/// <summary>
/// The note text-size clamp (P4 D1): a settings file with textSizeDip outside 18-48 loads at
/// the CLAMPED value - not at the default 28 the #153 setter-throw revert would answer with -
/// and duplicated keys load without error because the clamp step runs AFTER
/// NormalizeDuplicateProperties. Review-01 M2: JsonNode.Parse throws ArgumentException the
/// moment a duplicated key is indexed, so a clamp running BEFORE the normalizer fail-safes to
/// its input and the STILL-illegal value reaches the throwing setter, whose revert answers 28.
/// The duplicate fixtures build REAL duplicates at byte level (the first version of this test
/// Replace-d the SERIALIZED text and silently built no duplicate at all).
/// </summary>
public class SettingsServiceNoteTextSizeClampTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"eyectrl_note_{Guid.NewGuid()}.json");

    private EyeCtrlSettings LoadWith(int textSize)
    {
        var path = TempPath();
        var settings = new EyeCtrlSettings();
        settings.Dwell.ActivationButtonDwellMs = 600;
        settings.Cursor.SmoothingFactor = 0.7;
        var service = new SettingsService(path);
        service.Save(settings);

        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["note"] = new JsonObject { ["textSizeDip"] = textSize };
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

    /// <summary>Builds a raw settings file whose root or note section carries REAL duplicated
    /// keys: the saved root minus its note section, compacted, with the prefix spliced in at
    /// byte level (the reviewer probe shape).</summary>
    private static string WithDuplicate(string prefix)
    {
        var path = TempPath();
        var settings = new EyeCtrlSettings();
        settings.Dwell.ActivationButtonDwellMs = 600;
        new SettingsService(path).Save(settings);
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root.Remove("note");
        var json = prefix + root.ToJsonString().Substring(1);
        File.Delete(path);
        return json;
    }

    private static EyeCtrlSettings LoadRaw(string json)
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, json);
            return new SettingsService(path).Load();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AFileWithTextSize17_LoadsAs18_NotAsTheDefault28()
    {
        var loaded = LoadWith(17);
        Check.That(loaded.Note.TextSizeDip).IsEqualTo(18);
        Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(600);
        Check.That(loaded.Cursor.SmoothingFactor).IsEqualTo(0.7);
    }

    [Fact]
    public void AFileWithTextSize49_LoadsAs48()
    {
        Check.That(LoadWith(49).Note.TextSizeDip).IsEqualTo(48);
    }

    [Fact]
    public void AFileWithTextSizeNegative_LoadsAs18()
    {
        Check.That(LoadWith(-5).Note.TextSizeDip).IsEqualTo(18);
    }

    [Fact]
    public void AnInLegalSize_RidesThroughUnchanged()
    {
        Check.That(LoadWith(28).Note.TextSizeDip).IsEqualTo(28);
        Check.That(LoadWith(48).Note.TextSizeDip).IsEqualTo(48);
    }

    [Fact]
    public void ADuplicatedTextSize_17Then49_Loads48_Not28()
    {
        var loaded = LoadRaw(WithDuplicate("{\"note\":{\"textSizeDip\":17,\"textSizeDip\":49},"));
        Check.That(loaded.Note.TextSizeDip).IsEqualTo(48);
        Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(600);
    }

    [Fact]
    public void ADuplicatedTextSize_30Then17_Loads18_Not28()
    {
        var loaded = LoadRaw(WithDuplicate("{\"note\":{\"textSizeDip\":30,\"textSizeDip\":17},"));
        Check.That(loaded.Note.TextSizeDip).IsEqualTo(18);
    }

    [Fact]
    public void ADuplicatedRootKey_WithTextSize17_Loads18_Not28()
    {
        var loaded = LoadRaw(WithDuplicate("{\"zzDup\":1,\"zzDup\":2,\"note\":{\"textSizeDip\":17},"));
        Check.That(loaded.Note.TextSizeDip).IsEqualTo(18);
        Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(600);
    }

    [Fact]
    public void ADuplicatedKeyInsideTheNoteSection_WithTextSize17_Loads18_Not28()
    {
        var loaded = LoadRaw(WithDuplicate("{\"note\":{\"textSizeDip\":17,\"legacy\":1,\"legacy\":2},"));
        Check.That(loaded.Note.TextSizeDip).IsEqualTo(18);
        Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(600);
    }
}
