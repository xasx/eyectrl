namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using NFluent;
using Xunit;

/// <summary>
/// T06: the startup-error box's texts resolve through the process table, and they must render even
/// when loading that table is what failed — the case the box exists for. The expected strings are
/// English literals by design: resolving both sides from the keys would let a missing key pass as
/// its own raw text.
/// </summary>
[Collection("StringTable Tests (App)")]
public class StartupErrorTextTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "eyectrl-startup-error-" + Guid.NewGuid().ToString("N"));

    public StartupErrorTextTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void TheBox_RendersEnglish_WhenTheTableWasNeverActivated()
    {
        // The startup path the box exists for: InitializeSettingsAndLogging threw before
        // StringTable.Activate ran, so the process table is still the compiled English default.
        StringTable.ResetToEmbeddedEnglish();

        var text = StartupErrorText.Resolve("boom");

        Check.That(text.Title).IsEqualTo("EyeCtrl \u2014 Startup Error");
        Check.That(text.Message).IsEqualTo("EyeCtrl failed to start:\nTechnical details: boom");
    }

    [Fact]
    public void TheBox_RendersEnglish_WhenTheTableLoadIsDegraded()
    {
        // A corrupt loose German file must not take the box down: the load degrades to embedded
        // English with a warning (never throws), and the box still resolves.
        File.WriteAllText(Path.Combine(_directory, StringTable.GermanFileName), "{ not json");
        File.WriteAllText(Path.Combine(_directory, StringTable.ManifestFileName), "{ not json");
        var warnings = new List<string>();
        StringTable.Activate(StringTable.Load(
            UiLanguage.German, CultureInfo.GetCultureInfo("de-DE"), _directory, warnings.Add));

        try
        {
            var text = StartupErrorText.Resolve("boom");

            Check.That(warnings).Not.IsEmpty();
            Check.That(text.Title).IsEqualTo("EyeCtrl \u2014 Startup Error");
            Check.That(text.Message).IsEqualTo("EyeCtrl failed to start:\nTechnical details: boom");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [Fact]
    public void TheDetail_IsLabelled_AndTheRawMessageStaysVerbatim()
    {
        // The one English wording change of T06: the raw detail (English, or the Windows language
        // for OS errors) gains its label and is otherwise untouched.
        var text = StartupErrorText.Resolve("Zugriff verweigert. (0x80070005)");

        Check.That(text.Message).IsEqualTo(
            "EyeCtrl failed to start:\nTechnical details: Zugriff verweigert. (0x80070005)");
    }
}
