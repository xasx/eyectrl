namespace EyeCtrl.Supervisor.Tests.Supervision;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Supervisor.Supervision;
using EyeCtrl.Tests.TestSupport;
using NFluent;
using Xunit;

/// <summary>
/// The Supervisor's lazy lookup: the language comes from the settings file under the writer mutex,
/// and the loose German files are searched beside the App exe it already resolves. Nothing places
/// the Supervisor beside the App today (TODO installer), so each test lays out its own install
/// folder and points the resolver at it.
/// </summary>
[Collection(AppExeEnvironmentCollection.Name)]
public class SupervisorLanguageResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "eyectrl-sup-lang-" + Guid.NewGuid().ToString("N"));
    private readonly string _install;
    private readonly string _settingsPath;
    private readonly string _appExePath;
    private readonly string _writerMutexName =
        @"Local\EyeCtrl.Settings.Writer.Test." + Guid.NewGuid().ToString("N");

    public SupervisorLanguageResolverTests()
    {
        _install = Path.Combine(_root, "install");
        Directory.CreateDirectory(_install);
        _settingsPath = Path.Combine(_root, "settings.json");
        _appExePath = Path.Combine(_install, "EyeCtrl.App.exe");
        File.WriteAllText(_appExePath, string.Empty);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private SupervisorLanguageResolver Resolver() =>
        new(_settingsPath, appExePath: _appExePath, writerMutexName: _writerMutexName);

    private void WriteLanguage(string token) =>
        File.WriteAllText(_settingsPath, "{\"general\":{\"uiLanguage\":\"" + token + "\"}}");

    [Fact]
    public void ResolveLanguage_ReadsTheSettingsFile()
    {
        WriteLanguage("english");
        Check.That(Resolver().ResolveLanguage()).IsEqualTo(UiLanguage.English);

        WriteLanguage("german");
        Check.That(Resolver().ResolveLanguage()).IsEqualTo(UiLanguage.German);
    }

    [Fact]
    public void ResolveTable_FindsTheLooseFilesBesideTheResolvedAppExe()
    {
        WriteLanguage("german");
        File.WriteAllText(Path.Combine(_install, StringTable.GermanFileName), "{}");
        File.WriteAllText(Path.Combine(_install, StringTable.ManifestFileName), "[]");

        var warnings = new List<string>();
        var table = Resolver().ResolveTable(warnings.Add);

        Check.That(table.Language).IsEqualTo(UiLanguage.German);
        Check.That(warnings).IsEmpty();
    }

    [Fact]
    public void ResolveTable_MissingLooseFiles_WarnsAboutTheInstallFolder()
    {
        WriteLanguage("german");

        var warnings = new List<string>();
        _ = Resolver().ResolveTable(warnings.Add);

        Check.That(warnings).HasElementThatMatches(w => w.Contains(StringTable.GermanFileName, StringComparison.Ordinal));
        Check.That(warnings).HasElementThatMatches(w => w.Contains(StringTable.ManifestFileName, StringComparison.Ordinal));
    }

    [Fact]
    public void ResolverWithoutAnExplicitPath_UsesTheResolvedAppExe()
    {
        // The install folder is exposed through EYECTRL_APP_EXE, the same way the installer will
        // supply the canonical path; the lookup must follow the resolved exe, not the Supervisor's
        // own directory.
        WriteLanguage("german");
        File.WriteAllText(Path.Combine(_install, StringTable.GermanFileName), "{}");
        File.WriteAllText(Path.Combine(_install, StringTable.ManifestFileName), "[]");

        var original = Environment.GetEnvironmentVariable(AppExePathResolver.AppExeEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AppExePathResolver.AppExeEnvVar, _appExePath);
            var warnings = new List<string>();
            var table = new SupervisorLanguageResolver(_settingsPath, writerMutexName: _writerMutexName)
                .ResolveTable(warnings.Add);

            Check.That(table.Language).IsEqualTo(UiLanguage.German);
            Check.That(warnings).IsEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppExePathResolver.AppExeEnvVar, original);
        }
    }

    // ── T05b: the pending choice first, then the setting ────────────────────────────────────

    [Fact]
    public void APendingChoice_WinsOverTheSetting()
    {
        WriteLanguage("english");
        UiLanguage? pending = UiLanguage.German;
        var resolver = new SupervisorLanguageResolver(
            _settingsPath, appExePath: _appExePath, writerMutexName: _writerMutexName, pending: () => pending);

        Check.That(resolver.ResolveLanguage()).IsEqualTo(UiLanguage.German);

        pending = null;                          // cleared once the re-read found it saved
        Check.That(resolver.ResolveLanguage()).IsEqualTo(UiLanguage.English);
    }

    /// <summary>
    /// The production wiring, pinned by source (Program.cs is integration wiring that cannot be
    /// composed in a test): the resolver the Supervisor builds is handed the delivery's pending
    /// choice. Dropping <c>pending:</c> leaves every resolver test green, since they build their own
    /// resolvers, while the Supervisor's own screens stop following the caretaker's choice at once
    /// (holistic review F2).
    /// </summary>
    [Fact]
    public void TheSupervisorsResolver_IsWiredToTheLanguageDeliverysPendingChoice()
    {
        var program = File.ReadAllText(
            Path.Combine(GermanDraft.RepoRoot(), "src", "EyeCtrl.Supervisor", "Program.cs"));

        var construction = Regex.Matches(program, @"new\s+SupervisorLanguageResolver\s*\(([^;]*)\)\s*;");
        Check.WithCustomMessage("Program.cs no longer constructs exactly one SupervisorLanguageResolver.")
            .That(construction.Count).IsEqualTo(1);
        Check.WithCustomMessage(
                "The resolver must take `pending: () => _languageDelivery.Pending`, the delivery that "
                + "SetupRow writes the caretaker's choice into.")
            .That(Regex.IsMatch(
                construction[0].Groups[1].Value,
                @"pending\s*:\s*\(\s*\)\s*=>\s*_languageDelivery\s*\.\s*Pending\b")).IsTrue();
    }

    [Fact]
    public void TheNextWindow_FollowsAnAppSideChange_WithoutARestart()
    {
        // The setup and FallenBack windows resolve their table when they open. A change made in
        // EyeCtrl itself reaches the next window through the guarded disk read.
        WriteLanguage("english");
        File.WriteAllText(Path.Combine(_install, StringTable.GermanFileName), "{}");
        File.WriteAllText(Path.Combine(_install, StringTable.ManifestFileName), "[]");
        var resolver = Resolver();
        Check.That(resolver.ResolveTable().Language).IsEqualTo(UiLanguage.English);

        WriteLanguage("german");                 // the App's own language row saved German

        Check.That(resolver.ResolveTable().Language).IsEqualTo(UiLanguage.German);
    }
}
