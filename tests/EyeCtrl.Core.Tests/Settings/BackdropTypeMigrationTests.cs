namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Settings;

/// <summary>
/// The Mica + Acrylic -> Glass merge (2026-10-16). Two things are being defended:
/// <list type="bullet">
/// <item><description>
/// COMPATIBILITY: Mica and Acrylic drew the same material since 2026-10-14, so an existing file
/// holding either legacy name must load as Glass with EVERY other setting intact.
/// </description></item>
/// <item><description>
/// The WIPE TRAP: with the members gone from the enum, "mica"/"acrylic" is an unknown enum string
/// and JsonStringEnumConverter throws on it inside Deserialize. The #153 repair treats that as a
/// defect of the backdrop section: alone it costs the section's type (the drop-one retry reverts
/// one property), but beside any second backdrop defect no single drop repairs the section and
/// D3's multi-fault verdict resets the WHOLE section — the user's opacity and Enabled lost over a
/// name the app itself wrote. The pre-deserialize migration rewrites the name at the JSON node
/// BEFORE the deserializer runs, so it is never a defect at all, and Load's atomic write-back
/// persists the rewrite.
/// </description></item>
/// </list>
/// </summary>
public class BackdropTypeMigrationTests
{
    [Theory]
    [InlineData("acrylic")]
    [InlineData("mica")]
    [InlineData("Mica")]     // hand-edited casing must migrate too
    [InlineData("MICA")]
    [InlineData("Acrylic")]
    public void ALegacyBlurName_LoadsAsGlass_WithEveryOtherSettingIntact_AndHealsTheFile(string legacyTypeName)
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, LegacyFile(legacyTypeName));
            var loaded = new SettingsService(path).Load();

            Check.That(loaded.Backdrop.Type).IsEqualTo(BackdropType.Glass);
            // The in-section witnesses: without the migration these rely on the repair's drop-one
            // verdict alone, and beside a second defect they are exactly what a section reset eats.
            Check.That(loaded.Backdrop.Opacity).IsEqualTo(0.45);
            Check.That(loaded.Backdrop.Enabled).IsFalse();
            // The rest of the file — a reset or wipe must never reach it.
            Check.That(loaded.Keyboard.ActiveLayout).IsEqualTo("AZERTY");
            Check.That(loaded.Keyboard.LanguagePack).IsEqualTo("de");
            Check.That(loaded.Keyboard.MaxSuggestions).IsEqualTo(3);
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(850);
            Check.That(loaded.General.UiLanguage).IsEqualTo(UiLanguage.German);

            // The migration persists (Load's atomic write-back): the legacy name is gone from the
            // file itself, so nothing downstream — the Supervisor's raw reader, the live check's
            // Enum.TryParse — ever meets a name this build no longer knows.
            string onDisk = File.ReadAllText(path);
            Check.That(onDisk).Contains("\"glass\"");
            Check.That(onDisk.Contains("\"mica\"", StringComparison.OrdinalIgnoreCase)).IsFalse();
            Check.That(onDisk.Contains("\"acrylic\"", StringComparison.OrdinalIgnoreCase)).IsFalse();
            Check.That(File.Exists(path + ".tmp")).IsFalse();
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("0", BackdropType.Glass)]        // old Acrylic: 0 is Glass by the pinned enum values
    [InlineData("1", BackdropType.Glass)]        // old Mica: undefined under the new values, rewritten at the node
    [InlineData("2", BackdropType.SolidDarken)]  // old SolidDarken: 2 is Solid darken by the pinned values
    public void ANumericLegacyType_KeepsItsOldMaterial_WithEveryOtherSettingIntact(string rawNumber, BackdropType expected)
    {
        // The string-enum converter accepts integers (the #153 F3 note), and this owner hand-edits
        // settings.json — so a bare old ordinal must keep its meaning. The enum pins Glass = 0 and
        // SolidDarken = 2 exactly for this; the old Mica's 1 has no value left on purpose and is
        // rewritten, like the names.
        var path = TempFile();
        try
        {
            File.WriteAllText(path, NumericLegacyFile(rawNumber));
            string before = File.ReadAllText(path);

            var loaded = new SettingsService(path).Load();

            Check.That(loaded.Backdrop.Type).IsEqualTo(expected);
            Check.That(loaded.Backdrop.Opacity).IsEqualTo(0.45);
            Check.That(loaded.Backdrop.Enabled).IsFalse();
            Check.That(loaded.Keyboard.ActiveLayout).IsEqualTo("AZERTY");
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(850);

            string onDisk = File.ReadAllText(path);
            if (rawNumber == "1")
            {
                Check.That(onDisk).Contains("\"glass\"");
                Check.That(onDisk.Contains("\"type\": 1", StringComparison.Ordinal)).IsFalse();
            }
            else
            {
                // 0 and 2 still deserialize to their old materials: no rewrite, byte-identical file.
                Check.That(onDisk).IsEqualTo(before);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ALegacyName_BesideASecondBackdropDefect_DoesNotResetTheSection()
    {
        // D3's multi-fault verdict is the wipe this migration exists to prevent. Without it the
        // legacy name IS a defect, and beside the typo'd opacity no single drop repairs the
        // section: Enabled comes back true and the opacity reverts to its default. With the
        // migration the name is gone before deserialization, so the drop-one retry costs only the
        // typo'd property — the section reset never happens.
        var path = TempFile();
        try
        {
            File.WriteAllText(path, LegacyFile("mica", opacity: "0.95"));
            var loaded = new SettingsService(path).Load();

            Check.That(loaded.Backdrop.Type).IsEqualTo(BackdropType.Glass);
            Check.That(loaded.Backdrop.Enabled).IsFalse();      // the section was NOT reset
            Check.That(loaded.Backdrop.Opacity).IsEqualTo(0.3); // the typo cost its one property
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(850);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ASolidDarkenFile_IsNotTouchedByTheMigration()
    {
        // The cheap reject keys on the text, but the migration itself must rewrite NOTHING when
        // the type is not a legacy blur name: the file stays byte-identical (no needless
        // write-back on the common already-current file).
        var path = TempFile();
        try
        {
            File.WriteAllText(path, LegacyFile("solidDarken"));
            string before = File.ReadAllText(path);

            var loaded = new SettingsService(path).Load();

            Check.That(loaded.Backdrop.Type).IsEqualTo(BackdropType.SolidDarken);
            Check.That(loaded.Backdrop.Opacity).IsEqualTo(0.45);
            Check.That(File.ReadAllText(path)).IsEqualTo(before);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ALegacyName_OutsideTheBackdropType_IsNotRewritten()
    {
        // The cheap reject sees the text anywhere; the STRUCTURE check must confirm it before any
        // edit, so a legacy word in some other value cannot cost a needless whole-file rewrite.
        var path = TempFile();
        try
        {
            const string json = """
            {
              "backdrop": { "enabled": true, "type": "glass", "opacity": 0.45, "materialNote": "Mica" }
            }
            """;
            File.WriteAllText(path, json);

            var loaded = new SettingsService(path).Load();

            Check.That(loaded.Backdrop.Type).IsEqualTo(BackdropType.Glass);
            Check.That(File.ReadAllText(path)).IsEqualTo(json);   // untouched
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheMigrationRunsOnce_AndASecondLoadChangesNothing()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, LegacyFile("acrylic"));
            var service = new SettingsService(path);
            var first = service.Load();
            string afterFirst = File.ReadAllText(path);

            var second = service.Load();

            Check.That(second.Backdrop.Type).IsEqualTo(BackdropType.Glass);
            Check.That(second.Backdrop.Opacity).IsEqualTo(0.45);
            Check.That(first.Backdrop.Opacity).IsEqualTo(second.Backdrop.Opacity);
            Check.That(File.ReadAllText(path)).IsEqualTo(afterFirst);   // not rewritten again
        }
        finally { File.Delete(path); }
    }

    // ── a duplicated key must never cost the file (review-01 F1, review-02 R1) ──────────────────
    //
    // The duplicate normalizer keeps the LAST occurrence of every duplicated property before any
    // other consumer runs (the deserializer's own rule), so a lost-looking step is safe AND the
    // duplicate itself no longer reaches the #153 repair walk — whose JsonNode navigation used to
    // throw ArgumentException over a ROOT duplicate, costing the whole file. The healed text is
    // persisted by Load's write-back; the one-time .bak-glass still holds the ORIGINAL bytes when
    // the backdrop rename itself happens.

    [Fact]
    public void ADuplicateKeyInsideBackdrop_IsNormalizedToTheLastValue_AndEverythingSurvives()
    {
        // P12: a copied "opacity" line inside the section (the owner's edit). All values are legal,
        // so this one always loaded on main via last-wins; the normalizer keeps that outcome AND
        // heals the file so no later consumer sees the duplicate.
        var path = TempFile();
        try
        {
            const string json = """
            {
              "dwell": { "activationButtonDwellMs": 850, "keyboardKeyDwellMs": 500 },
              "keyboard": { "activeLayout": "AZERTY", "languagePack": "de", "maxSuggestions": 3 },
              "backdrop": { "enabled": false, "opacity": 0.4, "opacity": 0.45, "type": "solidDarken" }
            }
            """;
            File.WriteAllText(path, json);

            var loaded = new SettingsService(path).Load();

            Check.That(loaded.Backdrop.Type).IsEqualTo(BackdropType.SolidDarken);
            Check.That(loaded.Backdrop.Opacity).IsEqualTo(0.45);   // the last duplicate wins
            Check.That(loaded.Backdrop.Enabled).IsFalse();
            Check.That(loaded.Keyboard.ActiveLayout).IsEqualTo("AZERTY");
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(850);

            string healed = File.ReadAllText(path);
            Check.That(CountOccurrences(healed, "\"opacity\"")).IsEqualTo(1);
            Check.That(healed).Contains("0.45");
            Check.That(File.Exists(path + SettingsService.GlassBackupSuffix)).IsFalse(); // no rename happened
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ADuplicateTypeKey_IsNormalizedToTheLastValue_AndEverythingSurvives()
    {
        // P1, now with the better outcome R1 bought: the normalizer drops the first "type" before
        // any consumer runs, so the whole-graph deserialize never meets the old "mica" name, the
        // backdrop section is NOT judged damaged, and the 0.45 opacity survives. The file heals;
        // no .bak-glass is needed because the surviving value is already "glass".
        var path = TempFile();
        try
        {
            const string json = """
            {
              "dwell": { "activationButtonDwellMs": 850, "keyboardKeyDwellMs": 500 },
              "keyboard": { "activeLayout": "AZERTY", "languagePack": "de", "maxSuggestions": 3 },
              "backdrop": { "enabled": false, "type": "mica", "type": "glass", "opacity": 0.45 }
            }
            """;
            File.WriteAllText(path, json);

            var loaded = new SettingsService(path).Load();

            Check.That(loaded.Backdrop.Type).IsEqualTo(BackdropType.Glass);
            Check.That(loaded.Backdrop.Opacity).IsEqualTo(0.45);
            Check.That(loaded.Backdrop.Enabled).IsFalse();
            Check.That(loaded.Keyboard.ActiveLayout).IsEqualTo("AZERTY");
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(850);

            string healed = File.ReadAllText(path);
            Check.That(CountOccurrences(healed, "\"type\"")).IsEqualTo(1);
            Check.That(healed.Contains("mica", StringComparison.Ordinal)).IsFalse();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ADuplicatedRootSectionKey_IsNormalized_AndTheFileSurvives()
    {
        // P11: a duplicated ROOT key (a copied section block). The normalizer proves the duplicate
        // first, so the later steps and the whole-graph deserializer both see the last occurrence —
        // the same rule main applies when every value is legal.
        var path = TempFile();
        try
        {
            const string json = """
            {
              "dwell": { "activationButtonDwellMs": 600, "keyboardKeyDwellMs": 500 },
              "dwell": { "activationButtonDwellMs": 850, "keyboardKeyDwellMs": 500 },
              "keyboard": { "activeLayout": "AZERTY", "languagePack": "de", "maxSuggestions": 3 },
              "backdrop": { "enabled": false, "type": "glass", "opacity": 0.45 }
            }
            """;
            File.WriteAllText(path, json);

            var loaded = new SettingsService(path).Load();

            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(850);   // the last root key wins
            Check.That(loaded.Keyboard.ActiveLayout).IsEqualTo("AZERTY");
            Check.That(loaded.Backdrop.Type).IsEqualTo(BackdropType.Glass);
            Check.That(loaded.Backdrop.Opacity).IsEqualTo(0.45);
            Check.That(CountOccurrences(File.ReadAllText(path), "\"dwell\"")).IsEqualTo(1);   // healed
        }
        finally { File.Delete(path); }
    }

    // ── R1: a ROOT duplicate plus a value that needs repair must not wipe the file ──────────────

    [Fact]
    public void ARootDuplicatePlusALegacyName_Migrates_Heals_AndBacksUpTheOriginal()
    {
        // P4: the un-migrated legacy file the owner had hand-edited into a root duplicate. Every
        // stage used to fail in turn and the outer catch answered all-defaults; now the normalizer
        // first, then the rename, and the one-time backup holds the ORIGINAL duplicated file.
        var path = TempFile();
        var backup = path + SettingsService.GlassBackupSuffix;
        try
        {
            const string json = """
            {
              "dwell": { "activationButtonDwellMs": 850, "keyboardKeyDwellMs": 500 },
              "dwell": { "activationButtonDwellMs": 850, "keyboardKeyDwellMs": 500 },
              "keyboard": { "activeLayout": "AZERTY", "languagePack": "de", "maxSuggestions": 3 },
              "general": { "uiLanguage": "german", "startPaused": true },
              "backdrop": { "enabled": false, "type": "mica", "opacity": 0.45 }
            }
            """;
            File.WriteAllText(path, json);
            byte[] originalBytes = File.ReadAllBytes(path);

            var loaded = new SettingsService(path).Load();

            Check.That(loaded.Backdrop.Type).IsEqualTo(BackdropType.Glass);
            Check.That(loaded.Backdrop.Opacity).IsEqualTo(0.45);
            Check.That(loaded.Backdrop.Enabled).IsFalse();
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(850);
            Check.That(loaded.Keyboard.ActiveLayout).IsEqualTo("AZERTY");
            Check.That(loaded.Keyboard.LanguagePack).IsEqualTo("de");
            Check.That(loaded.Keyboard.MaxSuggestions).IsEqualTo(3);
            Check.That(loaded.General.UiLanguage).IsEqualTo(UiLanguage.German);
            Check.That(loaded.General.StartPaused).IsTrue();

            var healed = File.ReadAllText(path);
            Check.That(healed).Contains("\"glass\"");
            Check.That(healed.Contains("mica", StringComparison.Ordinal)).IsFalse();
            Check.That(CountOccurrences(healed, "\"dwell\"")).IsEqualTo(1);
            // The backup is the ORIGINAL file — the duplicate included, byte for byte.
            Check.That(File.ReadAllBytes(backup).SequenceEqual(originalBytes)).IsTrue();
        }
        finally { File.Delete(path); File.Delete(backup); }
    }

    [Fact]
    public void ARootDuplicatePlusASubFloorDwell_KeepsEverySetting()
    {
        // P13: root duplicate + keyboardKeyDwellMs 200. The normalizer proves the duplicate, the
        // dwell clamp then runs on the deduplicated JSON and lifts the floor; every setting stays.
        var path = TempFile();
        try
        {
            const string json = """
            {
              "dwell": { "activationButtonDwellMs": 850, "keyboardKeyDwellMs": 200 },
              "dwell": { "activationButtonDwellMs": 850, "keyboardKeyDwellMs": 200 },
              "keyboard": { "activeLayout": "AZERTY", "languagePack": "de", "maxSuggestions": 3 },
              "general": { "uiLanguage": "german", "startPaused": true },
              "backdrop": { "enabled": false, "type": "glass", "opacity": 0.45 }
            }
            """;
            File.WriteAllText(path, json);

            var loaded = new SettingsService(path).Load();

            Check.That(loaded.Dwell.KeyboardKeyDwellMs).IsEqualTo(300);   // clamped, not dropped
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(850);
            Check.That(loaded.Keyboard.ActiveLayout).IsEqualTo("AZERTY");
            Check.That(loaded.General.UiLanguage).IsEqualTo(UiLanguage.German);
            Check.That(loaded.General.StartPaused).IsTrue();
            Check.That(loaded.Backdrop.Type).IsEqualTo(BackdropType.Glass);
            Check.That(loaded.Backdrop.Opacity).IsEqualTo(0.45);

            var healed = File.ReadAllText(path);
            Check.That(CountOccurrences(healed, "\"dwell\"")).IsEqualTo(1);
            Check.That(healed).Contains("300");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ARootDuplicatePlusAnOutOfRangeOpacity_KeepsEveryOtherSetting()
    {
        // P14: root duplicate + opacity 5.0. The normalizer heals the duplicate, the repair then
        // costs exactly the one invalid property (the #153 drop-one verdict) and every other
        // value — the other backdrop fields included — survives. The file is not wiped.
        var path = TempFile();
        try
        {
            const string json = """
            {
              "dwell": { "activationButtonDwellMs": 850, "keyboardKeyDwellMs": 500 },
              "dwell": { "activationButtonDwellMs": 850, "keyboardKeyDwellMs": 500 },
              "keyboard": { "activeLayout": "AZERTY", "languagePack": "de", "maxSuggestions": 3 },
              "general": { "uiLanguage": "german", "startPaused": true },
              "backdrop": { "enabled": false, "type": "solidDarken", "opacity": 5.0 }
            }
            """;
            File.WriteAllText(path, json);

            var loaded = new SettingsService(path).Load();

            Check.That(loaded.Backdrop.Opacity).IsEqualTo(0.3);   // the one invalid property reverts
            Check.That(loaded.Backdrop.Type).IsEqualTo(BackdropType.SolidDarken);
            Check.That(loaded.Backdrop.Enabled).IsFalse();
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(850);
            Check.That(loaded.Keyboard.ActiveLayout).IsEqualTo("AZERTY");
            Check.That(loaded.General.UiLanguage).IsEqualTo(UiLanguage.German);
            Check.That(loaded.General.StartPaused).IsTrue();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ADuplicateInsideDwell_PlusASubFloor_KeepsEverySetting()
    {
        // P15: a duplicate INSIDE the dwell section + the sub-floor value. Before R1 the duplicate
        // made the section a D5 multi-fault and the whole section reverted (850 -> 700); the
        // normalizer keeps the last copy, the clamp lifts 200 to 300, and the section — and the
        // file — stay otherwise untouched.
        var path = TempFile();
        try
        {
            const string json = """
            {
              "dwell": { "activationButtonDwellMs": 850, "keyboardKeyDwellMs": 400, "keyboardKeyDwellMs": 200 },
              "keyboard": { "activeLayout": "AZERTY", "languagePack": "de", "maxSuggestions": 3 },
              "general": { "uiLanguage": "german", "startPaused": true },
              "backdrop": { "enabled": false, "type": "glass", "opacity": 0.45 }
            }
            """;
            File.WriteAllText(path, json);

            var loaded = new SettingsService(path).Load();

            Check.That(loaded.Dwell.KeyboardKeyDwellMs).IsEqualTo(300);
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(850);
            Check.That(loaded.Keyboard.ActiveLayout).IsEqualTo("AZERTY");
            Check.That(loaded.General.UiLanguage).IsEqualTo(UiLanguage.German);
            Check.That(loaded.General.StartPaused).IsTrue();
            Check.That(loaded.Backdrop.Opacity).IsEqualTo(0.45);

            var healed = File.ReadAllText(path);
            Check.That(CountOccurrences(healed, "\"keyboardKeyDwellMs\"")).IsEqualTo(1);
            Check.That(healed).Contains("300");
        }
        finally { File.Delete(path); }
    }

    // ── review-01: the one-time backup ──────────────────────────────────────────────────────────

    [Fact]
    public void TheOneTimeBackup_KeepsTheOriginalBytes_AndASecondLoadDoesNotTouchIt()
    {
        var path = TempFile();
        var backup = path + SettingsService.GlassBackupSuffix;
        try
        {
            File.WriteAllText(path, LegacyFile("acrylic"));
            byte[] originalBytes = File.ReadAllBytes(path);

            var service = new SettingsService(path);
            service.Load();

            Check.That(File.Exists(backup)).IsTrue();
            Check.That(File.ReadAllBytes(backup).SequenceEqual(originalBytes)).IsTrue();
            Check.That(File.ReadAllText(path)).Contains("\"glass\"");   // the file itself migrated
            var backupWrite = File.GetLastWriteTimeUtc(backup);

            service.Load();   // already current: the step changes nothing, so it must not touch the backup

            Check.That(File.ReadAllBytes(backup).SequenceEqual(originalBytes)).IsTrue();
            Check.That(File.GetLastWriteTimeUtc(backup)).IsEqualTo(backupWrite);
        }
        finally { File.Delete(path); File.Delete(backup); }
    }

    [Fact]
    public void AnExistingBackup_IsNeverOverwritten()
    {
        var path = TempFile();
        var backup = path + SettingsService.GlassBackupSuffix;
        try
        {
            File.WriteAllText(path, LegacyFile("mica"));
            File.WriteAllText(backup, "the user's own earlier backup");

            new SettingsService(path).Load();

            Check.That(File.ReadAllText(backup)).IsEqualTo("the user's own earlier backup");
            Check.That(File.ReadAllText(path)).Contains("\"glass\"");
        }
        finally { File.Delete(path); File.Delete(backup); }
    }

    [Fact]
    public void ANoOpLoad_WritesNoBackup()
    {
        var path = TempFile();
        var backup = path + SettingsService.GlassBackupSuffix;
        try
        {
            File.WriteAllText(path, LegacyFile("solidDarken"));

            new SettingsService(path).Load();

            Check.That(File.Exists(backup)).IsFalse();
        }
        finally { File.Delete(path); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"eyectrl_backdropmig_{Guid.NewGuid()}.json");

    /// <summary>How often <paramref name="token"/> appears in <paramref name="text"/> — used to
    /// prove a duplicated property was healed to exactly one occurrence.</summary>
    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        for (var index = text.IndexOf(token, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(token, index + token.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    /// <summary>A real old-style settings file: the legacy backdrop name beside several
    /// non-default settings — the witnesses a section reset (or a wipe) must never reach.</summary>
    private static string LegacyFile(string legacyTypeName, string opacity = "0.45") =>
        $$"""
        {
          "dwell": { "activationButtonDwellMs": 850 },
          "keyboard": { "activeLayout": "AZERTY", "languagePack": "de", "maxSuggestions": 3 },
          "general": { "uiLanguage": "german" },
          "backdrop": { "enabled": false, "type": "{{legacyTypeName}}", "opacity": {{opacity}} }
        }
        """;

    /// <summary>The same old-style file with a BARE NUMBER as the type — the hand-edit form the
    /// string-enum converter accepts (no build ever wrote one), quoted strings being no legacy
    /// form at all: the converter has always matched string values by member name.</summary>
    private static string NumericLegacyFile(string rawNumber) =>
        $$"""
        {
          "dwell": { "activationButtonDwellMs": 850 },
          "keyboard": { "activeLayout": "AZERTY", "languagePack": "de", "maxSuggestions": 3 },
          "general": { "uiLanguage": "german" },
          "backdrop": { "enabled": false, "type": {{rawNumber}}, "opacity": 0.45 }
        }
        """;
}
