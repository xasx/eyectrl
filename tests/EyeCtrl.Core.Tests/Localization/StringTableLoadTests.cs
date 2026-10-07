namespace EyeCtrl.Core.Tests.Localization;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using NFluent;
using Xunit;

/// <summary>
/// The load path: embedded English always, the loose German file and manifest beside the App exe.
/// A missing or corrupt loose file means English plus a log — never a failed start, because the
/// startup-error box itself renders through this loader.
/// </summary>
public class StringTableLoadTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "eyectrl-strings-" + Guid.NewGuid().ToString("N"));

    public StringTableLoadTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private void WriteGerman(string json) =>
        File.WriteAllText(Path.Combine(_directory, StringTable.GermanFileName), json);

    private void WriteManifest(string json) =>
        File.WriteAllText(Path.Combine(_directory, StringTable.ManifestFileName), json);

    [Fact]
    public void EmbeddedResource_IsCompiledIn_AndParses()
    {
        using var stream = typeof(StringTable).Assembly
            .GetManifestResourceStream(StringTable.EmbeddedResourceName);

        Check.WithCustomMessage(
                $"The embedded resource '{StringTable.EmbeddedResourceName}' is missing from EyeCtrl.Core, "
                + "so English has no compiled floor. Check the EmbeddedResource item (and its LogicalName) "
                + "in EyeCtrl.Core.csproj.")
            .That(stream).IsNotNull();

        using var reader = new StreamReader(stream!);
        var warnings = new List<string>();
        var content = StringTableContent.ParseEnglish(reader.ReadToEnd(), warnings.Add);
        Check.That(content).IsNotNull();
        Check.That(warnings).IsEmpty();
    }

    [Fact]
    public void NoDirectory_UsesCompiledEnglish_WithoutWarnings()
    {
        var warnings = new List<string>();
        var table = StringTable.Load(UiLanguage.German, CultureInfo.GetCultureInfo("de-DE"), null, warnings.Add);

        Check.That(table.Language).IsEqualTo(UiLanguage.German);
        Check.That(warnings).IsEmpty();
        Check.That(table.T("settings.alpha")).IsEqualTo("settings.alpha");
    }

    [Fact]
    public void MissingLooseFiles_WarnAndStayEnglish()
    {
        var warnings = new List<string>();
        var table = StringTable.Load(
            UiLanguage.German, CultureInfo.GetCultureInfo("de-DE"), _directory, warnings.Add);

        Check.That(table.T("settings.alpha")).IsEqualTo("settings.alpha");
        Check.That(warnings).HasElementThatMatches(w => w.Contains(StringTable.GermanFileName, StringComparison.Ordinal));
        Check.That(warnings).HasElementThatMatches(w => w.Contains(StringTable.ManifestFileName, StringComparison.Ordinal));
    }

    [Fact]
    public void ValidLooseFiles_AreFoundWithoutWarnings()
    {
        // The shipped English table is empty at this point in the plan, so no key can resolve to
        // German yet — what this pins is that both loose files were located and parsed cleanly.
        WriteGerman("{}");
        WriteManifest("[]");

        var warnings = new List<string>();
        var table = StringTable.Load(
            UiLanguage.German, CultureInfo.GetCultureInfo("de-DE"), _directory, warnings.Add);

        Check.That(table.Language).IsEqualTo(UiLanguage.German);
        Check.That(warnings).IsEmpty();
    }

    [Fact]
    public void CorruptGerman_IsEnglishWithALog()
    {
        WriteGerman("{ not json");
        WriteManifest("[]");

        var warnings = new List<string>();
        var table = StringTable.Load(
            UiLanguage.German, CultureInfo.GetCultureInfo("de-DE"), _directory, warnings.Add);

        Check.That(table.Language).IsEqualTo(UiLanguage.German);
        Check.That(warnings).HasElementThatMatches(w => w.Contains("not valid JSON", StringComparison.Ordinal));
    }

    [Fact]
    public void CorruptManifest_IsEnglishWithALog()
    {
        WriteGerman("{}");
        WriteManifest("{ \"settings\": true }");

        var warnings = new List<string>();
        var table = StringTable.Load(
            UiLanguage.German, CultureInfo.GetCultureInfo("de-DE"), _directory, warnings.Add);

        Check.That(warnings).HasElementThatMatches(
            w => w.Contains(StringTable.ManifestFileName, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("[ \"settings\"")]   // truncated mid-write
    [InlineData("{ not json")]       // not JSON at all
    public void UnparseableManifest_IsEnglishWithALog_AndNeverThrows(string json)
    {
        WriteGerman("{}");
        WriteManifest(json);

        var warnings = new List<string>();
        var table = StringTable.Load(
            UiLanguage.German, CultureInfo.GetCultureInfo("de-DE"), _directory, warnings.Add);

        Check.That(table.Language).IsEqualTo(UiLanguage.German);
        Check.That(table.T("settings.alpha")).IsEqualTo("settings.alpha");
        Check.That(warnings).HasElementThatMatches(
            w => w.Contains(StringTable.ManifestFileName, StringComparison.Ordinal));
    }

    [Fact]
    public void ManifestWithANonStringEntry_WarnsAndIgnoresIt()
    {
        WriteGerman("{}");
        WriteManifest("""[ "settings", 5 ]""");

        var warnings = new List<string>();
        _ = StringTable.Load(UiLanguage.German, CultureInfo.GetCultureInfo("de-DE"), _directory, warnings.Add);

        Check.That(warnings).HasElementThatMatches(w => w.Contains("non-string", StringComparison.Ordinal));
    }
}
