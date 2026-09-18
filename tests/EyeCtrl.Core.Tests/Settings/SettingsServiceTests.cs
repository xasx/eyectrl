namespace EyeCtrl.Core.Tests.Settings;
using System.Text.Json.Nodes;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public class SettingsServiceTests
{
    [Fact]
    public void SaveAndLoad_RoundTrips_AllSettings()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");
        try
        {
            var settings = new EyeCtrlSettings();
            settings.Dwell.ActivationButtonDwellMs = 600;
            settings.Cursor.SmoothingFactor = 0.7;
            var service = new SettingsService(tempPath);
            service.Save(settings);
            var loaded = service.Load();
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(600);
            Check.That(loaded.Cursor.SmoothingFactor).IsEqualTo(0.7);
        }
        finally { File.Delete(tempPath); }
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_ReturnsDefaults()
    {
        var service = new SettingsService("/nonexistent/path.json");
        var settings = service.Load();
        Check.That(settings).IsNotNull();
        Check.That(settings.Dwell.ActivationButtonDwellMs).IsEqualTo(700);
    }

    [Fact]
    public void Load_MalformedLanguagePack_IsSanitizedToEmpty_OtherSettingsSurvive()
    {
        // LanguagePack is a path ingredient (dictionary/trie reads, user-dictionary write): a
        // hand-edited "..\..\evil" must be reset to "" (auto-detect) WITHOUT tripping the
        // whole-file defaults wipe — that would punish one bad string by reverting every
        // configured value the user relies on.
        var tempPath = Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");
        try
        {
            var json = """
            {
                "keyboard": { "languagePack": "..\\..\\evil", "keySizeMultiplier": 1.5 },
                "dwell": { "activationButtonDwellMs": 800 }
            }
            """;
            File.WriteAllText(tempPath, json);
            var service = new SettingsService(tempPath);
            var settings = service.Load();

            Check.That(settings.Keyboard.LanguagePack).IsEqualTo("");
            Check.That(settings.Keyboard.KeySizeMultiplier).IsEqualTo(1.5);
            Check.That(settings.Dwell.ActivationButtonDwellMs).IsEqualTo(800);
        }
        finally { File.Delete(tempPath); }
    }

    [Fact]
    public void Load_MigratesDragStartToDrag_InVisibleActions()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");
        try
        {
            var json = """
            {
                "actionMenu": {
                    "visibleActions": ["leftClick", "rightClick", "doubleClick", "dragStart", "scrollUp", "scrollDown", "pinNote"]
                },
                "selectionMode": "ManualSelection",
                "dwell": { "activationButtonDwellMs": 800 }
            }
            """;
            File.WriteAllText(tempPath, json);
            var service = new SettingsService(tempPath);
            var settings = service.Load();

            Check.That(settings.ActionMenu.VisibleActions).Contains(InteractionAction.Drag);
            Check.That(settings.SelectionMode).IsEqualTo(SelectionMode.ManualSelection);
            Check.That(settings.Dwell.ActivationButtonDwellMs).IsEqualTo(800);
        }
        finally { File.Delete(tempPath); }
    }

    [Fact]
    public void Load_MigrationWriteBack_IsAtomic_AndLeavesNoTempFile()
    {
        // The migration rewrite happens during startup. A torn write there leaves the settings file
        // unparseable, and Load answers that with all-defaults — every configured value silently gone.
        // Temp file + rename is the guarantee; this pins its observable consequences.
        var tempPath = Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");
        try
        {
            var json = """
            {
                "actionMenu": {
                    "visibleActions": ["leftClick", "dragStart", "scrollUp"]
                },
                "dwell": { "activationButtonDwellMs": 800 }
            }
            """;
            File.WriteAllText(tempPath, json);
            var service = new SettingsService(tempPath);
            service.Load();

            Check.That(File.Exists(tempPath + ".tmp")).IsFalse();

            // The migrated form really reached disk: a second Load sees no dragStart to migrate and
            // still returns the user's value rather than the default.
            Check.That(File.ReadAllText(tempPath)).Not.Contains("dragStart");
            Check.That(service.Load().Dwell.ActivationButtonDwellMs).IsEqualTo(800);
        }
        finally { File.Delete(tempPath); }
    }

    [Fact]
    public void Load_RemovesDragEnd_FromVisibleActions()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");
        try
        {
            var json = """
            {
                "actionMenu": {
                    "visibleActions": ["leftClick", "dragEnd", "scrollUp"]
                }
            }
            """;
            File.WriteAllText(tempPath, json);
            var service = new SettingsService(tempPath);
            var settings = service.Load();

            Check.That(settings.ActionMenu.VisibleActions).Contains(InteractionAction.Drag);
            Check.That(settings.ActionMenu.VisibleActions).HasSize(3);
        }
        finally { File.Delete(tempPath); }
    }

    [Fact]
    public void Load_MigratesDragStart_CaseInsensitive()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");
        try
        {
            var json = """
            {
                "actionMenu": {
                    "visibleActions": ["leftClick", "DragStart", "scrollUp"]
                }
            }
            """;
            File.WriteAllText(tempPath, json);
            var service = new SettingsService(tempPath);
            var settings = service.Load();

            Check.That(settings.ActionMenu.VisibleActions).Contains(InteractionAction.Drag);
            Check.That(settings.ActionMenu.VisibleActions).HasSize(3);
        }
        finally { File.Delete(tempPath); }
    }

    [Fact]
    public void Load_ClampsOutOfRangeGazeIndicator_InsteadOfResettingEverything()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");
        try
        {
            var settings = new EyeCtrlSettings();
            settings.Dwell.ActivationButtonDwellMs = 600;
            var service = new SettingsService(tempPath);
            service.Save(settings);

            // Hand-edit the file the way a user would: one out-of-range indicator value.
            var root = JsonNode.Parse(File.ReadAllText(tempPath))!.AsObject();
            root["manualSelection"]!["gazeIndicatorOuterDiameter"] = 250;
            File.WriteAllText(tempPath, root.ToJsonString());

            var loaded = service.Load();
            Check.That(loaded.ManualSelection.GazeIndicatorOuterDiameter).IsEqualTo(200);
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(600);
        }
        finally { File.Delete(tempPath); }
    }

    [Fact]
    public void Load_ClampsOutOfRangeManualSelectionTargeting_InsteadOfResettingEverything()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");
        try
        {
            var settings = new EyeCtrlSettings();
            settings.Dwell.ActivationButtonDwellMs = 600;
            var service = new SettingsService(tempPath);
            service.Save(settings);

            // Hand-edit the file the way a user would: an out-of-range hold radius and a stability
            // window below K (which would otherwise make onset impossible and selection dead).
            var root = JsonNode.Parse(File.ReadAllText(tempPath))!.AsObject();
            root["manualSelection"]!["holdRadiusLogical"] = 5000;
            root["manualSelection"]!["stabilityWindowSize"] = 1;
            File.WriteAllText(tempPath, root.ToJsonString());

            var loaded = service.Load();
            Check.That(loaded.ManualSelection.HoldRadiusLogical).IsEqualTo(300);
            Check.That(loaded.ManualSelection.StabilityWindowSize)
                .IsEqualTo(loaded.ManualSelection.StabilitySampleCount);
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(600);
        }
        finally { File.Delete(tempPath); }
    }
}
