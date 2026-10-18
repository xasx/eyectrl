namespace EyeCtrl.Core.Tests.Settings;

using System.Text.Json.Nodes;
using EyeCtrl.Core.Settings;
using Xunit;

/// <summary>
/// The note text-size clamp (P4 D1): a settings file with textSizeDip outside 18-48 loads at the
/// CLAMPED value - not at the default 28 the #153 setter-throw revert would answer with - and a
/// duplicated key loads without error because the clamp step runs AFTER NormalizeDuplicateProperties
/// (its JsonNode indexer walk throws ArgumentException on a duplicate otherwise).
/// </summary>
public class SettingsServiceNoteTextSizeClampTests
{
    private string TempPath() => Path.Combine(Path.GetTempPath(), $"eyectrl_note_{Guid.NewGuid()}.json");

    private EyeCtrlSettings LoadWith(int textSize, bool duplicate = false)
    {
        var path = TempPath();
        var settings = new EyeCtrlSettings();
        settings.Dwell.ActivationButtonDwellMs = 600;
        settings.Cursor.SmoothingFactor = 0.7;
        var service = new SettingsService(path);
        service.Save(settings);

        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["note"] = new JsonObject { ["textSizeDip"] = textSize };
        var text = root.ToJsonString();
        if (duplicate)
        {
            // The duplicated key must survive serialization (a JsonObject cannot hold it), so
            // the duplicate is written as raw text: the exact hand-edit the normalizer repairs.
            var entry = "\"textSizeDip\":" + textSize;
            text = text.Replace(entry, entry + ", " + entry);
        }
        File.WriteAllText(path, text);

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
    public void ADuplicatedTextSizeKey_LoadsWithoutError_AtTheLastValue()
    {
        var loaded = LoadWith(35, duplicate: true);
        Check.That(loaded.Note.TextSizeDip).IsEqualTo(35);
        Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(600);
    }

    [Fact]
    public void AnInLegalSize_RidesThroughUnchanged()
    {
        Check.That(LoadWith(28).Note.TextSizeDip).IsEqualTo(28);
        Check.That(LoadWith(48).Note.TextSizeDip).IsEqualTo(48);
    }
}
