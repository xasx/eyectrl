namespace EyeCtrl.App.Tests.Settings.Pages;

using System.Linq;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Controls.Theme;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// The settings resets must not reopen the cross-theme ring/trail carry-over at runtime
/// (review round 2, N2). Both reset buttons write <see cref="SettingsDefaults"/> — Midnight
/// absolutes — and previously painted the live theme's halo with them until the next
/// restart; they now re-derive through <see cref="Services.ThemeStartup.MigrateHaloedMarks"/>.
///
/// Serialised into the ActiveTheme collection: these activate Daylight, which mutates the
/// process-global <see cref="Tokens.Current"/>; Midnight is restored in each finally.
/// </summary>
[Collection("ActiveTheme")]
public class ResetMarkMigrationTests
{
    [Fact]
    public void CursorReset_OnDaylight_RewritesTheRingAsTheThemeAccent()
    {
        var settings = new EyeCtrlSettings();
        try
        {
            Tokens.Activate(DaylightTheme.Instance);
            var rec = new RecordingSettingsPageContext(settings);
            var reset = new CursorPageSource(rec.Context).Build()
                .SelectMany(p => p.Rows).OfType<ButtonSpec>()
                .Single(b => b.Id == "c_reset");

            reset.OnActivate();
            Check.That(rec.LastModalOnYes).IsNotNull();
            rec.LastModalOnYes!();

            Check.That(settings.Cursor.CircleColor).IsEqualTo("#07343E");
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }
    }

    /// <summary>QaTestSupport's recipe, but around THIS test's settings instance, so
    /// ResetAllSettings mutates the object the assertions read (the support helper
    /// hardcodes its own).</summary>
    private static ViewModels.SettingsWindowViewModel MakeViewModel(EyeCtrlSettings settings)
    {
        var settingsVM = new ViewModels.SettingsViewModel(settings);
        var editor = new ViewModels.SettingsQuickActionsEditorViewModel(
            new QaTestSupport.FakeQuickActionsStore(), new QaTestSupport.FakeInstalledAppProvider());
        var vm = new ViewModels.SettingsWindowViewModel(
            settingsVM, editor, new WeakReferenceMessenger(), settings.Dwell);
        editor.Load();
        return vm;
    }

    [Fact]
    public void ResetAll_OnDaylight_RewritesBothMarks()
    {
        var settings = new EyeCtrlSettings();
        try
        {
            Tokens.Activate(DaylightTheme.Instance);
            var rec = new RecordingSettingsPageContext(settings, viewModel: MakeViewModel(settings));
            var reset = new GeneralPageSource(rec.Context).Build()
                .SelectMany(p => p.Rows).OfType<ButtonSpec>()
                .Single(b => b.Id == "g_resetall");

            // Round 3, R2: ResetAllSettings copies every default by reflection — the
            // third path that wrote Midnight's absolutes under a live theme.
            reset.OnActivate();
            Check.That(rec.LastModalOnYes).IsNotNull();
            rec.LastModalOnYes!();

            Check.That(settings.Cursor.CircleColor).IsEqualTo("#07343E");
            Check.That(settings.Drag.TrailColor).IsEqualTo("#7A1000");
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }
    }

    [Fact]
    public void CursorReset_MigratesTheMark_BeforeTheSaveThatRepaintsTheRing()
    {
        // Round 4, m3: the behavioural half the source-order pin cannot see. The
        // hook captures the ring colour AT ApplyAndSave time — the value the live
        // SettingsChanged repaint would paint. If the migration ran after the save,
        // this sees Midnight's #A5F3FC even though the final settings are correct.
        var settings = new EyeCtrlSettings();
        try
        {
            Tokens.Activate(DaylightTheme.Instance);
            string? colourAtApply = null;
            var rec = new RecordingSettingsPageContext(
                settings, onApplyAndSave: () => colourAtApply = settings.Cursor.CircleColor);
            var reset = new CursorPageSource(rec.Context).Build()
                .SelectMany(p => p.Rows).OfType<ButtonSpec>()
                .Single(b => b.Id == "c_reset");

            reset.OnActivate();
            rec.LastModalOnYes!();

            Check.That(colourAtApply).IsEqualTo("#07343E");
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }
    }

    [Fact]
    public void ResetHandlers_MigrateMarksBeforeApplyAndSave()
    {
        // m3c, pinned at the source level because the behavioural seam cannot observe
        // it: the recording context's ApplyAndSave only counts, and the settings object
        // is correct again by the time OnYes returns — the ONLY divergence is that
        // SettingsChanged (which repaints the ring live) would see the unmigrated
        // Midnight absolute if the migration ran after ApplyAndSave. Same technique as
        // HaloZOrderTests: order of two calls in the handler is the contract.
        var root = new System.IO.DirectoryInfo(System.IO.Path.GetDirectoryName(
            typeof(ResetMarkMigrationTests).Assembly.Location)!);
        while (root is not null && !System.IO.File.Exists(System.IO.Path.Combine(root.FullName, "EyeCtrl.slnx")))
            root = root.Parent!;
        Check.WithCustomMessage("could not locate repo root").That(root).IsNotNull();

        foreach (var relative in new[]
                 {
                     "src/EyeCtrl.App/Views/Settings/Sources/CursorPageSource.cs",
                     "src/EyeCtrl.App/Views/Settings/Sources/AdvancedPageSource.cs",
                     "src/EyeCtrl.App/Views/Settings/Sources/GeneralPageSource.cs",
                 })
        {
            var source = System.IO.File.ReadAllText(System.IO.Path.Combine(root!.FullName, relative));
            // Per OCCURRENCE, not whole-file: GeneralPageSource has several ApplyAndSave
            // calls (the theme row's comes first in the file), so the contract is that
            // each migration call is followed, within the same handler, by its save.
            var migrateAt = source.IndexOf("MigrateHaloedMarks", System.StringComparison.Ordinal);
            Check.WithCustomMessage($"{relative}: MigrateHaloedMarks not found")
                .That(migrateAt).IsStrictlyGreaterThan(-1);
            while (migrateAt >= 0)
            {
                var applyAt = source.IndexOf("_ctx.ApplyAndSave()", migrateAt, System.StringComparison.Ordinal);
                Check.WithCustomMessage(
                        $"{relative}: no ApplyAndSave follows the migration at offset {migrateAt}")
                    .That(applyAt).IsStrictlyGreaterThan(-1);
                Check.WithCustomMessage(
                        $"{relative}: the migration at offset {migrateAt} must sit immediately " +
                        "before its handler's ApplyAndSave (within the same handler body)")
                    .That(applyAt - migrateAt).IsStrictlyLessThan(300);
                migrateAt = source.IndexOf("MigrateHaloedMarks", applyAt, System.StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void AdvancedReset_OnDaylight_RewritesTheTrailAsTheThemeWarning()
    {
        var settings = new EyeCtrlSettings();
        try
        {
            Tokens.Activate(DaylightTheme.Instance);
            var rec = new RecordingSettingsPageContext(settings);
            var reset = new AdvancedPageSource(rec.Context).Build()
                .SelectMany(p => p.Rows).OfType<ButtonSpec>()
                .Single(b => b.Id == "av_reset");

            reset.OnActivate();
            Check.That(rec.LastModalOnYes).IsNotNull();
            rec.LastModalOnYes!();

            Check.That(settings.Drag.TrailColor).IsEqualTo("#7A1000");
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }
    }
}
