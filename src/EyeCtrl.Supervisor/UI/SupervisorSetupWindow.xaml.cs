namespace EyeCtrl.Supervisor.UI;

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EyeCtrl.Controls;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Supervisor.Supervision;
using Media = System.Windows.Media;

/// <summary>
/// Minimal setup window for the supervisor: enable supervision, toggle logon start,
/// view the fallback-AT order, and tweak retry/hang thresholds. Plus a diagnostics
/// "launch now" action. Intentionally small — the supervisor is a watchdog, not the app.
/// </summary>
/// <remarks>
/// All interactive controls are <see cref="DwellToggle"/> / <see cref="DwellButton"/> so the
/// window is dwell/click operable (the user may reach it while the main app is unresponsive).
/// Writes go through a <see cref="SettingsService"/> constructed with
/// <see cref="SettingsWriter.Supervisor"/>, so only the <see cref="SupervisionSettings"/>
/// section is persisted (single-writer rule; <see cref="SettingsService.Save"/> re-reads
/// disk under a mutex and preserves every other section).
/// <para>
/// <c>StartOnBoot</c> lives in <see cref="GeneralSettings"/>, which the App process owns —
/// the supervisor may not persist it. Its toggle is therefore reflected into the in-memory
/// snapshot and handed to <paramref name="saveChanges"/>; Task 20 decides how (or whether)
/// to route that across writers (it is not the supervisor's section to write).
/// </para>
/// </remarks>
public partial class SupervisorSetupWindow : Window
{
    private readonly NoopDwellRegistrar _registrar = new();
    private readonly EyeCtrlSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly Action<EyeCtrlSettings> _saveChanges;
    private readonly Action _launchAppNow;

    /// <summary>The enable value as loaded when this window opened: the baseline a save compares
    /// against to tell "the user changed the toggle here" from "the App wrote it since".</summary>
    private readonly bool _enabledAtOpen;

    /// <summary>
    /// Constructs the setup window.
    /// </summary>
    /// <param name="settings">The live settings snapshot to view/edit.</param>
    /// <param name="settingsService">A <see cref="SettingsService"/> constructed with <see cref="SettingsWriter.Supervisor"/> for persisting the Supervision section.</param>
    /// <param name="saveChanges">
    /// Invoked when the user saves, with the edited snapshot. Task 20 uses this to react to
    /// changes the supervisor cannot own (e.g. StartOnBoot) and to refresh the running loop.
    /// </param>
    /// <param name="launchAppNow">Invoked by the "Launch EyeCtrl now" diagnostics button.</param>
    /// <param name="statusText">Initial diagnostics status text (e.g. current supervisor state).</param>
    public SupervisorSetupWindow(
        EyeCtrlSettings settings,
        SettingsService settingsService,
        Action<EyeCtrlSettings> saveChanges,
        Action launchAppNow,
        string statusText = "")
    {
        _settings = settings;
        _settingsService = settingsService;
        _saveChanges = saveChanges;
        _launchAppNow = launchAppNow;
        _enabledAtOpen = settings.Supervision.Enabled;

        InitializeComponent();
        PopulateFallbackList();
        PopulateAdvancedFields();
        StatusLabel.Text = string.IsNullOrEmpty(statusText) ? "Supervisor idle." : statusText;
        BuildToggles();
        BuildDiagnostics();
    }

    private void PopulateFallbackList()
    {
        // Spec Non-Goal: full CRUD is out of scope. Show the configured default order read-only.
        FallbackList.ItemsSource = _settings.Supervision.FallbackAtOrder.ToList();
    }

    private void PopulateAdvancedFields()
    {
        MaxRetriesBox.Text = _settings.Supervision.MaxRetries.ToString();
        HangThresholdBox.Text = _settings.Supervision.HangThresholdSeconds.ToString();
    }

    private void BuildToggles()
    {
        // Enable supervision — owned by the supervisor (Supervision section).
        var enableToggle = new DwellToggle(
            registrar: _registrar,
            id: "setup_enable_supervision",
            label: "Enable supervision",
            get: () => _settings.Supervision.Enabled,
            set: value =>
            {
                _settings.Supervision.Enabled = value;
                // The actual Scheduled-Task install/remove is a separate workstream (spec Non-Goal);
                // for now the toggle just flips the setting + notifies the host so Task 20 can react.
                StatusLabel.Text = value
                    ? "Supervision enabled. (Scheduled Task install is a separate step.)"
                    : "Supervision disabled.";
            },
            category: DwellCategory.Control,
            radiusPhysical: 200,
            dwellMs: 600);

        // Start on logon — reflected into GeneralSettings.StartOnBoot, but NOT persisted by the
        // supervisor (that section is App-owned). Handed to the save callback for Task 20 to route.
        var startOnLogonToggle = new DwellToggle(
            registrar: _registrar,
            id: "setup_start_on_logon",
            label: "Start EyeCtrl at logon",
            get: () => _settings.General.StartOnBoot,
            set: value => _settings.General.StartOnBoot = value,
            category: DwellCategory.Control,
            radiusPhysical: 200,
            dwellMs: 600);

        enableToggle.Attach(ToggleHost);
        startOnLogonToggle.Attach(ToggleHost);
    }

    private void BuildDiagnostics()
    {
        var launchNow = new DwellButton(
            registrar: _registrar,
            id: "setup_launch_now",
            label: "Launch EyeCtrl now",
            action: () =>
            {
                StatusLabel.Text = "Launch requested.";
                _launchAppNow();
            },
            baseColor: Media.Color.FromRgb(45, 90, 137),
            category: DwellCategory.Control,
            radiusPhysical: 200,
            dwellMs: 600,
            width: 240,
            height: 70);

        var save = new DwellButton(
            registrar: _registrar,
            id: "setup_save",
            label: "Save",
            action: OnSave,
            baseColor: Media.Color.FromRgb(30, 100, 30),
            category: DwellCategory.Control,
            radiusPhysical: 200,
            dwellMs: 600,
            width: 160,
            height: 70);

        launchNow.Attach(DiagnosticsHost);
        save.Attach(DiagnosticsHost);
    }

    private void OnSave()
    {
        // Pull advanced numeric fields into the snapshot before persisting.
        if (int.TryParse(MaxRetriesBox.Text, out var maxRetries))
            _settings.Supervision.MaxRetries = maxRetries;
        if (int.TryParse(HangThresholdBox.Text, out var hangThreshold))
            _settings.Supervision.HangThresholdSeconds = hangThreshold;

        // enabled is the one key the App may also write (its sanctioned exception). The App's
        // Settings row can have turned it on since this window opened; re-read disk and keep the
        // on-disk value unless the user changed the toggle in THIS window, so a save cannot
        // silently write the stale value back. See SupervisionSetupSave.
        _settings.Supervision.Enabled = SupervisionSetupSave.ResolveEnabled(
            current: _settings.Supervision.Enabled,
            atOpen: _enabledAtOpen,
            onDisk: _settingsService.Load().Supervision.Enabled);

        try
        {
            // SettingsWriter.Supervisor => Save persists ONLY the Supervision section
            // (re-reads disk under mutex, preserves all other sections).
            _settingsService.Save(_settings);
            StatusLabel.Text = "Saved (supervision settings).";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Save failed: {ex.Message}";
        }

        // Let Task 20 react (e.g. apply the StartOnBoot change and refresh the running loop).
        _saveChanges(_settings);
    }
}
