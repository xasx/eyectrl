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

    // A real surface with real keys: `startup-error` is two keys, so a complete German file for it
    // is small, and its English is the shipped literal the "stays English" tests assert. (A key no
    // table defines would also read as itself, which passes whether German, English or nothing
    // loaded: the vacuous check the holistic review found.)
    private const string TitleKey = "startup-error.title";
    private const string MessageKey = "startup-error.message";
    private const string EnglishTitle = "EyeCtrl — Startup Error";
    private const string GermanTitle = "EyeCtrl konnte nicht starten (Testwert)";

    private const string CompleteGermanForStartupError = """
        {
          "startup-error.title": { "text": "EyeCtrl konnte nicht starten (Testwert)" },
          "startup-error.message": { "text": "Fehler.\nTechnische Details: {0}" }
        }
        """;

    private static StringTable LoadGerman(string? directory, List<string> warnings) =>
        StringTable.Load(UiLanguage.German, CultureInfo.GetCultureInfo("de-DE"), directory, warnings.Add);

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
        Check.That(table.T(TitleKey)).IsEqualTo(EnglishTitle);
    }

    [Fact]
    public void MissingLooseFiles_WarnAndStayEnglish()
    {
        var warnings = new List<string>();
        var table = StringTable.Load(
            UiLanguage.German, CultureInfo.GetCultureInfo("de-DE"), _directory, warnings.Add);

        Check.That(table.T(TitleKey)).IsEqualTo(EnglishTitle);
        Check.That(warnings).HasElementThatMatches(w => w.Contains(StringTable.GermanFileName, StringComparison.Ordinal));
        Check.That(warnings).HasElementThatMatches(w => w.Contains(StringTable.ManifestFileName, StringComparison.Ordinal));
    }

    [Fact]
    public void ValidLooseFiles_AreFoundWithoutWarnings_AndTheSurfaceResolvesGerman()
    {
        WriteGerman(CompleteGermanForStartupError);
        WriteManifest("""[ "startup-error" ]""");

        var warnings = new List<string>();
        var table = LoadGerman(_directory, warnings);

        Check.That(table.Language).IsEqualTo(UiLanguage.German);
        Check.That(warnings).IsEmpty();
        Check.That(table.T(TitleKey)).IsEqualTo(GermanTitle);
        Check.That(table.T(MessageKey, "boom")).IsEqualTo("Fehler.\nTechnische Details: boom");
    }

    [Fact]
    public void AnEmptyManifest_KeepsEverySurfaceEnglish_EvenWithACompleteGermanFile()
    {
        WriteGerman(CompleteGermanForStartupError);
        WriteManifest("[]");

        var warnings = new List<string>();
        var table = LoadGerman(_directory, warnings);

        Check.That(warnings).IsEmpty();
        Check.That(table.T(TitleKey)).IsEqualTo(EnglishTitle);
    }

    [Fact]
    public void AValidGermanFile_NextToAMissingManifest_StaysEnglishWithALog()
    {
        WriteGerman(CompleteGermanForStartupError);

        var warnings = new List<string>();
        var table = LoadGerman(_directory, warnings);

        Check.That(table.T(TitleKey)).IsEqualTo(EnglishTitle);
        Check.That(warnings).HasElementThatMatches(
            w => w.Contains(StringTable.ManifestFileName, StringComparison.Ordinal));
    }

    [Fact]
    public void CorruptGerman_IsEnglishWithALog()
    {
        WriteGerman("{ not json");
        WriteManifest("""[ "startup-error" ]""");

        var warnings = new List<string>();
        var table = StringTable.Load(
            UiLanguage.German, CultureInfo.GetCultureInfo("de-DE"), _directory, warnings.Add);

        Check.That(table.Language).IsEqualTo(UiLanguage.German);
        Check.That(table.T(TitleKey)).IsEqualTo(EnglishTitle);
        Check.That(warnings).HasElementThatMatches(w => w.Contains("not valid JSON", StringComparison.Ordinal));
    }

    [Fact]
    public void CorruptManifest_IsEnglishWithALog()
    {
        WriteGerman(CompleteGermanForStartupError);
        WriteManifest("{ \"settings\": true }");

        var warnings = new List<string>();
        var table = StringTable.Load(
            UiLanguage.German, CultureInfo.GetCultureInfo("de-DE"), _directory, warnings.Add);

        Check.That(table.T(TitleKey)).IsEqualTo(EnglishTitle);
        Check.That(warnings).HasElementThatMatches(
            w => w.Contains(StringTable.ManifestFileName, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("[ \"settings\"")]   // truncated mid-write
    [InlineData("{ not json")]       // not JSON at all
    public void UnparseableManifest_IsEnglishWithALog_AndNeverThrows(string json)
    {
        WriteGerman(CompleteGermanForStartupError);
        WriteManifest(json);

        var warnings = new List<string>();
        var table = StringTable.Load(
            UiLanguage.German, CultureInfo.GetCultureInfo("de-DE"), _directory, warnings.Add);

        Check.That(table.Language).IsEqualTo(UiLanguage.German);
        Check.That(table.T(TitleKey)).IsEqualTo(EnglishTitle);
        Check.That(warnings).HasElementThatMatches(
            w => w.Contains(StringTable.ManifestFileName, StringComparison.Ordinal));
    }

    [Fact]
    public void ManifestWithANonStringEntry_WarnsAndIgnoresIt()
    {
        WriteGerman(CompleteGermanForStartupError);
        WriteManifest("""[ "startup-error", 5 ]""");

        var warnings = new List<string>();
        var table = LoadGerman(_directory, warnings);

        Check.That(warnings).HasElementThatMatches(w => w.Contains("non-string", StringComparison.Ordinal));
        // The valid entry is still honoured: only the non-string one is dropped.
        Check.That(table.T(TitleKey)).IsEqualTo(GermanTitle);
    }
}
