namespace EyeCtrl.Supervisor.UI;

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EyeCtrl.Controls;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
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
/// the supervisor may not persist it. Its toggle asks the App over the request channel
/// (<see cref="StartOnBootSetupRow"/>): the row shows the value on disk, or the parked choice
/// labelled as parked, and never writes the in-memory snapshot.
/// </para>
/// </remarks>
public partial class SupervisorSetupWindow : Window
{
    private readonly NoopDwellRegistrar _registrar = new();
    private readonly EyeCtrlSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly Action<EyeCtrlSettings> _saveChanges;
    private readonly Action _launchAppNow;
    private readonly StartOnBootSetupRow _startOnBoot;
    private readonly LanguageSetupRow _language;
    private readonly StringTable _strings;
    private readonly StackPanel _languageChoices = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _languageNote = new()
    {
        Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(0xF4, 0xD0, 0x3F)),
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 16),
    };
    private DwellButton[] _languageButtons = [];
    private DwellToggle? _startOnLogonToggle;
    private readonly TextBlock _startOnBootNote = new()
    {
        Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(0xF4, 0xD0, 0x3F)),
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, -16, 0, 16),
    };

    /// <summary>The enable value as on disk when this window opened, plus the save-time re-read:
    /// the running supervisor's startup snapshot can predate the App's Settings-row write.</summary>
    private readonly SupervisionSetupEnabledState _enabledState;

    /// <summary>
    /// Constructs the setup window.
    /// </summary>
    /// <param name="settings">The live settings snapshot to view/edit.</param>
    /// <param name="settingsService">A <see cref="SettingsService"/> constructed with <see cref="SettingsWriter.Supervisor"/> for persisting the Supervision section.</param>
    /// <param name="saveChanges">
    /// Invoked when the user saves, with the edited snapshot, so the host can apply the saved
    /// supervision policy to the running loop.
    /// </param>
    /// <param name="launchAppNow">Invoked by the "Launch EyeCtrl now" diagnostics button.</param>
    /// <param name="startOnBoot">The logon row's state over the Supervisor's StartOnBoot delivery;
    /// the window disposes it when it closes.</param>
    /// <param name="language">The language row's state over the Supervisor's language delivery
    /// (T05b); the window disposes it when it closes.</param>
    /// <param name="strings">The Supervisor's string table for this window.</param>
    /// <param name="statusText">Initial diagnostics status text (e.g. current supervisor state).</param>
    internal SupervisorSetupWindow(
        EyeCtrlSettings settings,
        SettingsService settingsService,
        Action<EyeCtrlSettings> saveChanges,
        Action launchAppNow,
        StartOnBootSetupRow startOnBoot,
        LanguageSetupRow language,
        StringTable strings,
        string statusText = "")
    {
        _settings = settings;
        _settingsService = settingsService;
        _saveChanges = saveChanges;
        _launchAppNow = launchAppNow;
        _startOnBoot = startOnBoot;
        _language = language;
        _strings = strings;

        // The window opens on the CURRENT on-disk value, not the supervisor's startup snapshot:
        // the App's Settings row may have turned supervision on since this process loaded. The
        // state object applies the value to the snapshot itself, so this call is the whole
        // wiring and cannot be half-done.
        _enabledState = new SupervisionSetupEnabledState(settingsService, settings);

        InitializeComponent();
        PopulateFallbackList();
        PopulateAdvancedFields();
        StatusLabel.Text = string.IsNullOrEmpty(statusText) ? "Supervisor idle." : statusText;
        BuildToggles();
        BuildDiagnostics();

        _startOnBoot.Changed += OnStartOnBootChanged;
        _language.Changed += OnLanguageChanged;
        Closed += (_, _) =>
        {
            _startOnBoot.Changed -= OnStartOnBootChanged;
            _startOnBoot.Dispose();
            _language.Changed -= OnLanguageChanged;
            _language.Dispose();
        };
    }

    private void PopulateFallbackList()
    {
        // Spec Non-Goal: full CRUD is out of scope. Show the configured default order read-only.
        FallbackList.ItemsSource = _settings.Supervision.FallbackAtOrder.ToList();
    }

    private void PopulateAdvancedFields()
    {
        // The supervisor surface's provider (T02): invariant while the surface is English, de-DE
        // once its German is effective. The same provider reads the boxes back in OnSave.
        var provider = EyeCtrl.Core.Localization.StringTable.Current.FormatProviderFor("supervisor");
        MaxRetriesBox.Text = SupervisorNumericField.Format(_settings.Supervision.MaxRetries, provider);
        HangThresholdBox.Text = SupervisorNumericField.Format(_settings.Supervision.HangThresholdSeconds, provider);
    }

    private void BuildToggles()
    {
        // The language row first (T05b): the label reads in both languages, the choices are
        // endonyms, so a caretaker finds it whatever the window is shown in.
        BuildLanguageRow();

        // Enable supervision — owned by the supervisor (Supervision section).
        var enableToggle = new DwellToggle(
            registrar: _registrar,
            id: "setup_enable_supervision",
            label: "Enable supervision",
            // Host-supplied state words (T02); T06 passes supervisor keys.
            onText: "On",
            offText: "Off",
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

        // Start on logon — App-owned (GeneralSettings), so the toggle asks the App to save it and
        // shows the value on disk or the parked choice; it never writes the snapshot.
        _startOnLogonToggle = new DwellToggle(
            registrar: _registrar,
            id: "setup_start_on_logon",
            label: "Start EyeCtrl at logon",
            onText: "On",
            offText: "Off",
            get: () => _startOnBoot.Value,
            set: _startOnBoot.Choose,
            category: DwellCategory.Control,
            radiusPhysical: 200,
            dwellMs: 600);

        enableToggle.Attach(ToggleHost);
        _startOnLogonToggle.Attach(ToggleHost);
        ToggleHost.Children.Add(_startOnBootNote);
        RefreshStartOnBootNote();
    }

    private void OnStartOnBootChanged()
    {
        // The value can change without a dwell (superseded, refused): repaint the switch, or its
        // face shows the dropped choice and the next dwell sends the inverse of the hidden value.
        _startOnLogonToggle?.Refresh();
        RefreshStartOnBootNote();
        if (_startOnBoot.StatusText(_strings) is { } status)
            StatusLabel.Text = status;
    }

    private void BuildLanguageRow()
    {
        ToggleHost.Children.Add(new TextBlock
        {
            Text = LanguageSetupRow.Label(_strings),
            Foreground = Media.Brushes.White,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        });
        ToggleHost.Children.Add(_languageChoices);
        ToggleHost.Children.Add(_languageNote);
        RefreshLanguageRow();
    }

    /// <summary>Rebuilds the three choice buttons (their captions mark the selection) and the
    /// note. User-rate, not gaze-rate: it runs on a choice and on a delivery outcome.</summary>
    private void RefreshLanguageRow()
    {
        foreach (var button in _languageButtons)
            button.Detach(_languageChoices);

        _languageButtons = new DwellButton[LanguageSetupRow.Choices.Length];
        for (var index = 0; index < LanguageSetupRow.Choices.Length; index++)
        {
            var choice = LanguageSetupRow.Choices[index];
            var selected = choice == _language.Selected;
            var button = new DwellButton(
                registrar: _registrar,
                id: "setup_language_" + UiLanguageTokens.ToJson(choice),
                label: _language.Caption(choice),
                action: () => _language.Choose(choice),
                // Selected: the window's Launch blue, already gated for the dwell bar's contrast
                // (DwellButtonProgressBarContrastTests); otherwise the theme's plain-button fill.
                baseColor: selected ? Media.Color.FromRgb(45, 90, 137) : EyeCtrl.Controls.Theme.Tokens.SurfaceMedium,
                category: DwellCategory.Control,
                radiusPhysical: 200,
                dwellMs: 600,
                width: 160,
                height: 70);
            button.Attach(_languageChoices);
            _languageButtons[index] = button;
        }

        var note = _language.Note(_strings);
        _languageNote.Text = note ?? "";
        _languageNote.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnLanguageChanged()
    {
        RefreshLanguageRow();
        if (_language.StatusText(_strings) is { } status)
            StatusLabel.Text = status;
    }

    /// <summary>The language row's choice buttons, in display order (headless tests).</summary>
    internal System.Collections.Generic.IReadOnlyList<DwellButton> LanguageButtons => _languageButtons;

    /// <summary>The language row's note (headless tests).</summary>
    internal TextBlock LanguageNote => _languageNote;

    private void RefreshStartOnBootNote()
    {
        var note = _startOnBoot.Note(_strings);
        _startOnBootNote.Text = note ?? "";
        _startOnBootNote.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
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
        // Pull advanced numeric fields into the snapshot before persisting. Same provider as
        // PopulateAdvancedFields, so what this window writes it can read back.
        var provider = EyeCtrl.Core.Localization.StringTable.Current.FormatProviderFor("supervisor");
        if (SupervisorNumericField.TryParse(MaxRetriesBox.Text, provider, out var maxRetries))
            _settings.Supervision.MaxRetries = maxRetries;
        if (SupervisorNumericField.TryParse(HangThresholdBox.Text, provider, out var hangThreshold))
            _settings.Supervision.HangThresholdSeconds = hangThreshold;

        // enabled is the one key the App may also write (its sanctioned exception). Re-read disk
        // at save and keep the on-disk value unless the user changed the toggle in THIS window,
        // so a save cannot silently write the stale startup value back.
        _settings.Supervision.Enabled = _enabledState.ResolveForSave(_settings.Supervision.Enabled);

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

        // Let the host refresh the running loop with the saved supervision policy.
        _saveChanges(_settings);
    }
}
