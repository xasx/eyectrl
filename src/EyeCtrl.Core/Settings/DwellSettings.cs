namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>Dwell timing settings for gaze-based click activation.</summary>
public partial class DwellSettings : ObservableObject, IValidatable
{
    /// <summary>Time in milliseconds to detect a fixation before dwell begins. Valid range: 100–1000.</summary>
    [ObservableProperty] private int _fixationDetectionMs = 300;
    /// <summary>Dwell time in milliseconds for activation buttons. Valid range: 200–2000.</summary>
    [ObservableProperty] private int _activationButtonDwellMs = 700;
    /// <summary>Dwell time in milliseconds for action menu buttons. Valid range: 200–2000.</summary>
    [ObservableProperty] private int _actionMenuDwellMs = 600;
    /// <summary>Dwell time in milliseconds for manual selection action menu buttons. Valid range: 200–2000.</summary>
    [ObservableProperty] private int _manualSelectionActionMenuDwellMs = 600;
    /// <summary>Dwell time in milliseconds for toolbar buttons. Valid range: 200–2000.</summary>
    [ObservableProperty] private int _toolbarButtonDwellMs = 700;
    /// <summary>Dwell time in milliseconds for keyboard keys. Valid range: 300–1500.</summary>
    [ObservableProperty] private int _keyboardKeyDwellMs = 450;
    /// <summary>Dwell time in milliseconds for prediction suggestion buttons. Valid range: 300–1500.</summary>
    [ObservableProperty] private int _keyboardPredictionDwellMs = 500;
    /// <summary>
    /// Radius in pixels within which gaze samples are considered a fixation (physical px — never
    /// DPI-converted). Valid range: 10–200. The default is 75 since ticket 3: the running-centroid rule
    /// passes G1 at both dispersion bars at 75 and misses D200 at 60, and the recording's settings.json
    /// held 90 — an unvalidated value carried over from a paused session.
    /// </summary>
    [ObservableProperty] private double _fixationRadiusPixels = 75;

    /// <summary>
    /// Whether <see cref="MigrateFixationRadiusOnce"/> has already run for this settings file. Absent from
    /// every pre-ticket-3 file (so it loads false) and set on the first migration; from then on the radius
    /// is user-owned (the Settings slider edits it) and the startup migration must not overwrite a value
    /// the user chose. Same pattern and reason as <c>ToolbarSettings.SizeMigrated</c>.
    /// </summary>
    [ObservableProperty] private bool _fixationRadiusMigrated;
    /// <summary>Velocity threshold in pixels per second below which gaze is considered fixating.</summary>
    [ObservableProperty] private double _velocityThresholdPxPerSec = 100.0;
    /// <summary>Cooldown period in milliseconds after a dwell activation before another can trigger. Valid range: 100–5000.</summary>
    [ObservableProperty] private int _dwellCooldownMs = 400;
    /// <summary>Whether a soft confirmation tone plays when a dwell completes on a non-typing
    /// surface (toolbar, menus, settings, etc.). Keys keep their own click sound. Default off
    /// (spec 2026-09-23, ledger D3).</summary>
    [ObservableProperty] private bool _cueEnabled = false;
    /// <summary>Dwell time in milliseconds for the drag release button. Valid range: 200–2000.</summary>
    [ObservableProperty] private int _dragReleaseButtonDwellMs = 600;
    /// <summary>Dwell time in milliseconds for the drag right-edge button. Valid range: 200–2000.</summary>
    [ObservableProperty] private int _dragRightEdgeButtonDwellMs = 600;
    /// <summary>Dwell time in milliseconds for the scroll stop button. Valid range: 200–2000.</summary>
    [ObservableProperty] private int _scrollStopButtonDwellMs = 600;
    /// <summary>Dwell time in milliseconds for manual selection side buttons (close, recapture). Valid range: 200–2000.</summary>
    [ObservableProperty] private int _manualSelectionSideButtonDwellMs = 600;
    /// <summary>Dwell time in milliseconds for the backdrop hide button. Valid range: 200–2000.</summary>
    [ObservableProperty] private int _backdropHideButtonDwellMs = 600;

