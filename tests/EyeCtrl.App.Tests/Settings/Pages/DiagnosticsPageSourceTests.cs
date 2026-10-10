namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Settings;

/// <summary>
/// The Diagnostics page, exercised headlessly.
/// <para>
/// This page is load-bearing in a way its size hides: it is the only route by which the user can arm
/// the raw-gaze recorder, and the whole measurement stage is gated on one operator session. A toggle
/// that renders but silently fails to reach <see cref="DiagnosticsSettings"/> or to call
/// <c>ApplyAndSave</c> produces a session that records nothing — and the failure looks like a broken
/// recorder, which is the most expensive possible place to start debugging.
/// </para>
/// <para>
/// The only coverage before this file was <c>SettingsTests</c>'s <c>Category=UI</c> tab enumeration,
/// which CI does not run and which asserts that a tab exists, not that anything on it works.
/// </para>
/// </summary>
public class DiagnosticsPageSourceTests
{
    private static IEnumerable<SettingsRowSpec> Rows(DiagnosticsPageSource source) =>
        source.Build().SelectMany(p => p.Rows);

    [Fact]
    public void Build_ExposesBothInstrumentToggles_AndTheButtons()
    {
        var rows = Rows(new DiagnosticsPageSource(TestSettingsPageContext.Build())).ToList();

        Check.That(rows.OfType<ToggleSpec>().Select(t => t.Id).ToList())
             .ContainsExactly("dg_record_raw", "dg_ui_instruments");
        Check.That(rows.OfType<ButtonSpec>().Select(b => b.Id).ToList())
             .ContainsExactly("dg_open_folder", "dg_subpixel_probe", "dg_reset");
    }

    [Fact]
    public void BothToggles_SayTheyTakeEffectAtTheNextStart()
    {
        // Not cosmetic. Both flags are read once at composition — the recorder decorates the gaze
        // provider at startup and the probes are constructed there — so a label that merely said
        // "Record Raw Gaze" would promise something the wiring does not do, and the operator would
        // run a whole session believing it was recording.
        var toggles = Rows(new DiagnosticsPageSource(TestSettingsPageContext.Build()))
            .OfType<ToggleSpec>().ToList();

        Check.That(toggles).HasSize(2);
        foreach (var toggle in toggles)
            Check.That(toggle.Label).Contains("next start");
    }

    [Fact]
    public void Toggles_WriteLiveSettings_AndPersist()
    {
        // The failure this page exists to prevent: the toggle flips in the UI, the setting never
        // changes, and the operator's session records nothing.
        var settings = new EyeCtrlSettings();
        var rec = new RecordingSettingsPageContext(settings);
        var toggles = Rows(new DiagnosticsPageSource(rec.Context))
            .OfType<ToggleSpec>().ToDictionary(t => t.Id);

        Check.That(settings.Diagnostics.RecordRawGaze).IsFalse();   // the shipped default
        Check.That(settings.Diagnostics.MeasureUiInstruments).IsFalse();

        toggles["dg_record_raw"].Set(true);
        toggles["dg_ui_instruments"].Set(true);

        Check.That(settings.Diagnostics.RecordRawGaze).IsTrue();
        Check.That(settings.Diagnostics.MeasureUiInstruments).IsTrue();

        // Written to disk, not just to memory: the flags are consumed on the NEXT start, so a change
        // that is never saved is a change that never happens.
        Check.That(rec.ApplyAndSaveCount).IsEqualTo(2);
    }

    [Fact]
    public void Toggles_ReadBackTheLiveValue()
    {
        // The Get delegates run on every rebuild (and twice per rebuild, via the pagination
        // measurer). A getter wired to the wrong field would show the page in a state the settings
        // are not in — which on this page means showing "off" while a recording is running.
        var settings = new EyeCtrlSettings();
        settings.Diagnostics.RecordRawGaze = true;
        settings.Diagnostics.MeasureUiInstruments = false;

        var toggles = Rows(new DiagnosticsPageSource(TestSettingsPageContext.Build(settings)))
            .OfType<ToggleSpec>().ToDictionary(t => t.Id);

        Check.That(toggles["dg_record_raw"].Get()).IsTrue();
        Check.That(toggles["dg_ui_instruments"].Get()).IsFalse();
    }

    [Fact]
    public void Reset_RestoresBothDefaults_OnlyAfterTheModalIsConfirmed()
    {
        var settings = new EyeCtrlSettings();
        settings.Diagnostics.RecordRawGaze = true;
        settings.Diagnostics.MeasureUiInstruments = true;

        var rec = new RecordingSettingsPageContext(settings);
        var reset = Rows(new DiagnosticsPageSource(rec.Context))
            .OfType<ButtonSpec>().Single(b => b.Id == "dg_reset");

        reset.OnActivate();

        // Nothing applied yet — the button shows a confirmation and waits.
        Check.That(rec.LastModalOnYes).IsNotNull();
        Check.That(settings.Diagnostics.RecordRawGaze).IsTrue();
        Check.That(rec.ApplyAndSaveCount).IsEqualTo(0);

        rec.LastModalOnYes!();   // the user confirms

        var defaults = new DiagnosticsSettings();
        Check.That(settings.Diagnostics.RecordRawGaze).IsEqualTo(defaults.RecordRawGaze);
        Check.That(settings.Diagnostics.MeasureUiInstruments).IsEqualTo(defaults.MeasureUiInstruments);
        Check.That(rec.ApplyAndSaveCount).IsEqualTo(1);
        Check.That(rec.RebuildContentCount).IsEqualTo(1);
    }

