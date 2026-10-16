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

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"eyectrl_backdropmig_{Guid.NewGuid()}.json");

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
}
