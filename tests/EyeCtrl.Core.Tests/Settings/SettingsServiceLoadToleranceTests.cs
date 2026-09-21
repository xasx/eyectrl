namespace EyeCtrl.Core.Tests.Settings;

using System.Text.Json.Nodes;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

// Issue #153: Load must tolerate a damaged settings file per-setting, not answer any defect
// with whole-file defaults. The policy (design ledger, SettingsService.Load):
//   D1  the Clamp family stays — intent-preserving recovery where a range is known;
//   D2  a section is acceptable iff it deserializes without throwing AND (after clamping)
//       passes its own Validate() — evaluated standalone, at the JSON-node level;
//   D3  a section failing D2 is retried with each single property dropped (single-fault
//       assumption); the first accepting candidate wins, else the WHOLE section reverts
//       to defaults;
//   D4  the root enum scalars (viewPosition, selectionMode) get the same probe-or-drop;
//   D5  whole-file defaults survive only for the no-data classes: missing file, unreadable
//       file, unparseable JSON, non-object root;
//   D6  repairs are in-memory only — unlike the lossless migrations they are lossy, so they
//       reach disk on the next organic Save, never at load time.
public class SettingsServiceLoadToleranceTests
{
    private string TempPath() => Path.Combine(Path.GetTempPath(), $"eyectrl_tol_{Guid.NewGuid()}.json");