    partial void OnFixationDetectionMsChanging(int value)
    {
        if (value < 100 || value > 1000)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(FixationDetectionMs)} must be between 100 and 1000");
    }

    partial void OnActivationButtonDwellMsChanging(int value)
    {
        if (value < 200 || value > 2000)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(ActivationButtonDwellMs)} must be between 200 and 2000");
    }

    partial void OnActionMenuDwellMsChanging(int value)
    {
        if (value < 200 || value > 2000)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(ActionMenuDwellMs)} must be between 200 and 2000");
    }

    partial void OnManualSelectionActionMenuDwellMsChanging(int value)
    {
        if (value < 200 || value > 2000)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(ManualSelectionActionMenuDwellMs)} must be between 200 and 2000");
    }

    partial void OnToolbarButtonDwellMsChanging(int value)
    {
        if (value < 200 || value > 2000)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(ToolbarButtonDwellMs)} must be between 200 and 2000");
    }

    partial void OnKeyboardKeyDwellMsChanging(int value)
    {
        if (value < 300 || value > 1500)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(KeyboardKeyDwellMs)} must be between 300 and 1500");
    }

    partial void OnKeyboardPredictionDwellMsChanging(int value)
    {
        if (value < 300 || value > 1500)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(KeyboardPredictionDwellMs)} must be between 300 and 1500");
    }

    partial void OnFixationRadiusPixelsChanging(double value)
    {
        // Deliberately stays 10–200. Narrowing it to the new default's neighbourhood would make
        // SettingsService.Load reject (and wipe) a file holding a legal legacy value like the user's 90.
        if (value < 10.0 || value > 200.0)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(FixationRadiusPixels)} must be between 10 and 200");
    }

    /// <summary>
    /// Sets <see cref="FixationRadiusPixels"/> to the ticket-3 default (75) exactly once per settings file,
    /// then never touches it again. Unlike <c>ToolbarSettings.MigrateSizeOnce</c>, ANY prior value is
    /// replaced on this first run: the 90 in the user's file was not a considered choice but an
    /// unvalidated value from a paused session (final synthesis, "Radius for step 1"), and the rule change
    /// makes 75 the value the gates were measured at. The persisted flag is what lets a LATER user edit
    /// stick across restarts. The startup caller must use this, not a raw assignment.
    /// </summary>
    public void MigrateFixationRadiusOnce()
    {
        if (FixationRadiusMigrated) return;
        FixationRadiusPixels = 75;
        FixationRadiusMigrated = true;
    }

    partial void OnVelocityThresholdPxPerSecChanging(double value)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(VelocityThresholdPxPerSec)} must be greater than 0");
    }

    partial void OnDwellCooldownMsChanging(int value)
    {
        if (value < 100 || value > 5000)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(DwellCooldownMs)} must be between 100 and 5000");
    }

    partial void OnDragReleaseButtonDwellMsChanging(int value)
    {
        if (value < 200 || value > 2000)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(DragReleaseButtonDwellMs)} must be between 200 and 2000");
    }

    partial void OnDragRightEdgeButtonDwellMsChanging(int value)
    {
        if (value < 200 || value > 2000)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(DragRightEdgeButtonDwellMs)} must be between 200 and 2000");
    }

    partial void OnScrollStopButtonDwellMsChanging(int value)
    {
        if (value < 200 || value > 2000)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(ScrollStopButtonDwellMs)} must be between 200 and 2000");
    }

    partial void OnManualSelectionSideButtonDwellMsChanging(int value)
    {
        if (value < 200 || value > 2000)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(ManualSelectionSideButtonDwellMs)} must be between 200 and 2000");
    }

    partial void OnBackdropHideButtonDwellMsChanging(int value)
    {
        if (value < 200 || value > 2000)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(BackdropHideButtonDwellMs)} must be between 200 and 2000");
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateRange(FixationDetectionMs, 100, 1000, nameof(FixationDetectionMs)),
            ValidationHelper.ValidateRange(ActivationButtonDwellMs, 200, 2000, nameof(ActivationButtonDwellMs)),
            ValidationHelper.ValidateRange(ActionMenuDwellMs, 200, 2000, nameof(ActionMenuDwellMs)),
            ValidationHelper.ValidateRange(ManualSelectionActionMenuDwellMs, 200, 2000, nameof(ManualSelectionActionMenuDwellMs)),
            ValidationHelper.ValidateRange(ToolbarButtonDwellMs, 200, 2000, nameof(ToolbarButtonDwellMs)),
            ValidationHelper.ValidateRange(KeyboardKeyDwellMs, 300, 1500, nameof(KeyboardKeyDwellMs)),
            ValidationHelper.ValidateRange(KeyboardPredictionDwellMs, 300, 1500, nameof(KeyboardPredictionDwellMs)),
            ValidationHelper.ValidateRange(FixationRadiusPixels, 10.0, 200.0, nameof(FixationRadiusPixels)),
            VelocityThresholdPxPerSec > 0
                ? null
                : $"{nameof(VelocityThresholdPxPerSec)} must be greater than 0, but was {VelocityThresholdPxPerSec}",
            ValidationHelper.ValidateRange(DwellCooldownMs, 100, 5000, nameof(DwellCooldownMs)),
            ValidationHelper.ValidateRange(DragReleaseButtonDwellMs, 200, 2000, nameof(DragReleaseButtonDwellMs)),
            ValidationHelper.ValidateRange(DragRightEdgeButtonDwellMs, 200, 2000, nameof(DragRightEdgeButtonDwellMs)),
            ValidationHelper.ValidateRange(ScrollStopButtonDwellMs, 200, 2000, nameof(ScrollStopButtonDwellMs)),
            ValidationHelper.ValidateRange(ManualSelectionSideButtonDwellMs, 200, 2000, nameof(ManualSelectionSideButtonDwellMs)),
            ValidationHelper.ValidateRange(BackdropHideButtonDwellMs, 200, 2000, nameof(BackdropHideButtonDwellMs))
        );
    }
}
