namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// T11: the SHIPPED switch-on, gated on the production manifest — not on
/// <see cref="EyeCtrl.Tests.TestSupport.GermanDraft"/>'s test manifest, which keeps every surface on
/// so T10's fit gates can measure the whole draft. The gates here pin what a user on this de-DE
/// machine actually gets: exactly the eight surfaces Step 1 (T11) and Step 2 (T12, Settings)
/// resolved German, the Step-3 surfaces stay English, and every <c>crisis</c> key's surface is one
/// of the seven Step-1 surfaces.
///
/// <para>
/// Crises assert German <b>literals</b>, per the story's convention; resolving both sides from the
/// keys would let a missing key pass as its own raw text.
/// </para>
/// </summary>
public class ShippedGermanSwitchOnTests
{
    /// <summary>
    /// The manifest pin: the shipped file names exactly the switched-on surfaces, in sorted order
    /// (the validator's own rule), the copy beside the exe is the same file, and the eight plus the
    /// three held back account for every surface in <see cref="StringTableSchema.Surfaces"/> — a new
    /// surface fails here rather than silently escaping the decision.
    /// </summary>
    [Fact]
    public void TheShippedManifest_NamesExactlyTheSwitchedOnSurfaces()
    {
        var surfaces = ShippedGerman.ManifestSurfaces();
        var deployed = ShippedGerman.ManifestSurfaces(
            Path.Combine(AppContext.BaseDirectory, StringTable.ManifestFileName));

        Check.WithCustomMessage(
                "The shipped manifest changed. Step 1 is the seven crisis surfaces and Step 2 adds `settings`, "
                + "sorted; moving a surface in or out is a switch-on decision (T11–T13), not a test update.")
            .That(string.Join(",", surfaces)).IsEqualTo(string.Join(",", ShippedGerman.SwitchedOn));

        Check.WithCustomMessage("The manifest beside the exe differs from the repo's source copy; the build did not ship it.")
            .That(string.Join(",", deployed)).IsEqualTo(string.Join(",", surfaces));

        var partition = ShippedGerman.SwitchedOn.Concat(ShippedGerman.StillEnglish)
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
        var schema = StringTableSchema.Surfaces.OrderBy(s => s, StringComparer.Ordinal).ToList();
        Check.WithCustomMessage(
                "A surface is neither switched on by Steps 1–2 nor held back by the test list; update the partition "
                + "deliberately when a surface ships.")
            .That(partition).IsEqualTo(schema);
    }

    /// <summary>
    /// The routing: loading through production's own loader on a de-DE System setting gives German
    /// for the eight and English for the rest. The format provider is the exact effective-surface
    /// witness (de-DE only where German actually renders), so it pins the whole effective set, not
    /// a sample; the literals pin the text itself.
    /// </summary>
    [Fact]
    public void TheShippedTable_OnADeDeSystem_ShowsGermanForTheSwitchedOn_AndEnglishForTheRest()
    {
        var warnings = new List<string>();
        var table = ShippedGerman.Load(warnings.Add);
        var english = StringTable.EmbeddedEnglish;

        Check.WithCustomMessage("Loading the shipped German warned:\n  " + string.Join("\n  ", warnings))
            .That(warnings).IsEmpty();
        Check.That(table.Selection).IsEqualTo(UiLanguage.System);
        Check.That(table.Language).IsEqualTo(UiLanguage.German);

        foreach (var surface in ShippedGerman.SwitchedOn)
            Check.WithCustomMessage($"{surface} does not resolve German: it is not effective in the shipped table.")
                .That(ProviderName(table, surface)).IsEqualTo("de-DE");
        foreach (var surface in ShippedGerman.StillEnglish)
            Check.WithCustomMessage($"{surface} resolves German, but its Step is not shipped; the manifest leaked it.")
                .That(ProviderName(table, surface)).IsEqualTo(CultureInfo.InvariantCulture.Name);

        Check.That(table.T("app-tray.settings")).IsEqualTo("Einstellungen");
        Check.That(table.T("caregiver-alert.title")).IsEqualTo("Bitte kurz nachsehen");
        Check.That(table.T("drift-notice.message"))
            .IsEqualTo("Der Blickpunkt weicht vielleicht ab – bitte in Tobii neu kalibrieren");
        Check.That(table.T("language-recovery.prompt.message")).IsEqualTo("Die neue Sprache gilt nach einem Neustart.");
        Check.That(table.T("startup-error.title")).IsEqualTo("EyeCtrl konnte nicht starten");
        Check.That(table.T("supervisor.fallback.title")).IsEqualTo("EyeCtrl läuft nicht mehr");
        Check.That(table.T("track-status.quality.good")).IsEqualTo("Gut");
        Check.That(table.T("settings.tab.general.label")).IsEqualTo("Allgemein");
        Check.That(table.T("settings.tab.quickactions.label")).IsEqualTo("Kurzbefehle");

        Check.WithCustomMessage("The Step-3 calibration, eye care and overlays would already show German; only Steps 1–2 ship.")
            .That(table.T("calibration.window.retry")).IsEqualTo(english.T("calibration.window.retry"));
        Check.That(table.T("eye-care.banner.message")).IsEqualTo(english.T("eye-care.banner.message"));
        Check.That(table.T("overlays.ring.leftClick")).IsEqualTo(english.T("overlays.ring.leftClick"));
    }