    private static JsonNode ReadRoot(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    [Fact]
    public void Load_OneGuardedValueOutOfRange_DropsOnlyThatProperty_EverythingElseSurvives()
    {
        // The hero case from the issue: one hand-edited number (fixationDetectionMs=50, below
        // the setter guard's 100 floor, covered by no clamp) must cost ONE property, not the
        // whole file. The same section's other values and every other section survive.
        var path = TempPath();
        try
        {
            var settings = new EyeCtrlSettings();
            settings.Dwell.ActivationButtonDwellMs = 600;
            settings.Cursor.SmoothingFactor = 0.7;
            var service = new SettingsService(path);
            service.Save(settings);

            var root = ReadRoot(path);
            root["dwell"]!["fixationDetectionMs"] = 50;
            File.WriteAllText(path, root.ToJsonString());

            var loaded = service.Load();
            Check.That(loaded.Dwell.FixationDetectionMs).IsEqualTo(300);          // dropped -> default
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(600);      // same section, survived
            Check.That(loaded.Cursor.SmoothingFactor).IsEqualTo(0.7);             // other section, survived
            Check.That(loaded.Validate()).IsEmpty();                              // Save-ready

            // D6: the repair is in-memory only — the file keeps the bad value for the user to
            // inspect; the next organic Save persists the repaired state.
            var onDisk = ReadRoot(path);
            Check.That((int)onDisk["dwell"]!["fixationDetectionMs"]!).IsEqualTo(50);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_UnguardedInvalidValue_DropsOnlyThatProperty()
    {
        // activeLayout has no setter guard (any string deserializes) and is rejected only by
        // Validate() — the validation-error class. The layout reverts to its default while the
        // rest of the keyboard section survives.
        var path = TempPath();
        try
        {
            var settings = new EyeCtrlSettings();
            settings.Keyboard.ActiveLayout = "AZERTY";
            settings.Keyboard.KeySizeMultiplier = 1.5;
            settings.Dwell.ActivationButtonDwellMs = 600;
            var service = new SettingsService(path);
            service.Save(settings);

            var root = ReadRoot(path);
            root["keyboard"]!["activeLayout"] = "DVORAK";
            File.WriteAllText(path, root.ToJsonString());

            var loaded = service.Load();
            Check.That(loaded.Keyboard.ActiveLayout).IsEqualTo("QWERTY");         // dropped -> default
            Check.That(loaded.Keyboard.KeySizeMultiplier).IsEqualTo(1.5);         // survived
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(600);      // survived
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_TwoBadValuesInOneSection_ResetsSection_OtherSectionsSurvive()
    {
        // D3's boundary: the drop-one retry assumes a single fault per section. Two guarded
        // values out of range in one section revert that SECTION to defaults; everything
        // outside it survives.
        var path = TempPath();
        try
        {
            var settings = new EyeCtrlSettings();
            settings.Dwell.ActivationButtonDwellMs = 600;
            settings.Cursor.SmoothingFactor = 0.7;
            var service = new SettingsService(path);
            service.Save(settings);

            var root = ReadRoot(path);
            root["dwell"]!["fixationDetectionMs"] = 50;
            root["dwell"]!["activationButtonDwellMs"] = 6000;
            File.WriteAllText(path, root.ToJsonString());

            var loaded = service.Load();
            Check.That(loaded.Dwell.FixationDetectionMs).IsEqualTo(300);
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(700);      // whole section defaulted
            Check.That(loaded.Cursor.SmoothingFactor).IsEqualTo(0.7);             // survived
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_NullSection_FallsBackToSectionDefaults_SiblingsSurvive()
    {
        // "cursor": null deserializes without throwing (the section is simply null), which
        // today NREs inside ClampCursorFilter and wipes the file via the catch-all.
        var path = TempPath();
        try
        {
            var json = """
            {
                "cursor": null,
                "dwell": { "activationButtonDwellMs": 800 }
            }
            """;
            File.WriteAllText(path, json);

            var loaded = new SettingsService(path).Load();
            Check.That(loaded.Cursor).IsNotNull();
            Check.That(loaded.Cursor.SmoothingFactor).IsEqualTo(0.5);             // default instance
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(800);      // survived
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_WrongTypeSection_FallsBackToSectionDefaults_SiblingsSurvive()
    {
        // "toolbar": 500 is a type mismatch — a JsonException during whole-graph
        // deserialization, which today costs the entire file.
        var path = TempPath();
        try
        {
            var json = """
            {
                "toolbar": 500,
                "dwell": { "activationButtonDwellMs": 800 }
            }
            """;
            File.WriteAllText(path, json);

            var loaded = new SettingsService(path).Load();
            Check.That(loaded.Toolbar.Size).IsEqualTo(120);                       // default instance
            Check.That(loaded.Toolbar.DockedEdge).IsEqualTo("Top");
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(800);      // survived
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_BadRootScalarEnum_FallsBackToDefault_SiblingsSurvive()
    {
        // An unknown enum string throws JsonException (the string-enum converter), which is
        // indistinguishable from malformed JSON at the whole-graph level. The tolerant path
        // re-anchors on JsonNode.Parse — parseable means recoverable — and probes the scalar.
        var path = TempPath();
        try
        {
            var json = """
            {
                "selectionMode": "Garbage",
                "dwell": { "activationButtonDwellMs": 800 }
            }
            """;
            File.WriteAllText(path, json);

            var loaded = new SettingsService(path).Load();
            Check.That(loaded.SelectionMode).IsEqualTo(SelectionMode.None);       // dropped -> default
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(800);      // survived
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_PascalCaseSectionKey_IsIgnored_LikeAnyUnknownMember_SiblingsSurvive()
    {
        // Pins the binder semantics the repair relies on: with a naming policy set, STJ binds
        // ONLY the policy name ("dwell") — a PascalCase "Dwell" is an unknown member, ignored
        // without throwing (the same established contract that quietly drops legacy theme
        // keys). The section therefore gets defaults; it is not damage, and siblings survive.
        // Verified empirically on net10 before pinning.
        var path = TempPath();
        try
        {
            var json = """
            {
                "Dwell": { "fixationDetectionMs": 50, "activationButtonDwellMs": 800 },
                "cursor": { "smoothingFactor": 0.7 }
            }
            """;
            File.WriteAllText(path, json);

            var loaded = new SettingsService(path).Load();
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(700);      // "Dwell" ignored -> default
            Check.That(loaded.Cursor.SmoothingFactor).IsEqualTo(0.7);             // bound key, survived
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_ClampableValueInDamagedFile_IsClampedNotDropped()
    {
        // The acceptance probe applies the Clamp family before judging (D2): a value the
        // clamps can preserve (indicator diameter 250 -> 200) must NOT be dropped to its
        // default even when the file is damaged elsewhere and takes the tolerant path.
        var path = TempPath();
        try
        {
            var json = """
            {
                "manualSelection": { "gazeIndicatorOuterDiameter": 250 },
                "dwell": { "fixationDetectionMs": 50, "activationButtonDwellMs": 800 }
            }
            """;
            File.WriteAllText(path, json);

            var loaded = new SettingsService(path).Load();
            Check.That(loaded.ManualSelection.GazeIndicatorOuterDiameter).IsEqualTo(200);  // clamped
            Check.That(loaded.Dwell.FixationDetectionMs).IsEqualTo(300);                   // dropped
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(800);               // survived
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_DamagedSectionsAreRepairedIndependently()
    {
        // Faults in different sections are independent: one per section drops its property,
        // an untouched third section survives untouched.
        var path = TempPath();
        try
        {
            var json = """
            {
                "dwell": { "fixationDetectionMs": 50, "activationButtonDwellMs": 800 },
                "keyboard": { "activeLayout": "DVORAK", "keySizeMultiplier": 1.5 },
                "cursor": { "smoothingFactor": 0.7 }
            }
            """;
            File.WriteAllText(path, json);

            var loaded = new SettingsService(path).Load();
            Check.That(loaded.Dwell.FixationDetectionMs).IsEqualTo(300);
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(800);
            Check.That(loaded.Keyboard.ActiveLayout).IsEqualTo("QWERTY");
            Check.That(loaded.Keyboard.KeySizeMultiplier).IsEqualTo(1.5);
            Check.That(loaded.Cursor.SmoothingFactor).IsEqualTo(0.7);
            Check.That(loaded.Validate()).IsEmpty();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_UnparseableJson_StillReturnsWholeFileDefaults()
    {
        // D5: a file that fails JsonNode.Parse has no data to salvage — whole-file defaults
        // is the honest answer, unchanged from before.
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{ \"dwell\": { \"activationButtonDwellMs\": ");
            var loaded = new SettingsService(path).Load();
            var defaults = new EyeCtrlSettings();
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(defaults.Dwell.ActivationButtonDwellMs);
            Check.That(loaded.Cursor.SmoothingFactor).IsEqualTo(defaults.Cursor.SmoothingFactor);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_RepairedSettings_CanBeSavedWithoutThrowing()
    {
        // The repaired in-memory state must satisfy Save()'s validate-and-throw contract —
        // this is the loop that closed the data loss: Load repairs, the next Save persists.
        var path = TempPath();
        try
        {
            var json = """
            {
                "dwell": { "fixationDetectionMs": 50, "activationButtonDwellMs": 800 },
                "theme": { "name": "NotATheme" }
            }
            """;
            File.WriteAllText(path, json);
            var service = new SettingsService(path);

            var loaded = service.Load();
            Check.ThatCode(() => service.Save(loaded)).DoesNotThrow();

            // And the saved file is now clean: a second Load takes the fast path intact.
            var reloaded = service.Load();
            Check.That(reloaded.Dwell.ActivationButtonDwellMs).IsEqualTo(800);
            Check.That(reloaded.Theme.Name).IsEqualTo("Midnight");
        }
        finally { File.Delete(path); }
    }
}