    [Fact]
    public void OpenRecordingsFolderButton_InvokesTheCallback()
    {
        // The operator has to reach gaze-recording.csv to analyze it, and asking a gaze-only user to
        // navigate to %APPDATA% by hand is asking for the whole measurement.
        bool opened = false;
        var source = new DiagnosticsPageSource(
            TestSettingsPageContext.Build(), onOpenRecordingFolder: () => opened = true);

        Rows(source).OfType<ButtonSpec>().Single(b => b.Id == "dg_open_folder").OnActivate();

        Check.That(opened).IsTrue();
    }

    [Fact]
    public void OpenRecordingsFolderButton_WithNoCallback_DoesNotThrow()
    {
        // The callback is optional so the source stays constructible from a test or a future host
        // that has no Explorer to open. A null-ref here would take the settings window down.
        var source = new DiagnosticsPageSource(TestSettingsPageContext.Build());

        Check.ThatCode(() => Rows(source).OfType<ButtonSpec>()
                 .Single(b => b.Id == "dg_open_folder").OnActivate())
             .DoesNotThrow();
    }

    [Fact]
    public void ThePrivacyNote_IsPresent_AndNamesWhatARecordingHolds()
    {
        // The person deciding whether to turn recording on is the person it records, so what a
        // recording contains belongs on screen rather than only in a design document.
        var labels = Rows(new DiagnosticsPageSource(TestSettingsPageContext.Build()))
            .OfType<LabelSpec>().Select(l => l.Text()).ToList();

        Check.That(labels).HasSize(1);
        Check.That(labels[0]).Contains("looked");
        Check.That(labels[0]).Contains("this computer");
    }

    // ---- W3 (a): a blocked precondition shows the reason and never requests the probe -----------

    [Fact]
    public void SubPixelButton_WhenBlocked_ShowsTheReason_AndDoesNotRequest()
    {
        var rec = new RecordingSettingsPageContext(new EyeCtrlSettings());
        bool requested = false;
        var source = new DiagnosticsPageSource(
            rec.Context,
            probeBlockReason: () => "Resume first, then start the test.",
            requestProbe: () => requested = true);

        Rows(source).OfType<ButtonSpec>().Single(b => b.Id == "dg_subpixel_probe").OnActivate();

        Check.WithCustomMessage(
                "A blocked start must show the one-line reason instead of arming the probe (review 05 F8).")
            .That(rec.Errors).ContainsExactly("Resume first, then start the test.");
        Check.That(requested).IsFalse();
    }

    [Fact]
    public void SubPixelButton_WhenAllowed_RequestsTheProbe()
    {
        var rec = new RecordingSettingsPageContext(new EyeCtrlSettings());
        bool requested = false;
        var source = new DiagnosticsPageSource(
            rec.Context,
            probeBlockReason: () => null,
            requestProbe: () => requested = true);

        Rows(source).OfType<ButtonSpec>().Single(b => b.Id == "dg_subpixel_probe").OnActivate();

        Check.That(requested).IsTrue();
        Check.That(rec.Errors).IsEmpty();
    }

    [Fact]
    public void SubPixelButton_WithNoCallbacks_DoesNotThrow()
    {
        var source = new DiagnosticsPageSource(TestSettingsPageContext.Build());

        Check.ThatCode(() => Rows(source).OfType<ButtonSpec>()
                 .Single(b => b.Id == "dg_subpixel_probe").OnActivate())
             .DoesNotThrow();
    }

    // ---- W3 (g): the label switches for a session-2 resume --------------------------------------

    [Fact]
    public void SubPixelButton_Label_SwitchesForASession2Resume()
    {
        var ctx = TestSettingsPageContext.Build();

        string LabelFor(bool canResume) => Rows(new DiagnosticsPageSource(
                ctx, canResumeSession2: () => canResume))
            .OfType<ButtonSpec>().Single(b => b.Id == "dg_subpixel_probe").Label;

        Check.That(LabelFor(false)).IsEqualTo(
            EyeCtrl.Core.Localization.StringTable.Current.T("settings.diagnostics.subpixel.start"));
        Check.WithCustomMessage(
                "The label predicate is inverted: a resume-capable verdict still shows the full-run label.")
            .That(LabelFor(true)).IsEqualTo(
                EyeCtrl.Core.Localization.StringTable.Current.T("settings.diagnostics.subpixel.continue"));
    }
}
