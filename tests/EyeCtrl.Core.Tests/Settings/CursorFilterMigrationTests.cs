namespace EyeCtrl.Core.Tests.Settings;

using System.Text.Json.Nodes;
using EyeCtrl.Core.Settings;

/// <summary>
/// The Stage 3 filter-settings migration. Two things are being defended here, and they pull in opposite
/// directions:
/// <list type="bullet">
/// <item><description>
/// A changed default is INERT without this. <c>Load</c> deserialises straight onto the object graph and
/// <c>Save</c> writes every property, so the one machine the retune was designed for would keep running
/// the old numbers forever and nobody would notice.
/// </description></item>
/// <item><description>
/// A migration that produces an out-of-range value costs the user their ENTIRE settings file: the
/// generated setter throws during deserialisation, and <c>Load</c> answers any exception with
/// all-defaults. So the conversion clamps, and the whole-file survival is asserted, not assumed.
/// </description></item>
/// </list>
/// </summary>
public class CursorFilterMigrationTests
{
    private const double LegacySpeedNormalization = 2000.0;   // OneEuroFilter.SpeedNormalizationPixels

    [Fact]
    public void OldDefaults_BecomeTheNewDefaults()
    {
        // The case that matters on the user's own machine: a profile that never touched either knob.
        Run(
            legacyJson: Cursor(minCutoff: 5.0, legacyBeta: 0.5),
            assert: s =>
            {
                var d = new CursorSettings();
                Check.That(s.Cursor.OneEuroMinCutoff).IsEqualTo(d.OneEuroMinCutoff);
                Check.That(s.Cursor.OneEuroBetaPerDiagonal).IsEqualTo(d.OneEuroBetaPerDiagonal);
                Check.That(d.OneEuroMinCutoff).IsEqualTo(1.5);      // and the defaults really moved
                Check.That(d.OneEuroBetaPerDiagonal).IsEqualTo(20.0);
            });
    }

    [Fact]
    public void AUserChosenBeta_IsConvertedIntoTheNewUnits_NotCopiedOrDiscarded()
    {
        // "Preserve values a user explicitly changed" has to mean preserve the BEHAVIOUR: the units
        // changed underneath the number, so copying 0.02 across would silently make the filter 2000x
        // less speed-adaptive than the user set it to be.
        Run(
            legacyJson: Cursor(minCutoff: 2.0, legacyBeta: 0.02),
            assert: s =>
            {
                Check.That(s.Cursor.OneEuroBetaPerDiagonal).IsEqualTo(0.02 * LegacySpeedNormalization);
                Check.That(s.Cursor.OneEuroMinCutoff).IsEqualTo(2.0);   // not the old default → untouched
            });
    }

