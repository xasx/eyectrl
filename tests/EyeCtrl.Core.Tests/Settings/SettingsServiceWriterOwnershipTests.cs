namespace EyeCtrl.Core.Tests.Settings;

using System.Text.Json.Nodes;
using EyeCtrl.Core.Settings;

/// <summary>
/// Verifies single-writer ownership enforcement in <see cref="SettingsService"/>.
/// The App process owns every section EXCEPT Supervision; the Supervisor process
/// owns ONLY Supervision. An App writer must never clobber a Supervision value the
/// supervisor persisted to disk (spec: "Lost updates, not just corruption").
/// </summary>
public class SettingsServiceWriterOwnershipTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");

    [Fact]
    public void AppWriter_DoesNotPersist_SupervisionSection()
    {
        var path = TempPath();
        try
        {
            // Supervisor wrote Enabled=true on disk.
            var supervisor = new SettingsService(path, SettingsWriter.Supervisor);
            var seed = new EyeCtrlSettings { Supervision = { Enabled = true } };
            supervisor.Save(seed);

            // App loads, then saves its own snapshot (Supervision.Enabled default false).
            var app = new SettingsService(path, SettingsWriter.App);
            var loaded = app.Load();
            Check.That(loaded.Supervision.Enabled).IsTrue(); // sees supervisor's value
            app.Save(loaded); // app must not clobber it

            var roundTripped = new SettingsService(path, SettingsWriter.App).Load();
            Check.That(roundTripped.Supervision.Enabled).IsTrue(); // preserved, not reset
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AppWriter_Preserves_OwnedSections()
    {
        // The App writer must still persist the sections it owns (everything but Supervision).
        var path = TempPath();
        try
        {
            var app = new SettingsService(path, SettingsWriter.App);
            var settings = new EyeCtrlSettings();
            settings.Dwell.ActivationButtonDwellMs = 900;
            app.Save(settings);

            var loaded = new SettingsService(path, SettingsWriter.App).Load();
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(900);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AppWriter_PreservesSupervision_EvenWhenOnDiskFileIsDamagedElsewhere()
    {
        // Regression: Save re-read the on-disk state via Load(), which (pre-#153) returned
        // ALL DEFAULTS when the file failed validation. So an App writer would silently reset
        // a supervisor-owned Supervision value it must preserve. Preservation must read the
        // RAW on-disk section, not a re-validated (defaulted) load.
        //
        // #153 review evolution: this test originally put the invalid value INSIDE
        // supervision (maxRetries=0) as a device to fail the whole-file validation. Since
        // per-setting tolerance, that shape is exactly the never-healing damage of review
        // finding F1 — the overlay now heals it (pinned in SettingsServiceLoadToleranceTests).
        // The ownership guarantee this test actually guards is re-expressed with the damage
        // OUTSIDE the owned section: a valid supervision section survives an App save no
        // matter what else in the file is broken.
        //
        // Round-2 finding 2: the futureKey makes the RAW-read invariant itself testable
        // again. Per-setting tolerance dissolved the original discrimination (a Load()-based
        // baseline now returns valid supervision values fine) — but a validating round-trip
        // DROPS unknown keys, while the raw overlay preserves them verbatim. Revert Save's
        // baseline to a Load() (mutation N1) and this assertion is what dies.
        var path = TempPath();
        try
        {
            // Raw on-disk file: the supervisor persisted Enabled=true plus a key this schema
            // doesn't know (stand-in for a future version's knob), but the file ALSO has a
            // damaged dwell section, so the file as a whole needs repair.
            File.WriteAllText(path,
                "{\"supervision\":{\"enabled\":true,\"futureKey\":\"keep-me\"},\"dwell\":{\"activationButtonDwellMs\":50}}");

            // App saves its own (valid) snapshot — Supervision.Enabled defaults to false.
            var app = new SettingsService(path, SettingsWriter.App);
            app.Save(new EyeCtrlSettings());

            // Read the persisted file RAW (a validating Load would re-default it) and assert
            // the supervisor's section survived untouched — values AND unknown keys: the app
            // must not clobber a section it doesn't own.
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            Check.That(node["supervision"]!["enabled"]!.GetValue<bool>()).IsTrue();
            Check.That(node["supervision"]!["futureKey"]!.GetValue<string>()).IsEqualTo("keep-me");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SupervisorWriter_DoesNotPersist_AppSections()
    {
        // Symmetric guarantee: the Supervisor writer must not clobber an App-owned section.
        var path = TempPath();
        try
        {
            // App wrote Dwell.ActivationButtonDwellMs=1234 on disk.
            var app = new SettingsService(path, SettingsWriter.App);
            var seed = new EyeCtrlSettings();
            seed.Dwell.ActivationButtonDwellMs = 1234;
            app.Save(seed);

            // Supervisor loads, flips its own section, and saves.
            var supervisor = new SettingsService(path, SettingsWriter.Supervisor);
            var loaded = supervisor.Load();
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(1234); // sees app's value
            loaded.Supervision.Enabled = true;
            supervisor.Save(loaded);

            var roundTripped = new SettingsService(path, SettingsWriter.Supervisor).Load();
            Check.That(roundTripped.Supervision.Enabled).IsTrue();          // supervisor's write preserved
            Check.That(roundTripped.Dwell.ActivationButtonDwellMs).IsEqualTo(1234); // app's write preserved
        }
        finally { File.Delete(path); }
    }

    // ── The sanctioned App exception (SaveSupervisionEnabled) ────────────────────────────────

    [Fact]
    public void AppException_SaveSupervisionEnabled_PersistsOnlyThatKey_AndPreservesEverythingElse()
    {
        // The App's one sanctioned way to flip supervision on: a Supervisor-writer instance,
        // editing one key on the raw on-disk JSON. Everything else must survive — the section's
        // other values, unknown keys inside the section (a future Supervisor knob), unknown root
        // keys, and every App-owned section. A whole-section replacement would drop the former.
        var path = TempPath();
        try
        {
            var app = new SettingsService(path, SettingsWriter.App);
            var seed = new EyeCtrlSettings();
            seed.Dwell.ActivationButtonDwellMs = 1234;
            app.Save(seed);   // an App-owned section value on disk

            var supervisor = new SettingsService(path, SettingsWriter.Supervisor);
            var sup = supervisor.Load();
            sup.Supervision.MaxRetries = 7;
            sup.Supervision.HangThresholdSeconds = 42;
            supervisor.Save(sup);   // the section's non-default siblings, App sections preserved

            var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            node["supervision"]!.AsObject()["futureKey"] = "keep-me";
            node["rootFuture"] = 42;
            File.WriteAllText(path, node.ToJsonString());

            var writer = new SettingsService(path, SettingsWriter.Supervisor);
            writer.SaveSupervisionEnabled(true);

            var raw = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Check.That(raw["supervision"]!["enabled"]!.GetValue<bool>()).IsTrue();
            Check.That(raw["supervision"]!["maxRetries"]!.GetValue<int>()).IsEqualTo(7);
            Check.That(raw["supervision"]!["hangThresholdSeconds"]!.GetValue<int>()).IsEqualTo(42);
            Check.That(raw["supervision"]!["futureKey"]!.GetValue<string>()).IsEqualTo("keep-me");
            Check.That(raw["dwell"]!["activationButtonDwellMs"]!.GetValue<int>()).IsEqualTo(1234);
            Check.That(raw["rootFuture"]!.GetValue<int>()).IsEqualTo(42);

            // And back off — the turn-off path goes through the same method.
            writer.SaveSupervisionEnabled(false);
            Check.That(JsonNode.Parse(File.ReadAllText(path))!["supervision"]!["enabled"]!.GetValue<bool>())
                .IsFalse();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SaveSupervisionEnabled_OnAnAppWriterInstance_Throws()
    {
        // The exception is one-directional by design: the App routes it through a Supervisor-writer
        // instance, so the call that would silently discard the write is never reachable.
        var path = TempPath();
        try
        {
            InvalidOperationException? ex = null;
            try { new SettingsService(path, SettingsWriter.App).SaveSupervisionEnabled(true); }
            catch (InvalidOperationException e) { ex = e; }

            Check.That(ex).IsNotNull();
            Check.That(ex!.Message).Contains("SettingsWriter.Supervisor");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SaveSupervisionEnabled_DamagedFile_ThrowsAndLeavesItUntouched()
    {
        // This write is defined as "one key changes, everything else stays". A file that cannot be
        // read as a JSON object has no safe edit, so the method must refuse rather than replace it
        // with something minimal — a human (or a later whole-file Save) still has the original.
        var path = TempPath();
        try
        {
            foreach (var damaged in new[] { "{ not json", "[1, 2, 3]", "" })
            {
                File.WriteAllText(path, damaged);

                InvalidOperationException? ex = null;
                try { new SettingsService(path, SettingsWriter.Supervisor).SaveSupervisionEnabled(true); }
                catch (InvalidOperationException e) { ex = e; }

                Check.WithCustomMessage($"content: {damaged}").That(ex).IsNotNull();
                Check.That(File.ReadAllText(path)).IsEqualTo(damaged);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SaveSupervisionEnabled_WithNoFileYet_WritesTheMinimalSection()
    {
        // First run: there is no settings file at all, and the flip must not need one.
        var path = TempPath();
        try
        {
            new SettingsService(path, SettingsWriter.Supervisor).SaveSupervisionEnabled(true);

            Check.That(new SettingsService(path, SettingsWriter.Supervisor).Load().Supervision.Enabled).IsTrue();
        }
        finally { File.Delete(path); }
    }
}
