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
//       to defaults (which also reverts the section's once-only migration flags);
//   D4  root scalar properties (reflection-derived: every public writable non-section
//       property) are probed with deserialize +, for enums, a defined-value check;
//   D5  whole-file defaults survive only for the no-data classes: missing file, unreadable
//       file, unparseable JSON, non-object root — and duplicate keys where the fast path's
//       last-wins also fails (the repair's JsonNode lookup refuses ambiguous access);
//   D6  repairs are in-memory only — they reach disk on the next organic Save, whose
//       supervision overlay heals a damaged on-disk section from the on-disk values
//       (drop-one verdict, valid siblings survive) instead of preserving it forever
//       (#153 review F1).
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
    public void Load_PascalCaseSectionTwin_IsInert_PolicyNameBindsAndIsRepaired()
    {
        // Pins the binder semantics the repair relies on (verified empirically on net10):
        // with a naming policy set, STJ binds ONLY the policy name ("dwell") — a PascalCase
        // "Dwell" is an unknown member, ignored without error. The discriminating shape:
        // BOTH spellings present with different values, damage in the policy-name node, and
        // the PascalCase twin FIRST in document order — so a case-insensitive first-match
        // ResolveKey would probe the healthy twin, leave the damage in "dwell" for the final
        // whole-graph pass (which throws and wipes the file). Kills that mutation (M7).
        var path = TempPath();
        try
        {
            var json = """
            {
                "Dwell": { "activationButtonDwellMs": 999 },
                "dwell": { "fixationDetectionMs": 50, "activationButtonDwellMs": 800 },
                "cursor": { "smoothingFactor": 0.7 }
            }
            """;
            File.WriteAllText(path, json);

            var loaded = new SettingsService(path).Load();
            Check.That(loaded.Dwell.FixationDetectionMs).IsEqualTo(300);          // repaired on "dwell"
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(800);      // "dwell" wins, twin's 999 inert
            Check.That(loaded.Cursor.SmoothingFactor).IsEqualTo(0.7);             // survived
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
    public void Load_NonObjectRoot_StillReturnsWholeFileDefaults()
    {
        // D5, the other no-data shape: well-formed JSON whose root is not an object.
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "42");
            var loaded = new SettingsService(path).Load();
            var defaults = new EyeCtrlSettings();
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(defaults.Dwell.ActivationButtonDwellMs);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_NullSubProperty_IsDropped_OtherPropertiesSurvive()
    {
        // JSON null into a non-nullable value-type property is a deserialize error — the same
        // class as a setter throw, so the drop-one repair handles it per property.
        var path = TempPath();
        try
        {
            var json = """
            {
                "cursor": { "smoothingFactor": null, "circleSize": 300 },
                "dwell": { "activationButtonDwellMs": 800 }
            }
            """;
            File.WriteAllText(path, json);

            var loaded = new SettingsService(path).Load();
            Check.That(loaded.Cursor.SmoothingFactor).IsEqualTo(0.5);           // dropped -> default
            Check.That(loaded.Cursor.CircleSize).IsEqualTo(300);                // survived
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(800);    // survived
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_ArrayWhereSectionBelongs_ResetsSection_SiblingsSurvive()
    {
        // An array cannot deserialize into any settings class — the "not an object" branch of
        // the repair resets the section and keeps going.
        var path = TempPath();
        try
        {
            var json = """
            {
                "dwell": [1, 2, 3],
                "cursor": { "smoothingFactor": 0.7 }
            }
            """;
            File.WriteAllText(path, json);

            var loaded = new SettingsService(path).Load();
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(700);    // default instance
            Check.That(loaded.Cursor.SmoothingFactor).IsEqualTo(0.7);           // survived
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

    // ── Review round 1 (#153): F1–F8 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Save_DamagedOnDiskSupervision_IsHealedByTheAppWriter()
    {
        // F1: the App writer's raw supervision overlay preserved a damaged on-disk section
        // forever — the Supervisor loads but never saves, so the damage was re-repaired in
        // memory every launch and never healed on disk (probe P5: maxRetries 99 survived an
        // App Save). The overlay is now conditional on the acceptance probe: damaged
        // supervision keeps the in-memory (repaired) section and heals on this Save.
        var path = TempPath();
        try
        {
            var json = """
            {
                "dwell": { "activationButtonDwellMs": 800 },
                "supervision": { "maxRetries": 999 }
            }
            """;
            File.WriteAllText(path, json);
            var service = new SettingsService(path);

            var loaded = service.Load();            // repairs supervision in memory (drop-one -> default 3)
            Check.That(loaded.Supervision.MaxRetries).IsEqualTo(3);
            service.Save(loaded);

            var onDisk = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Check.That((int)onDisk["supervision"]!["maxRetries"]!).IsEqualTo(3);   // healed
            Check.That((int)onDisk["dwell"]!["activationButtonDwellMs"]!).IsEqualTo(800);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_ValidOnDiskSupervision_StillOverlaysTheInMemorySnapshot()
    {
        // F1 companion: the ownership guarantee is intact. A VALID on-disk supervision
        // section always wins over the caller's (possibly stale) in-memory one — the
        // Supervisor's writes are always valid, so the conditional overlay never discards
        // a legitimate supervisor value.
        var path = TempPath();
        try
        {
            var json = """
            {
                "supervision": { "maxRetries": 7 },
                "dwell": { "activationButtonDwellMs": 800 }
            }
            """;
            File.WriteAllText(path, json);
            var service = new SettingsService(path);

            var loaded = service.Load();
            loaded.Supervision.MaxRetries = 2;      // stale in-memory value, as if the supervisor wrote after we loaded
            service.Save(loaded);

            var onDisk = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Check.That((int)onDisk["supervision"]!["maxRetries"]!).IsEqualTo(7);   // on-disk wins
            Check.That((int)onDisk["dwell"]!["activationButtonDwellMs"]!).IsEqualTo(800);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_UnloadedSnapshot_MixedValidSupervision_HealsDamageAndKeepsValidValues()
    {
        // Self-review catch (pre-round-2): keeping the INCOMING section on a failed probe
        // destroys valid on-disk supervision siblings whenever the caller saves a snapshot
        // it never loaded (Save(new EyeCtrlSettings()) — the pattern the writer-ownership
        // tests themselves use). Here enabled=true must survive while maxRetries heals;
        // answering with all-defaults would trade one bad value for the user's whole
        // supervision section — the #153 mistake, one level down.
        var path = TempPath();
        try
        {
            var json = """
            {
                "supervision": { "enabled": true, "maxRetries": 999 },
                "dwell": { "activationButtonDwellMs": 800 }
            }
            """;
            File.WriteAllText(path, json);

            new SettingsService(path, SettingsWriter.App).Save(new EyeCtrlSettings());

            var onDisk = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Check.That(onDisk["supervision"]!["enabled"]!.GetValue<bool>()).IsTrue();   // valid sibling kept
            Check.That((int)onDisk["supervision"]!["maxRetries"]!).IsEqualTo(3);       // damage healed
            // Dwell is App-owned: the unloaded snapshot's default wins there (700), which is
            // exactly why the supervision heal must come from the on-disk values, not incoming.
            Check.That((int)onDisk["dwell"]!["activationButtonDwellMs"]!).IsEqualTo(700);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RootScalarRepairSet_IsDerivedByReflection_NotHandMaintained()
    {
        // F2 hardening: the TYPE pin (NonSectionWritableProperties_...) catches a new root
        // scalar, but not a revert of the implementation to a hand-maintained list that
        // happens to hold today's two names. This asserts the implementation's probe set IS
        // the reflection-derived set, so the revert mutation dies as soon as any third
        // scalar exists (or now, if the filters drift apart).
        var derived = typeof(EyeCtrlSettings).GetProperties()
            .Where(p => p.GetSetMethod() is not null
                && p.GetIndexParameters().Length == 0
                && !typeof(IValidatable).IsAssignableFrom(p.PropertyType))
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToList();

        var actual = SettingsService.RootScalarProperties.Select(t => t.Name).OrderBy(n => n).ToList();
        Check.That(actual).ContainsExactly(derived);
    }

    [Fact]
    public void Load_UndefinedEnumIntOnRootScalar_IsDropped_SiblingsSurvive()
    {
        // F3: JsonStringEnumConverter accepts any integer, so "viewPosition": 999
        // deserializes "fine" into an undefined enum value — the scalar probe was
        // deserialize-only and kept it (probe P4). It now also checks defined-ness.
        var path = TempPath();
        try
        {
            var json = """
            {
                "viewPosition": 999,
                "dwell": { "activationButtonDwellMs": 800 }
            }
            """;
            File.WriteAllText(path, json);

            var loaded = new SettingsService(path).Load();
            Check.That(loaded.ViewPosition).IsEqualTo(ViewPosition.AtCenter);     // dropped -> default
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(800);      // survived
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_NullManualSelectionSection_DoesNotThrow_SiblingsSurvive()
    {
        // F7: the null-section guard was removable only because validation (which flags
        // null sections) runs BEFORE the SelectionMode seed, which dereferences
        // ManualSelection. This pins that ordering — remove the reordering and this
        // test NREs on the fast path.
        var path = TempPath();
        try
        {
            var json = """
            {
                "manualSelection": null,
                "dwell": { "activationButtonDwellMs": 800 }
            }
            """;
            File.WriteAllText(path, json);

            var loaded = new SettingsService(path).Load();
            Check.That(loaded.ManualSelection).IsNotNull();
            var defaults = new ManualSelectionSettings();
            Check.That(loaded.ManualSelection.CircleRadiusPixels).IsEqualTo(defaults.CircleRadiusPixels);
            Check.That(loaded.SelectionMode).IsEqualTo(SelectionMode.None);       // default seed input
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(800);      // survived
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_DamagedDuplicateKeys_AnswersWholeFileDefaults()
    {
        // F5: STJ itself is last-wins and tolerates duplicates on the fast path — but when
        // the LAST duplicate is the damaged one, the fast path throws and the repair's
        // JsonNode lookup refuses the ambiguous access (ArgumentException), landing in the
        // catch. Pinned so the D5 ledger line stays honest.
        var path = TempPath();
        try
        {
            var json = """
            {
                "dwell": { "activationButtonDwellMs": 800 },
                "dwell": { "activationButtonDwellMs": 50 }
            }
            """;
            File.WriteAllText(path, json);

            var loaded = new SettingsService(path).Load();
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(700);      // whole-file defaults
        }
        finally { File.Delete(path); }
    }
}