    [Fact]
    public void AConvertedBetaThatLeavesTheNewRange_IsClamped_AndTheRestOfTheFileSurvives()
    {
        // The file-wipe trap, discharged end to end. 0.2 px-units converts to 400, far outside the new
        // [0, 60] guard — and an out-of-range value in the JSON makes the ObservableProperty setter
        // throw inside Deserialize, which Load turns into "here are your defaults, all of them".
        // The unrelated dwell value is the witness that nothing else was lost.
        Run(
            legacyJson: Cursor(minCutoff: 3.0, legacyBeta: 0.2, extra: "\"dwell\": { \"activationButtonDwellMs\": 850 }"),
            assert: s =>
            {
                Check.That(s.Cursor.OneEuroBetaPerDiagonal).IsEqualTo(60.0);
                Check.That(s.Cursor.OneEuroMinCutoff).IsEqualTo(3.0);
                Check.That(s.Dwell.ActivationButtonDwellMs).IsEqualTo(850);   // the file was NOT reset
            });
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.001)]
    [InlineData(0.01)]
    [InlineData(0.03)]
    [InlineData(0.5)]
    [InlineData(5.0)]        // the top of the OLD slider
    [InlineData(-2.0)]       // hand-edited nonsense
    [InlineData(1e9)]
    public void WhateverTheLegacyFileHeld_TheMigratedValueIsAssignableToTheSettingsGuard(double legacyBeta)
    {
        // The assignment-back oracle for this migration's duplicated clamp literal. SettingsService
        // restates the [0, 60] range that CursorSettings' setter owns, and the two can drift.
        //
        // TWO oracles here, and it is worth being precise about which catches what, because the obvious
        // reading of the first one is wrong:
        //
        //   * The ASSIGNMENT catches a clamp that produces a value the guard rejects. It only works
        //     through AssignThroughGuard — [ObservableProperty]'s setter compares before it calls the
        //     Changing guard, so assigning a value onto itself (as this test originally did) can never
        //     throw no matter how far the clamp has drifted.
        //   * The `minCutoff == 2.0` ASSERTION is the blunter and, in practice, stronger one: an
        //     out-of-range value makes the setter throw during deserialisation, Load answers that with
        //     ALL defaults, and minCutoff comes back 1.5 instead of the file's 2.0. That is also the
        //     real-world consequence being guarded — an unclamped conversion costs the user their whole
        //     settings file, not merely a bad beta.
        Run(
            legacyJson: Cursor(minCutoff: 2.0, legacyBeta: legacyBeta),
            assert: s =>
            {
                Check.That(s.Cursor.OneEuroMinCutoff).IsEqualTo(2.0);   // proves Load did NOT reset the file
                Check.That(double.IsFinite(s.Cursor.OneEuroBetaPerDiagonal)).IsTrue();
                AssignThroughGuard(v => s.Cursor.OneEuroBetaPerDiagonal = v,
                    s.Cursor.OneEuroBetaPerDiagonal, probeA: 1.0, probeB: 2.0);
            });
    }

    /// <summary>
    /// Assigns <paramref name="value"/> so that it is guaranteed to reach <c>OnXxxChanging</c>.
    /// <c>[ObservableProperty]</c>'s generated setter returns early on an unchanged value, so an
    /// assignment onto itself never reaches the guard. Both probes must be inside the guarded range.
    /// </summary>
    private static void AssignThroughGuard(Action<double> set, double value, double probeA, double probeB)
    {
        set(value == probeA ? probeB : probeA);
        set(value);
    }

    [Theory]
    [InlineData("1e400")]              // legal JSON, overflows a double -> +Infinity
    [InlineData("-1e400")]             // -> -Infinity
    [InlineData("\"not a number\"")]   // legal JSON, not a number at all
    [InlineData("null")]
    public void AnUnusableLegacyBeta_LandsOnTheDefault_NotOnTheEdgeOfTheRange(string rawJsonValue)
    {
        // "1e400" is the reachable one, and it is why the non-finite branch is not decoration: the JSON
        // parses, TryGetValue really does hand back positive Infinity (probed, not assumed), and
        // Math.Clamp alone would resolve that to 60 — the MAXIMUM beta, the least smoothing available.
        // Handing the least smoothing to someone whose settings file was garbage is the wrong direction
        // to fail in, so every unusable form resolves to the default instead.
        //
        // The other rows take different routes to the same place (the migration's fallbacks, or the key
        // simply being ignored at deserialisation). The destination is the property, not the route.
        var path = TempFile();
        try
        {
            File.WriteAllText(path, $$"""
            { "cursor": { "oneEuroMinCutoff": 2.0, "oneEuroBeta": {{rawJsonValue}} } }
            """);
            var loaded = new SettingsService(path).Load();

            Check.That(loaded.Cursor.OneEuroBetaPerDiagonal).IsEqualTo(new CursorSettings().OneEuroBetaPerDiagonal);
            Check.That(loaded.Cursor.OneEuroMinCutoff).IsEqualTo(2.0);   // and the file was not reset
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ABetaOfZero_IsPreservedAsZero()
    {
        // beta 0 was the ONLY useful setting the old slider could express (the debate's point about it),
        // so a deliberate 0 is the likeliest hand-set value in the wild. 0 x anything is still 0, but
        // the default-detection rule must not mistake it for "untouched" and push it to 20.
        Run(
            legacyJson: Cursor(minCutoff: 1.5, legacyBeta: 0.0),
            assert: s => Check.That(s.Cursor.OneEuroBetaPerDiagonal).IsEqualTo(0.0));
    }

    [Fact]
    public void AUserChosenMinCutoff_IsPreserved_AndOnlyTheOldDefaultMoves()
    {
        Run(
            legacyJson: Cursor(minCutoff: 12.0, legacyBeta: 0.5),
            assert: s =>
            {
                Check.That(s.Cursor.OneEuroMinCutoff).IsEqualTo(12.0);
                Check.That(s.Cursor.OneEuroBetaPerDiagonal).IsEqualTo(20.0);   // beta WAS the old default
            });
    }

    [Fact]
    public void TheMigrationRunsOnce_AndASecondLoadChangesNothing()
    {
        // The detection rule's real requirement. It keys on the JSON KEY, not on the value, precisely so
        // a user who later dials the new beta down into the old key's numeric range is not re-migrated
        // (and multiplied by 2000) on the next startup.
        var path = TempFile();
        try
        {
            File.WriteAllText(path, Cursor(minCutoff: 5.0, legacyBeta: 0.5));
            var service = new SettingsService(path);
            service.Load();

            // Now the user picks a small new-units beta — a value that sits inside the OLD range.
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            root["cursor"]!["oneEuroBetaPerDiagonal"] = 0.5;
            File.WriteAllText(path, root.ToJsonString());

            var reloaded = service.Load();

            Check.That(reloaded.Cursor.OneEuroBetaPerDiagonal).IsEqualTo(0.5);
            Check.That(File.ReadAllText(path)).Not.Contains("\"oneEuroBeta\"");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheMigratedFormReachesDisk_Atomically_AndLeavesNoTempFile()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, Cursor(minCutoff: 5.0, legacyBeta: 0.5));
            new SettingsService(path).Load();

            Check.That(File.Exists(path + ".tmp")).IsFalse();
            string onDisk = File.ReadAllText(path);
            Check.That(onDisk).Not.Contains("\"oneEuroBeta\"");
            Check.That(onDisk).Contains("oneEuroBetaPerDiagonal");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AFileCarryingBothKeys_KeepsTheNewOne_AndDropsTheStaleOne()
    {
        // Belt and braces for a mixed-version pair of processes, or a hand-edit that re-added the old
        // key. Re-running the conversion over an already-migrated value would multiply it by 2000 again.
        var path = TempFile();
        try
        {
            File.WriteAllText(path, """
            {
              "cursor": { "oneEuroMinCutoff": 5.0, "oneEuroBeta": 0.5, "oneEuroBetaPerDiagonal": 8.0 }
            }
            """);
            var loaded = new SettingsService(path).Load();

            Check.That(loaded.Cursor.OneEuroBetaPerDiagonal).IsEqualTo(8.0);
            Check.That(loaded.Cursor.OneEuroMinCutoff).IsEqualTo(5.0);   // rides the beta branch, so untouched
            Check.That(File.ReadAllText(path)).Not.Contains("\"oneEuroBeta\"");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AFreshProfile_IsNotTouchedByTheMigration()
    {
        var path = TempFile();
        try
        {
            var settings = new EyeCtrlSettings();
            settings.Cursor.OneEuroBetaPerDiagonal = 33.0;
            var service = new SettingsService(path);
            service.Save(settings);
            string beforeLoad = File.ReadAllText(path);

            var loaded = service.Load();

            Check.That(loaded.Cursor.OneEuroBetaPerDiagonal).IsEqualTo(33.0);
            Check.That(File.ReadAllText(path)).IsEqualTo(beforeLoad);   // not rewritten at all
        }
        finally { File.Delete(path); }
    }

    // ── read-time clamps for the deadband knobs ──────────────────────────────────────────────────

    [Theory]
    [InlineData(-3.0, 0.0)]
    [InlineData(99.0, 15.0)]
    [InlineData(4.5, 4.5)]
    public void DeadbandRadius_ClampsAtReadTime_InsteadOfResettingEverything(double written, double expected)
    {
        var path = TempFile();
        try
        {
            var settings = new EyeCtrlSettings();
            settings.Dwell.ActivationButtonDwellMs = 640;
            var service = new SettingsService(path);
            service.Save(settings);

            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            root["cursor"]!["deadbandRadiusPx"] = written;
            File.WriteAllText(path, root.ToJsonString());

            var loaded = service.Load();

            Check.That(loaded.Cursor.DeadbandRadiusPx).IsEqualTo(expected);
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(640);   // nothing else was lost
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(50, 5)]
    [InlineData(2, 2)]
    public void DeadbandConfirmSamples_ClampsAtReadTime(int written, int expected)
    {
        var path = TempFile();
        try
        {
            var service = new SettingsService(path);
            service.Save(new EyeCtrlSettings());

            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            root["cursor"]!["deadbandConfirmSamples"] = written;
            File.WriteAllText(path, root.ToJsonString());

            Check.That(service.Load().Cursor.DeadbandConfirmSamples).IsEqualTo(expected);
        }
        finally { File.Delete(path); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"eyectrl_filtermig_{Guid.NewGuid()}.json");

    /// <summary>A pre-Stage-3 settings file carrying the legacy pixel-unit <c>oneEuroBeta</c> key.</summary>
    private static string Cursor(double minCutoff, double legacyBeta, string? extra = null) =>
        $$"""
        {
          "cursor": {
            "filterMode": "oneEuro",
            "oneEuroMinCutoff": {{minCutoff.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
            "oneEuroBeta": {{legacyBeta.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
            "maxGapInterpolationMs": 150
          }{{(extra is null ? "" : ",\n  " + extra)}}
        }
        """;

    private static void Run(string legacyJson, Action<EyeCtrlSettings> assert)
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, legacyJson);
            assert(new SettingsService(path).Load());
        }
        finally { File.Delete(path); }
    }
}