    /// <summary>
    /// The switched-on crisis surfaces at their real sites, resolved from the shipped table: the App
    /// tray record, the caregiver alert's two messages, the TrackStatus quality words and the
    /// startup-error box. German literals on the expected side, per the story's convention.
    /// </summary>
    [Fact]
    public void TheCrisisSurfaces_InTheShippedGerman_ReadTheirGermanAtTheirSites()
    {
        var table = ShippedGerman.Load();

        Check.That(AppTrayTexts.For(table)).IsEqualTo(new AppTrayTexts(
            "Pausieren", "Fortsetzen", "Einstellungen", "Anpassung zurücksetzen", "Beenden", "Symbolleiste anzeigen"));

        Check.That(CaregiverAlertWindow.MessageFor(table, CaregiverAlertCondition.SustainedPoorTracking))
            .IsEqualTo("Die Blickverfolgung ist seit einer Minute unzuverlässig – bitte Sitzposition prüfen, "
                       + "Eyetracker ausrichten oder in Tobii neu kalibrieren.");
        Check.That(CaregiverAlertWindow.MessageFor(table, CaregiverAlertCondition.SustainedStruggle))
            .IsEqualTo("Die Bedienung fällt seit einer Weile schwer – bitte nachsehen.");

        Check.That(TrackStatusWindow.QualityWord(table, DistanceQuality.Good)).IsEqualTo("Gut");
        Check.That(TrackStatusWindow.QualityWord(table, DistanceQuality.Fair)).IsEqualTo("Mittel");
        Check.That(TrackStatusWindow.QualityWord(table, DistanceQuality.Poor)).IsEqualTo("Schlecht");
        Check.That(TrackStatusWindow.QualityWord(table, DistanceQuality.Unknown)).IsEqualTo("Unbekannt");
    }

    /// <summary>
    /// Every <c>crisis: true</c> key's surface is one of the seven Step-1 surfaces — a literal set,
    /// so a new crisis key anywhere else fails deliberately. The validator's "every crisis key is
    /// present" gate can only see keys of manifest surfaces, so a crisis key on an unlisted surface
    /// would slip past it. The German side of each crisis key is checked by the validator (missing
    /// key per manifest surface) and by the load warnings above.
    /// </summary>
    [Fact]
    public void EveryCrisisKey_ShipsWithStepOne()
    {
        using var document = LocalizationTestData.EnglishDocument();
        var surfaces = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.StartsWith('_')) continue;
            if (!property.Value.TryGetProperty("crisis", out var crisis) || crisis.GetBoolean() is false) continue;
            surfaces.Add(property.Value.GetProperty("where").GetString()!);
        }

        Check.WithCustomMessage("No crisis keys were found: the gate would pass vacuously.")
            .That(surfaces).Not.IsEmpty();
        Check.WithCustomMessage("A crisis surface is not switched on; its keys would stay English.")
            .That(ShippedGerman.SwitchedOn).Contains(surfaces);
        Check.WithCustomMessage(
                "The set of crisis surfaces changed. Crisis keys exist only on the Step-1 surfaces; a new "
                + "crisis key on another surface is a switch-on decision, not a test update.")
            .That(surfaces.ToList()).IsEqualTo(new List<string>
            {
                "app-tray", "caregiver-alert", "drift-notice", "language-recovery",
                "startup-error", "supervisor", "track-status"
            });
    }

    private static string ProviderName(StringTable table, string surface) =>
        ((CultureInfo)table.FormatProviderFor(surface)).Name;
}
