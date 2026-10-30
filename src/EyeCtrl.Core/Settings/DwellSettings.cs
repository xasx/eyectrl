namespace EyeCtrl.Core.Settings;

using System.Text.Json.Serialization;
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
    /// <summary>
    /// Whether the cursor ring makes one brief weight step (bolder, never recoloured) when a dwell
    /// fires while the ring is visible (Wave C ticket C1): the visual sibling of
    /// <see cref="CueEnabled"/>, on the same announcement, so keys (which never announce) do not
    /// flash. An accepted activation offer flashes the magnifier's rim instead. Default ON. A new property, so an existing settings
    /// file picks the default from the initializer.
    /// </summary>
    [ObservableProperty] private bool _ringFiredFlashEnabled = true;
    /// <summary>
    /// Whether a short chime plays when the eye tracker has really disconnected (Wave B ticket B3):
    /// the rising edge of the published <c>Disconnected</c> flag, more than 10 s of continuous
    /// <c>IsConnected == false</c>. It sits beside <see cref="CueEnabled"/> because both are sound
    /// preferences and the row lives on the Dwell page; it is not a dwell setting. Default ON — the
    /// owner wants to hear a real tracker loss. A new property, so an existing settings file picks
    /// the default from the initializer.
    /// </summary>
    [ObservableProperty] private bool _statusChimeEnabled = true;
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
    /// <summary>
    /// How long, in milliseconds, a menu/toolbar dwell HOLDS its progress after the gaze leaves the
    /// target (or goes silent, as in a blink): returning within it resumes, and time away is never
    /// credited. 0 restores the exact reset-on-leave behaviour. Valid range: 0–1000. Defaults to the
    /// fixation detector's gap-abort bound, so a dwell forgives exactly what a fixation forgives.
    /// </summary>
    [ObservableProperty] private int _dwellPauseGraceMs = Processing.FixationDetector.GapAbortMs;

    /// <summary>
    /// Dwell time in milliseconds for the paused strip's Resume target (B2a). The dwell actually used
    /// is <c>max(ResumeDwellMs, ToolbarButtonDwellMs + 200)</c>, so Resume is never quicker than the
    /// toolbar: a glance that crosses it on the way to the edge must not end a pause. Valid range:
    /// 500–3000. A new property, so an existing file picks up the default from the initializer.
    /// </summary>
    [ObservableProperty] private int _resumeDwellMs = 1000;

    partial void OnResumeDwellMsChanging(int value)
    {
        if (value < 500 || value > 3000)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(ResumeDwellMs)} must be between 500 and 3000");
    }

    /// <summary>
    /// Dwell time in milliseconds for the restart notice's close button. 0 follows
    /// <see cref="ActivationButtonDwellMs"/> live; otherwise valid range: 200–3000. A NEW property
    /// (B6), so an existing settings file picks the default up from the initializer and the notice
    /// keeps matching the activation dwell until the user sets a value.
    /// </summary>
    [ObservableProperty] private int _restartNoticeDwellMs;

    /// <summary>
    /// Dwell time in milliseconds for the eye-care banner's rings. 0 follows
    /// <see cref="ActivationButtonDwellMs"/> live; otherwise valid range: 200–3000. A NEW property
    /// (B6), so an existing settings file picks the default up from the initializer and the banner
    /// keeps matching the activation dwell until the user sets a value.
    /// </summary>
    [ObservableProperty] private int _eyeCareBannerDwellMs;

    /// <summary>
    /// The restart notice's effective dwell: the knob when set, else the live activation dwell.
    /// Derived, not a setting — ignored by the serializer so it never lands in settings.json.
    /// </summary>
    [JsonIgnore]
    public int EffectiveRestartNoticeDwellMs =>
        RestartNoticeDwellMs > 0 ? RestartNoticeDwellMs : ActivationButtonDwellMs;

    /// <summary>
    /// The eye-care banner's effective dwell: the knob when set, else the live activation dwell.
    /// Derived, not a setting — ignored by the serializer so it never lands in settings.json.
    /// </summary>
    [JsonIgnore]
    public int EffectiveEyeCareBannerDwellMs =>
        EyeCareBannerDwellMs > 0 ? EyeCareBannerDwellMs : ActivationButtonDwellMs;

    partial void OnRestartNoticeDwellMsChanging(int value)
    {
        if (value != 0 && (value < 200 || value > 3000))
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(RestartNoticeDwellMs)} must be 0 or between 200 and 3000");
    }

    partial void OnEyeCareBannerDwellMsChanging(int value)
    {
        if (value != 0 && (value < 200 || value > 3000))
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(EyeCareBannerDwellMs)} must be 0 or between 200 and 3000");
    }

    partial void OnDwellPauseGraceMsChanging(int value)
    {
        if (value < 0 || value > 1000)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(DwellPauseGraceMs)} must be between 0 and 1000");
    }

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
            ValidationHelper.ValidateRange(BackdropHideButtonDwellMs, 200, 2000, nameof(BackdropHideButtonDwellMs)),
            ValidationHelper.ValidateRange(DwellPauseGraceMs, 0, 1000, nameof(DwellPauseGraceMs)),
            ValidationHelper.ValidateRange(ResumeDwellMs, 500, 3000, nameof(ResumeDwellMs)),
            RestartNoticeDwellMs == 0 || RestartNoticeDwellMs is >= 200 and <= 3000
                ? null
                : $"{nameof(RestartNoticeDwellMs)} must be 0 or between 200 and 3000, but was {RestartNoticeDwellMs}",
            EyeCareBannerDwellMs == 0 || EyeCareBannerDwellMs is >= 200 and <= 3000
                ? null
                : $"{nameof(EyeCareBannerDwellMs)} must be 0 or between 200 and 3000, but was {EyeCareBannerDwellMs}"
        );
    }
}
