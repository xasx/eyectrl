namespace EyeCtrl.Setup.Tests.Display;

using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Setup;
using EyeCtrl.Setup.Display;
using EyeCtrl.Setup.Windows;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// The theme/language load: the owner's settings decide the theme and the language, the German
/// table comes from the App's own folder (never a second copy beside Setup), and a missing or
/// damaged file falls back to English + Midnight instead of failing the uninstall.
/// </summary>
[Collection(SetupUiWindowsCollection.Name)]
public sealed class SetupUiTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "eyectrl-s6e-ui-" + Guid.NewGuid().ToString("N"));

    public SetupUiTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Tokens.Activate(MidnightTheme.Instance);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ParseSettings_ReadsTheThemeAndTheJsonLanguageToken()
    {
        var parsed = SetupUi.ParseSettings(
            """{"theme":{"name":"Contrast"},"general":{"uiLanguage":"english"}}""");

        Check.That(parsed.ThemeName).IsEqualTo("Contrast");
        Check.That(parsed.Language).IsEqualTo(UiLanguage.English);

        var system = SetupUi.ParseSettings(
            """{"theme":{"name":"Daylight"},"general":{"uiLanguage":"system"}}""");
        Check.That(system.Language).IsEqualTo(UiLanguage.System);
    }

    [Fact]
    public void ParseSettings_RejectsTheCommandLineTokens_AndToleratesDamage()
    {
        // "de" is the command-line spelling, not the settings-JSON token: it must not be guessed.
        Check.That(SetupUi.ParseSettings("""{"general":{"uiLanguage":"de"}}""").Language)
            .IsEqualTo(UiLanguage.System);

        Check.That(SetupUi.ParseSettings(null)).IsEqualTo(SetupUiSettings.Default);
        Check.That(SetupUi.ParseSettings("not json")).IsEqualTo(SetupUiSettings.Default);
        Check.That(SetupUi.ParseSettings("{}")).IsEqualTo(SetupUiSettings.Default);
        Check.That(SetupUi.ParseSettings("[]")).IsEqualTo(SetupUiSettings.Default);
        Check.That(SetupUi.ParseSettings("""{"theme":{"name":42}}""").ThemeName).IsNull();
    }

    [Fact]
    public void ParseSettings_ReadsTheOwnersExternalDwellTiming_AndClampsIt()
    {
        // fixup-06 M3(b): the Q8 gate is derived from the owner's real values, not a constant.
        var parsed = SetupUi.ParseSettings(
            """{"externalDwell":{"defaultDwellMs":1200,"cooldownMs":900}}""");
        Check.That(parsed.Dwell).IsEqualTo(new SetupDwellTiming(1200, 900));
        Check.That(parsed.Dwell.RestingGate).IsEqualTo(TimeSpan.FromMilliseconds(2400));

        // Missing section/fields keeps the App defaults (800 / 400 -> 1.5 s).
        Check.That(SetupUi.ParseSettings("{}").Dwell).IsEqualTo(SetupDwellTiming.Default);
        Check.That(SetupUi.ParseSettings("""{"externalDwell":{"cooldownMs":900}}""").Dwell)
            .IsEqualTo(new SetupDwellTiming(800, 900));

        // A hand-edited out-of-range value is clamped to the App's own legal range, never used raw.
        Check.That(SetupUi.ParseSettings(
                """{"externalDwell":{"defaultDwellMs":50,"cooldownMs":99999}}""").Dwell)
            .IsEqualTo(new SetupDwellTiming(200, 5000));
        Check.That(SetupUi.ParseSettings(
                """{"externalDwell":{"defaultDwellMs":"900","cooldownMs":true}}""").Dwell)
            .IsEqualTo(SetupDwellTiming.Default);
    }

    [Fact]
    public void FindLooseStringDirectory_PrefersTheInstallRoot_ThenTheFallback()
    {
        var paths = Paths("program-files", "appdata");
        var files = new EyeCtrl.Setup.Tests.TestSupport.InMemoryFileSystem();
        var fallback = Path.Combine(_root, "fallback");

        Check.That(SetupUi.FindLooseStringDirectory(paths, files, fallbackDirectory: fallback)).IsNull();

        files.AddFile(Path.Combine(fallback, StringTable.GermanFileName), "{}");
        Check.That(SetupUi.FindLooseStringDirectory(paths, files, fallbackDirectory: fallback)).IsEqualTo(fallback);

        files.AddFile(Path.Combine(paths.InstallRoot, StringTable.GermanFileName), "{}");
        Check.That(SetupUi.FindLooseStringDirectory(paths, files, fallbackDirectory: fallback)).IsEqualTo(paths.InstallRoot);
    }

    [Fact]
    public void Load_ReadsTheOwnersThemeAndTheAppsGermanTable()
    {
        var paths = Paths("program-files", "appdata");
        CopyShippedGerman(paths.InstallRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsPath)!);
        File.WriteAllText(paths.SettingsPath,
            """{"theme":{"name":"Daylight"},"general":{"uiLanguage":"german"}}""");

        var ui = SetupUi.Load(paths, new PhysicalFileSystem());

        Check.That(ui.Theme.Name).IsEqualTo("Daylight");
        Check.That(ui.Strings.Language).IsEqualTo(UiLanguage.German);
        Check.WithCustomMessage("The German table did not resolve the uninstall surface.")
            .That(ui.Strings.T("uninstall.choice.keep")).IsEqualTo("Behalten und deinstallieren");
        Check.That(ui.Strings.T("uninstall.choice.keep"))
            .Not.IsEqualTo(StringTable.EmbeddedEnglish.T("uninstall.choice.keep"));
    }

    [Fact]
    public void Load_FallsBackToTheAppFolder_WhenTheInstallRootHasNoGerman()
    {
        var paths = Paths("program-files", "appdata");
        var fallback = Path.Combine(_root, "app-folder");
        CopyShippedGerman(fallback);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsPath)!);
        File.WriteAllText(paths.SettingsPath, """{"general":{"uiLanguage":"german"}}""");

        var ui = SetupUi.Load(paths, new PhysicalFileSystem(), fallbackDirectory: fallback);

        Check.That(ui.Strings.Language).IsEqualTo(UiLanguage.German);
        Check.That(ui.Strings.T("uninstall.choice.title")).IsEqualTo("EyeCtrl deinstallieren");
    }

    [Fact]
    public void Load_ForUpdate_PrefersTheNewPackagesTable()
    {
        var paths = Paths("program-files", "appdata");
        CopyShippedGerman(paths.InstallRoot);
        var package = Path.Combine(_root, "inbox", "2.0.0");
        CopyShippedGerman(package);

        // The package's table comes from the new build; make it distinguishable from the installed
        // (old) one without changing the JSON shape.
        var packageGerman = Path.Combine(package, StringTable.GermanFileName);
        var text = File.ReadAllText(packageGerman);
        Check.That(text).Contains("Update wird installiert");
        File.WriteAllText(packageGerman, text.Replace("Update wird installiert", "Paket-Update laeuft"));

        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsPath)!);
        File.WriteAllText(paths.SettingsPath, """{"general":{"uiLanguage":"german"}}""");

        var update = SetupUi.Load(paths, new PhysicalFileSystem(), preferredDirectory: package);
        Check.WithCustomMessage("An update used the installed old table instead of the package's.")
            .That(update.Strings.T("update.progress.title")).IsEqualTo("Paket-Update laeuft");

        var installed = SetupUi.Load(paths, new PhysicalFileSystem());
        Check.That(installed.Strings.T("update.progress.title")).IsEqualTo("Update wird installiert");
    }

    [Fact]
    public void Load_VerifyFailedUsesTheInstalledTable_NotThePackages()
    {
        var paths = Paths("program-files", "appdata");
        CopyShippedGerman(paths.InstallRoot);
        var package = Path.Combine(_root, "inbox", "2.0.0");
        CopyShippedGerman(package);

        ReplaceGerman(paths.InstallRoot, "beschädigt", "INSTALL-beschädigt");
        ReplaceGerman(package, "beschädigt", "PAKET-beschädigt");

        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsPath)!);
        File.WriteAllText(paths.SettingsPath, """{"general":{"uiLanguage":"german"}}""");

        var ui = SetupUi.Load(paths, new PhysicalFileSystem(), preferredDirectory: package);
        var verifyFailed = new SetupResult { Verb = SetupVerb.Update, Outcome = SetupOutcome.VerifyFailed };
        var ok = new SetupResult { Verb = SetupVerb.Update, Outcome = SetupOutcome.Ok };

        Check.That(ui.Strings.T("update.result.verifyFailed")).Contains("PAKET-beschädigt");
        Check.That(ui.InstalledStrings.T("update.result.verifyFailed")).Contains("INSTALL-beschädigt");
        Check.WithCustomMessage("A refused package's own table supplied its refusal text.")
            .That(ui.TableFor(verifyFailed).T("update.result.verifyFailed")).Contains("INSTALL-beschädigt");
        Check.WithCustomMessage("An ordinary result stopped using the update package's table.")
            .That(ui.TableFor(ok).T("update.result.verifyFailed")).Contains("PAKET-beschädigt");

        var text = SetupDisplayText.Result(ui.TableFor(verifyFailed), verifyFailed, paths.AppDataRoot).Text;
        Check.That(text).Contains("INSTALL-beschädigt").And.Not.Contains("PAKET-beschädigt");
    }

    [Fact]
    public void Load_WithoutAnyTable_IsEnglishAndTheDefaultTheme()
    {
        var paths = Paths("program-files", "appdata");

        var ui = SetupUi.Load(paths, new PhysicalFileSystem(), fallbackDirectory: Path.Combine(_root, "nothing"));

        Check.That(ui.Theme.Name).IsEqualTo(MidnightTheme.Instance.Name);
        // System resolves against this machine's German culture, but with no loose table every key
        // falls back to the embedded English — the screen is never blank or a raw key.
        Check.That(ui.Strings.T("uninstall.choice.keep"))
            .IsEqualTo(StringTable.EmbeddedEnglish.T("uninstall.choice.keep"));
    }

    private SetupPaths Paths(string programFiles, string appData) =>
        new(Path.Combine(_root, programFiles), Path.Combine(_root, appData), Path.Combine(_root, "local"));

    private static void CopyShippedGerman(string directory)
    {
        Directory.CreateDirectory(directory);
        File.Copy(GermanDraft.GermanPath, Path.Combine(directory, StringTable.GermanFileName), overwrite: true);
        File.Copy(
            Path.Combine(Path.GetDirectoryName(GermanDraft.GermanPath)!, StringTable.ManifestFileName),
            Path.Combine(directory, StringTable.ManifestFileName),
            overwrite: true);
    }

    private static void ReplaceGerman(string directory, string oldText, string newText)
    {
        var path = Path.Combine(directory, StringTable.GermanFileName);
        File.WriteAllText(path, File.ReadAllText(path).Replace(oldText, newText));
    }
}

/// <summary>One serial collection for every test that constructs Setup's windows or changes the
/// global <see cref="Tokens"/>: WPF windows and the active theme are process-global.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SetupUiWindowsCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "Setup UI Windows";
}
