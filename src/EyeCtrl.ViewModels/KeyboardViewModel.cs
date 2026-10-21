namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

public partial class KeyboardViewModel : MessengerViewModelBase,
    IRecipient<ShowKeyboardMessage>,
    IRecipient<HideKeyboardMessage>
{
    private readonly EyeCtrlSettings _settings;

    // Gaze tracking state
    private long _nearestKeyEnteredTimestamp;
    private long _keyCooldownEndTimestamp;
    private long _lastSampleTimestampUs;
    private int _keyDwellMs;
    private int _baselineKeyDwellMs;
    private int _cooldownMs;

    // D13 spatial swap suppression: while the gaze stays within ~half a key width of its
    // pre-swap position, no key dwells — a sub-layout swap (manual or auto-return) happened
    // under a resting gaze and whatever materialized there must not fire. The radius is
    // computed at swap time from the OLD grid's still-valid centres; post-swap centres are
    // unarranged at the first samples and are never consulted (plan review B1).
    private Dictionary<string, PhysicalPoint>? _lastKeyScreenPositions;
    private PhysicalPoint? _lastGazeInsideWindowPoint;
    private PhysicalPoint? _swapSuppressedAnchor;
    private double _swapSuppressedClearRadiusSquared;

    /// <summary>
    /// Longest interval between two DELIVERED gaze samples that may still count as continuous
    /// dwell. The tracker measures elapsed time from timestamps, so a pause longer than this must
    /// restart the dwell rather than be credited — otherwise the first sample after a blink, a
    /// tracker dropout or a hidden interval receives the whole pause as held time and fires the
    /// key with no progress ever shown (F3). Withholding gap-held samples upstream cannot bound
    /// this: a pause is a timestamp fact, not a delivery fact.
    ///
    /// <para>
    /// 150 ms is 4.5x the 33 ms interval of the slowest supported tracker rate (30 Hz), so live
    /// scheduler jitter cannot trip it, while every real interruption exceeds it (a blink is
    /// typically 100–400 ms; the measured incident was 3 s). Deliberately NOT
    /// <c>CursorSettings.MaxGapInterpolationMs</c>: that is a user-tunable knob whose range
    /// (0–500) reaches past the keyboard dwell, so it cannot be the keyboard's safety bound.
    /// </para>
    ///
    /// <para>
    /// 300 ms is the keyboard family's floor (<c>AdaptationDwellRanges.KeyboardMinDwellMs</c>), so
    /// this bound is now below the minimum dwell a key can have: a SINGLE credited silence (at most
    /// this bound — exactly 150 ms is still credited here, unlike the shared detector's
    /// <c>&gt;=</c>) supplies at most half of a completed key, and the user must contribute at
    /// least as much real gaze as that silence could fake. The clamp is per gap, so N silences
    /// still contribute up to 150·N; the multi-stall qualifier and why it is accepted are recorded
    /// on <c>DwellDetector.MaxCreditedSampleGapUs</c>. The floor is enforced on every path — the
    /// <c>DwellSettings</c> guards and the Dwell sliders stop a lower baseline being written or
    /// configured, and <see cref="ApplyEffectiveKeyDwell"/> re-clamps the ADAPTIVE value, which the
    /// forgiving knob would otherwise shorten to 0.6×baseline (180 ms at a 300 ms baseline). The
    /// residual this paragraph used to record — a 100–150 ms stall crediting at a 100 ms minimum —
    /// is closed by that floor; do not re-derive the old 100 ms minimum from this constant.
    /// </para>
    /// </summary>
    private const long MaxContinuousSampleGapUs = 150 * MathConstants.MicrosecondsPerMillisecond;

    private readonly IStruggleTelemetrySink? _sink;
    private readonly EffectiveSettingsProvider? _effectiveSettings;
    private readonly IKeyboardContextProbe? _contextProbe;
    private readonly ISensitiveContextDetector? _sensitiveContext;
    private readonly Action _onEffectiveSettingsChanged;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _isEmojiMode;

    private readonly HashSet<ModifierKey> _activeModifiers = new();

    public IReadOnlySet<ModifierKey> ActiveModifiers => new HashSet<ModifierKey>(_activeModifiers);

    public bool IsShifted => _activeModifiers.Contains(ModifierKey.Shift)
                          || _activeModifiers.Contains(ModifierKey.CapsLock);
    [ObservableProperty] private string? _nearestKeyId;
    [ObservableProperty] private double _nearestKeyProgress;
    [ObservableProperty] private string _currentInput = "";
    [ObservableProperty] private string _activeSubLayoutId = "letters";

    private PredictionOrchestrator? _orchestrator;
    private KeyboardLayout? _layout;
    private string? _previousWord;
    private string _recentText = ""; // Track recent text for smart capitalization

    public event Action<string>? KeyDwellCompleted;

    // E1 (content-free per-key logging). Everything below is bookkeeping for the per-EVENT lines
    // KeyboardE1Recorder writes: running sums while one key is the dwell candidate, a restart
    // count, the saccade into the key, and the post-fire hold-on watch. Nothing here logs per
    // gaze sample and nothing here changes what the keyboard does.
    private PhysicalPoint _e1LastFirePoint;
    private bool _e1HasLastFire;
    private bool _e1PreviousFireWasText;
    private PhysicalPoint _e1DwellStartPoint;
    private int _e1LayoutToken;
    private int _e1LayoutLoggedToken = -1;
    private double _e1SumDxPx;
    private double _e1SumDyPx;
    private int _e1Samples;
    private int _e1Restarts;
    private bool _e1Resetting;
    private bool _inAutoReturn;
    private KeyboardMirrorChecker _mirrorChecker;

    // E1 slip tracking: a letter/digit, then an in-word Backspace, then the replacement. Only the
    // slipped key's centre, box and row band are kept, and only until the replacement arrives.
    private readonly record struct SlipCandidate(
        PhysicalPoint Centre, PhysicalRect? Box, string Band, long AtMs, CorrE1 Corr);
    private SlipCandidate? _slipCandidate;
    private bool _slipArmed;
    private long _slipBackspaceAtMs;

    private bool _holdOnActive;
    private long _holdOnSeq;
    private string _holdOnKeyId = "";
    private long _holdOnFireTimestampUs;
    private long _holdOnLastInsideUs;

    /// <summary>The longest hold-on E1 reports; read across and past the cooldown.</summary>
    private const int HoldOnCapMs = 600;

    /// <summary>The E1 recorder: the keyboard's content-free event log.</summary>
    public KeyboardE1Recorder E1 { get; private set; }

    /// <summary>
    /// Supplied by the window: dock edge, physical rect and time since the last window move, for
    /// the key lines. Null leaves those fields at their "unknown" values.
    /// </summary>
    public Func<KeyboardE1Geometry>? E1Geometry { get; set; }

    /// <summary>
    /// Supplied by the window: the standard letter box in physical pixels. It is logged ONCE per
    /// shown/layout line instead of on every key (at fractional display scales the box size differs
    /// by a pixel between odd and even columns, so a per-key size would be a column fingerprint).
    /// </summary>
    public Func<(int Width, int Height)>? E1LetterBox { get; set; }

    /// <summary>The standard letter box, or (0, 0) when unknown; never throws.</summary>
    public (int Width, int Height) LetterBoxPx()
    {
        try { return E1LetterBox?.Invoke() ?? (0, 0); }
        catch (Exception) { return (0, 0); }
    }

    /// <summary>The sequence number of the most recent logged key line (0 before the first, or while muted).</summary>
    public long LastKeySeq { get; private set; }

    public KeyboardViewModel(EyeCtrlSettings settings, IMessenger messenger,
        IStruggleTelemetrySink? sink = null, EffectiveSettingsProvider? effectiveSettings = null,
        ILogger<KeyboardViewModel>? logger = null, IKeyboardContextProbe? contextProbe = null,
        ISensitiveContextDetector? sensitiveContext = null)
        : base(messenger)
    {
        _settings = settings;
        _sink = sink;
        _effectiveSettings = effectiveSettings;
        _contextProbe = contextProbe;
        _sensitiveContext = sensitiveContext;
        E1 = new KeyboardE1Recorder(logger) { IsSensitive = SensitiveNow };
        _mirrorChecker = new KeyboardMirrorChecker(E1, contextProbe, sensitiveContext);
        if (_effectiveSettings is not null)
        {
            _onEffectiveSettingsChanged = ApplyEffectiveKeyDwell;
            _effectiveSettings.Changed += _onEffectiveSettingsChanged;
        }
        else
        {
            _onEffectiveSettingsChanged = () => { };
        }
    }

    /// <summary>Replaces the E1 recorder (tests inject a clock; the mirror checker follows it).</summary>
    internal void UseE1Recorder(KeyboardE1Recorder recorder)
    {
        E1 = recorder;
        E1.IsSensitive ??= SensitiveNow;
        _mirrorChecker = new KeyboardMirrorChecker(recorder, _contextProbe, _sensitiveContext);
    }

    // Whether a password-like field has focus: E1 then logs nothing per key (see KeyboardE1Recorder).
    private bool SensitiveNow() => _sensitiveContext?.IsSensitiveActive == true;

    /// <summary>
    /// The foreground PROCESS name (never a title) for the "keyboard shown" line, or <c>unknown</c>.
    /// </summary>
    public string ForegroundProcessName()
    {
        try { return _contextProbe?.GetForegroundProcessName() ?? "unknown"; }
        catch (Exception) { return "unknown"; }
    }

    /// <summary>
    /// Starts E1's mirror check (<c>hide</c> or <c>enter</c>) and returns at once: the keyboard's
    /// record is snapshotted here on the calling thread, the UI Automation read and the comparison
    /// run off-thread under a hard time box, and only a verdict and lengths are logged. The
    /// caller must invoke it BEFORE the Enter keystroke is sent or the keyboard is hidden.
    /// </summary>
    public void BeginMirrorCheck(string at)
    {
        if (!E1.Enabled) return;
        _ = _mirrorChecker.Begin(at, _recentText + CurrentInput);
    }

    public void Configure(int keyDwellMs, int cooldownMs)
    {
        _baselineKeyDwellMs = keyDwellMs;
        _cooldownMs = cooldownMs;
        ApplyEffectiveKeyDwell();
    }

    // The key tracker is not a DwellDetector, so it composes its own effective dwell (design §15:
    // KeyboardViewModel effective key dwell). The _nearestKeyEnteredTimestamp pattern means a mid-dwell
    // change re-evaluates on the next sample without any reset — the same semantics the detector's
    // SetEffectiveDwellTime pins. NB: no early-return on a null provider — Configure MUST assign
    // _keyDwellMs even without one, or ProcessKeyGaze's `<= 0` guard silently kills the keyboard
    // (review round 1, blocker 2). The clamp also covers the no-provider path; it is identity there
    // because KeyboardKeyDwellMs's own guard is exactly [300, 1500].
    private void ApplyEffectiveKeyDwell()
    {
        if (_baselineKeyDwellMs <= 0)
            return; // not configured yet — Configure drives the initial apply
        double effective = _effectiveSettings?.Effective(
            AdaptationKnob.DwellTime, DwellContext.Other, _baselineKeyDwellMs) ?? _baselineKeyDwellMs;
        _keyDwellMs = (int)Math.Clamp(Math.Round(effective),
            AdaptationDwellRanges.KeyboardMinDwellMs, AdaptationDwellRanges.KeyboardMaxDwellMs);
    }

    // The dwell the LATCHED key must be held for. App-escaping keys (enter, tab, Escape, Delete,
    // Insert, shortcut:*, F1–F12) are exempt from the DwellTime knob's forgiving direction. That
    // direction is SHORTER and is driven automatically by the user's own abandonment, so without the
    // exemption it shortens the most consequential keys first, for whoever is struggling most.
    //
    // The floor is max(configured baseline, AppEscapingPolicyFloorMs), and the policy value is 600:
    // an absolute floor that also survives a lowered baseline (see its doc for the decision). The
    // max() keeps a user's larger setting intact, and a tightening fill still raises these keys above
    // the floor — Math.Max is monotone in the composed value.
    private int KeyDwellMsFor(string? keyId)
    {
        if (!KeyboardSpecialActions.IsAppEscaping(keyId))
            return _keyDwellMs;
        int floor = Math.Max(_baselineKeyDwellMs, KeyboardSpecialActions.AppEscapingPolicyFloorMs);
        return Math.Max(_keyDwellMs, floor);
    }

    // E1: emoji enter/exit as mode changes (a hide's own reset is excluded by _e1Resetting).
    partial void OnActiveSubLayoutIdChanged(string value) => _e1LayoutToken++;

    partial void OnIsEmojiModeChanged(bool value)
    {
        _e1LayoutToken++;
        if (_e1Resetting) return;
        E1.RecordMode("emoji", value ? "text" : "emoji", value ? "emoji" : "text");
    }

    public void Receive(ShowKeyboardMessage message) => IsVisible = true;
    public void Receive(HideKeyboardMessage message) => IsVisible = false;

    // Hiding ends the keyboard's interaction context. Every other dwell-owning surface resets its
    // detector state on that transition; the keyboard was the only one that did not, so a stale
    // tracker credited the whole hidden interval on the first sample after reopen (F3's reopen
    // trigger) and a non-default sub-layout/emoji mode reopened as a trap. Reset on the property
    // rather than inside Receive(HideKeyboardMessage): a direct IsVisible write takes the same
    // path, and the state cannot disagree with the flag.
    partial void OnIsVisibleChanged(bool value)
    {
        if (!value)
            ResetInteractionState();
    }

    /// <summary>
    /// Clears the state that describes the CURRENT interaction. Called when the keyboard hides:
    /// accumulated dwell must not survive into the next open (the hidden interval is not held
    /// gaze) and neither must <see cref="NearestKeyId"/>, which would otherwise latch a key from
    /// a layout the next sample's positions may not even contain.
    ///
    /// <para>
    /// Mode state resets too. <see cref="ActiveSubLayoutId"/> and <see cref="IsEmojiMode"/> used
    /// to survive hide/show, so a user who hid the keyboard while inside the numbers, symbols or
    /// function layout — or emoji mode — reopened into the same layout and had to find their way
    /// back with the keys in front of them. Reopening should be the recovery a confused
    /// gaze-only user reaches for first, not a second trap. The trade is one extra dwell to get
    /// back to a non-default layout, against a session-long trap.
    /// </para>
    /// </summary>
    private void ResetInteractionState()
    {
        NearestKeyId = null;
        NearestKeyProgress = 0;
        _nearestKeyEnteredTimestamp = 0;
        _lastSampleTimestampUs = 0;
        _swapSuppressedAnchor = null;
        _lastGazeInsideWindowPoint = null;
        FinishHoldOn(capped: false);
        _slipCandidate = null;
        _slipArmed = false;
        _e1HasLastFire = false;
        _e1PreviousFireWasText = false;
        _e1LayoutToken++;
        _e1Restarts = 0;
        _e1Samples = 0;

        // A hide's own mode resets are not the user's mode changes: they must not read as an
        // emoji exit (or an undo of the entry) in the E1 log.
        _e1Resetting = true;
        try { IsEmojiMode = false; }
        finally { _e1Resetting = false; }
        ActiveSubLayoutId = _layout?.DefaultSubLayout ?? "letters";

        // M7 (impl review M2; symptom corrected r2 N4): raised AFTER the assignment above —
        // the window rebuilds the keys synchronously on it, and raising earlier (e.g. an
        // OnActiveSubLayoutIdChanged hook, which fires before PropertyChanged) would repaint
        // the OLD grid. Without this raise, a hide from a non-letters layout with Caps armed
        // reopens with the Caps key unlit and the SHIFT-BEARING labels stale (QWERTZ ß,
        // AZERTY é è ç à) while the keys type capitals — plain letter labels are already
        // capitals and never go stale.
        OnPropertyChanged(nameof(ActiveModifiers));
    }

    /// <summary>
    /// Key dwell tracking: picks the targeted key and tracks dwell time directly. The pick is
    /// <see cref="KeyTargeting.PickKey"/> — the key whose BOX contains the gaze, else the nearest
    /// centre — so a gaze inside a key selects that key, and the gaps between keys still belong to
    /// one (no dead zones). <see cref="NearestKeyId"/> keeps its name: in the gaps it is still the
    /// nearest key.
    /// </summary>
    /// <param name="keyScreenPositions">Key centres in physical pixels. Telemetry reports these.</param>
    /// <param name="keyScreenBoxes">Key boxes in physical pixels, from the same transform as the
    /// centres. Null is plain nearest-centre; production always passes them.</param>
    public void ProcessKeyGaze(Dictionary<string, PhysicalPoint> keyScreenPositions,
        PhysicalPoint gazePoint, bool isGazeInsideWindow, long timestampUs,
        IReadOnlyDictionary<string, PhysicalRect>? keyScreenBoxes = null)
    {
        if (keyScreenPositions.Count == 0 || _keyDwellMs <= 0) return;

        _lastKeyScreenPositions = keyScreenPositions;
        if (isGazeInsideWindow)
            _lastGazeInsideWindowPoint = gazePoint;

        // How long the stream was silent since the last DELIVERED sample, measured before this
        // sample replaces the mark. See MaxContinuousSampleGapUs.
        long sinceLastSampleUs = timestampUs - _lastSampleTimestampUs;
        _lastSampleTimestampUs = timestampUs;

        // E1: the post-fire hold-on watch (one rect test while it is open; nothing is logged per sample).
        if (_holdOnActive)
            UpdateHoldOn(keyScreenPositions, keyScreenBoxes, gazePoint, isGazeInsideWindow,
                timestampUs, sinceLastSampleUs);

        if (!isGazeInsideWindow)
        {
            if (NearestKeyId != null)
            {
                _e1Restarts++;
                EmitAbandonIfNeeded(keyScreenPositions, keyScreenBoxes, gazePoint, timestampUs, AbandonReason.GazeLeft);
                NearestKeyProgress = 0;
                NearestKeyId = null;
            }
            _lastGazeInsideWindowPoint = null;
            return;
        }

        // Post-activation cooldown (deliberately no emission — not a struggle signal).
        if (timestampUs < _keyCooldownEndTimestamp)
        {
            if (NearestKeyId != null)
            {
                NearestKeyProgress = 0;
                NearestKeyId = null;
            }
            return;
        }

        if (_swapSuppressedAnchor is { } suppressionAnchor)
        {
            if (gazePoint.DistanceSquaredTo(suppressionAnchor) > _swapSuppressedClearRadiusSquared)
            {
                _swapSuppressedAnchor = null;   // gaze moved meaningfully away — normal dwell resumes
            }
            else
            {
                // D13: still resting on the landing spot — nothing dwells.
                if (NearestKeyId != null)
                {
                    NearestKeyProgress = 0;
                    NearestKeyId = null;
                }
                return;
            }
        }

        // Box-first, then nearest centre. Only WHICH key changes; everything below is the same.
        string? nearestId = KeyTargeting.PickKey(gazePoint, keyScreenPositions, keyScreenBoxes);

        if (nearestId != NearestKeyId)
        {
            if (NearestKeyId != null)
                _e1Restarts++;
            EmitAbandonIfNeeded(keyScreenPositions, keyScreenBoxes, gazePoint, timestampUs, AbandonReason.NearestChanged);
            NearestKeyProgress = 0;
            NearestKeyId = nearestId;
            _nearestKeyEnteredTimestamp = timestampUs;
            // Where this candidate's dwell BEGAN: the saccade is measured from the previous fire point to
            // here, so it does not depend on how soon after the fire the dwell was allowed to start.
            _e1DwellStartPoint = gazePoint;
            if (nearestId != null)
                StartDwellOffsets(keyScreenPositions, gazePoint, nearestId);
        }
        else if (nearestId != null)
        {
            if (sinceLastSampleUs > MaxContinuousSampleGapUs)
            {
                // The stream paused (blink, tracker dropout, hidden interval). Crediting the pause
                // here is the F3 defect — a timestamp-based clock counts the silence as held gaze.
                // Restart instead, so only continuous gaze completes the dwell.
                NearestKeyProgress = 0;
                _nearestKeyEnteredTimestamp = timestampUs;
                _e1Restarts++;
                StartDwellOffsets(keyScreenPositions, gazePoint, nearestId);
            }
            else
            {
                AddDwellOffset(keyScreenPositions, gazePoint, nearestId);
                long elapsed = timestampUs - _nearestKeyEnteredTimestamp;
                int dwellMs = KeyDwellMsFor(nearestId);
                NearestKeyProgress = Math.Min(1.0, (double)elapsed / (dwellMs * MathConstants.MicrosecondsPerMillisecond));

                if (elapsed >= dwellMs * MathConstants.MicrosecondsPerMillisecond)
                {
                    EmitActivation(keyScreenPositions, gazePoint, nearestId, timestampUs, keyScreenBoxes);
                    NearestKeyProgress = 0;
                    NearestKeyId = null;
                    _keyCooldownEndTimestamp = timestampUs + (_cooldownMs * MathConstants.MicrosecondsPerMillisecond);
                    KeyDwellCompleted?.Invoke(nearestId);
                }
            }
        }
    }

    // Reports an abandonment of the CURRENT NearestKeyId (still set at the call point) if it had
    // accumulated at least the threshold. No-op when there is no sink or no in-progress key.
    // The gazePoint fallback for the center is unreachable in practice — key ids are stable within
    // a layout, so keyScreenPositions always contains NearestKeyId here — but keeps this total.
    private void EmitAbandonIfNeeded(Dictionary<string, PhysicalPoint> keyScreenPositions,
        IReadOnlyDictionary<string, PhysicalRect>? keyScreenBoxes,
        PhysicalPoint gazePoint, long timestampUs, AbandonReason reason)
    {
        if (NearestKeyId is null) return;
        if (_sink is null && !E1.Enabled) return;
        long elapsed = timestampUs - _nearestKeyEnteredTimestamp;
        // Progress against the abandoned KEY's own dwell (the escaping floor included), so an
        // escaping key's abandonment is not over-reported relative to the letters around it.
        int dwellMs = KeyDwellMsFor(NearestKeyId);
        double progress = Math.Min(1.0, (double)elapsed / (dwellMs * MathConstants.MicrosecondsPerMillisecond));
        // Shared with DwellDetector: cross-context comparability in Phase-1b scoring depends on every
        // surface using the same cut-off.
        if (progress < TelemetryThresholds.AbandonProgress) return;
        var center = keyScreenPositions.TryGetValue(NearestKeyId, out var c) ? c : gazePoint;
        RecordAbandonE1(NearestKeyId, keyScreenBoxes, gazePoint, center, timestampUs, progress, reason);
        if (_sink is null) return;
        _sink.Report(new DwellOutcome(
            timestampUs, DwellContext.Keyboard, NearestKeyId,
            OutcomeKind.Abandoned, reason, progress, gazePoint, center, 0, null));
    }

    private void EmitActivation(Dictionary<string, PhysicalPoint> keyScreenPositions,
        PhysicalPoint gazePoint, string keyId, long timestampUs,
        IReadOnlyDictionary<string, PhysicalRect>? keyScreenBoxes)
    {
        // E1 first: it reads CurrentInput and the layout BEFORE the key's action (which the
        // KeyDwellCompleted raise below triggers) changes either.
        RecordKeyE1(keyScreenPositions, keyScreenBoxes, gazePoint, keyId, timestampUs);
        if (_sink is null) return;
        // gazePoint fallback is unreachable in practice (keyId just matched a dictionary entry).
        var center = keyScreenPositions.TryGetValue(keyId, out var c) ? c : gazePoint;
        _sink.Report(new DwellOutcome(
            timestampUs, DwellContext.Keyboard, keyId,
            OutcomeKind.Activated, AbandonReason.None, 1.0, gazePoint, center, 0, null));
    }

    // ---- E1 ------------------------------------------------------------------------------

    private void StartDwellOffsets(Dictionary<string, PhysicalPoint> keyScreenPositions,
        PhysicalPoint gazePoint, string keyId)
    {
        _e1SumDxPx = 0;
        _e1SumDyPx = 0;
        _e1Samples = 0;
        AddDwellOffset(keyScreenPositions, gazePoint, keyId);
    }

    private void AddDwellOffset(Dictionary<string, PhysicalPoint> keyScreenPositions,
        PhysicalPoint gazePoint, string keyId)
    {
        if (!keyScreenPositions.TryGetValue(keyId, out var center)) return;
        _e1SumDxPx += gazePoint.X - center.X;
        _e1SumDyPx += gazePoint.Y - center.Y;
        _e1Samples++;
    }

    // The key's row/column and definition, found by the same id rule the window mints ids with.
    // Not resolvable (emoji surface, a key from another sub-layout): -1/-1 and no definition, so
    // an emoji's id (which can BE the glyph) is never read, let alone logged.
    private bool TryResolveKey(string keyId, out KeyDefinition? definition, out int row, out int col)
    {
        definition = null;
        row = col = -1;
        if (_layout is null || IsEmojiMode
            || !_layout.SubLayouts.TryGetValue(ActiveSubLayoutId, out var sub))
            return false;

        for (var r = 0; r < sub.Rows.Count; r++)
        {
            for (var c = 0; c < sub.Rows[r].Count; c++)
            {
                var key = sub.Rows[r][c];
                var id = !string.IsNullOrEmpty(key.SpecialAction) ? key.SpecialAction : $"key_{r}_{c}";
                if (id != keyId) continue;
                definition = key;
                row = r;
                col = c;
                return true;
            }
        }
        return false;
    }

    // A line for a text key (letter, digit, symbol, emoji) never names the key and never carries a
    // pixel offset. One closed class says which outer quarter of the box the gaze rested in.
    private static bool IsTextKey(string cls) => KeyboardE1Recorder.IsIdentityFree(cls);

    // The row's position among the rows that HOLD TEXT KEYS (a row needs at least three plain keys),
    // as a top/mid/bot third. The Space/function row holds none and is not counted.
    private string TextRowBand(int row)
    {
        if (_layout is null || !_layout.SubLayouts.TryGetValue(ActiveSubLayoutId, out var sub) || row < 0)
            return KeyboardE1Slip.None;
        int ordinal = -1, count = 0;
        for (var r = 0; r < sub.Rows.Count; r++)
        {
            var plain = sub.Rows[r].Count(k => string.IsNullOrEmpty(k.SpecialAction));
            if (plain < KeyboardE1Slip.MinTextKeysPerTextRow) continue;
            if (r == row) ordinal = count;
            count++;
        }
        return KeyboardE1Slip.RowBand(ordinal, count);
    }

    // The letter box goes on a `layout` line once per (re)layout, ahead of the first key line after it.
    private void EnsureLayoutLineE1()
    {
        if (_e1LayoutLoggedToken == _e1LayoutToken) return;
        _e1LayoutLoggedToken = _e1LayoutToken;
        var (w, h) = LetterBoxPx();
        E1.RecordLayout(ActiveSubLayoutId, w, h);
    }

    // Forget everything pending that describes typed text.
    private void DropTextStateE1()
    {
        _slipCandidate = null;
        _slipArmed = false;
        _holdOnActive = false;
        _e1Restarts = 0;
        _e1PreviousFireWasText = false;
        LastKeySeq = 0;
    }

    private void RecordAbandonE1(string keyId, IReadOnlyDictionary<string, PhysicalRect>? keyScreenBoxes,
        PhysicalPoint gazePoint, PhysicalPoint center, long timestampUs, double progress, AbandonReason reason)
    {
        try
        {
            if (!E1.Enabled) return;
            if (E1.Muted()) { DropTextStateE1(); return; }

            TryResolveKey(keyId, out var definition, out var row, out var col);
            var (cls, action) = KeyboardE1Classifier.Classify(definition, IsEmojiMode);
            var geometry = E1Geometry?.Invoke() ?? KeyboardE1Geometry.Unknown;
            // The offset is the MEAN over the abandoned dwell (where the gaze rested on the key), not the
            // sample that ended it: on NearestChanged that sample already sits on the NEXT key, and its
            // offset would hand over the direction from one key to the next.
            var meanDx = _e1Samples > 0 ? _e1SumDxPx / _e1Samples : gazePoint.X - center.X;
            var meanDy = _e1Samples > 0 ? _e1SumDyPx / _e1Samples : gazePoint.Y - center.Y;
            var edgeQuarter = KeyboardE1Slip.None;
            if (IsTextKey(cls) && keyScreenBoxes is not null && keyScreenBoxes.TryGetValue(keyId, out var box))
                edgeQuarter = KeyboardE1Slip.EdgeQuarter(meanDx, meanDy, box.Width, box.Height);
            EnsureLayoutLineE1();
            E1.RecordAbandon(new AbandonE1(
                timestampUs, row, col, cls, action,
                (int)Math.Round(progress * 100), reason.ToString(),
                (int)Math.Round(meanDx), (int)Math.Round(meanDy), edgeQuarter,
                ActiveSubLayoutId, geometry.Edge));
        }
        catch (Exception)
        {
            // Logging must never swallow the gaze path.
        }
    }

    private void RecordKeyE1(Dictionary<string, PhysicalPoint> keyScreenPositions,
        IReadOnlyDictionary<string, PhysicalRect>? keyScreenBoxes,
        PhysicalPoint gazePoint, string keyId, long timestampUs)
    {
        // The fire point is where the NEXT dwell's saccade is measured from, whether or not this
        // activation is logged.
        var previousFirePoint = _e1LastFirePoint;
        var hadPreviousFire = _e1HasLastFire;
        _e1LastFirePoint = gazePoint;
        _e1HasLastFire = true;

        // F10: E1 runs BEFORE the key's action; a fault in it (a callback, the dictionary scan, a
        // logger) must never swallow the user's key press.
        try
        {
            if (!E1.Enabled) return;
            if (E1.Muted()) { DropTextStateE1(); return; }
            FinishHoldOn(capped: false);

            TryResolveKey(keyId, out var definition, out var row, out var col);
            var (cls, action) = KeyboardE1Classifier.Classify(definition, IsEmojiMode);
            var textKey = IsTextKey(cls);
            var center = keyScreenPositions.TryGetValue(keyId, out var c) ? c : gazePoint;
            var dx = gazePoint.X - center.X;
            var dy = gazePoint.Y - center.Y;
            var meanDx = _e1Samples > 0 ? _e1SumDxPx / _e1Samples : dx;
            var meanDy = _e1Samples > 0 ? _e1SumDyPx / _e1Samples : dy;
            int boxW = 0, boxH = 0;
            PhysicalRect? boxRect = null;
            if (keyScreenBoxes is not null && keyScreenBoxes.TryGetValue(keyId, out var box))
            {
                boxW = (int)Math.Round(box.Width);
                boxH = (int)Math.Round(box.Height);
                boxRect = box;
            }
            var geometry = E1Geometry?.Invoke() ?? KeyboardE1Geometry.Unknown;

            // The saccade into this key as one of three classes in key pitches: the displacement from
            // the PREVIOUS fire point to where this dwell began (independent of the cooldown). Logged on
            // a non-text key's line, and for a text key only if it is later corrected.
            var pitch = KeyboardE1Slip.Pitch(keyScreenPositions.Values);
            var saccadeClass = hadPreviousFire
                ? KeyboardE1Slip.SaccadeClass(
                    Math.Sqrt(_e1DwellStartPoint.DistanceSquaredTo(previousFirePoint)), pitch)
                : KeyboardE1Slip.None;

            // Backspace class: inside the word being typed (a correction) vs into committed text.
            var backspaceClass = KeyboardE1Classifier.None;
            if (cls is KeyE1.Backspace or KeyE1.DeleteWord)
                backspaceClass = CurrentInput.Length > 0 ? "word" : _recentText.Length > 0 ? "committed" : "none";

            // Shadow likely-bit: was the activated letter among the 5 letters that most often continue
            // the prefix? Evaluated once per letter keystroke, on the prefix as it stands before the
            // key's action, KEPT in memory, and logged only if the letter is corrected. SHADOW ONLY.
            bool likely = false, wordStart = false, noContinuation = false;
            if (cls == KeyE1.Letter && definition is not null)
            {
                var output = IsShifted && !string.IsNullOrEmpty(definition.ShiftOutput)
                    ? definition.ShiftOutput : definition.Output;
                var letters = _orchestrator?.WordPredictor.LikelyNextLetters(CurrentInput, 5);
                wordStart = CurrentInput.Length == 0;
                noContinuation = letters is null || (!wordStart && letters.Count == 0);
                likely = letters is not null && output.Length > 0
                    && letters.Contains(char.ToLowerInvariant(output[0]));
            }

            var edgeQuarter = textKey ? KeyboardE1Slip.EdgeQuarter(meanDx, meanDy, boxW, boxH) : KeyboardE1Slip.None;

            // A NON-text key's saccade and restart count describe the path from the PREVIOUS fire. When
            // that was a text key they would carry its position (the distance letter -> Space, the keys
            // crossed on the way), so they are withheld (saccade "-", restarts -1) in that case and logged
            // only after another non-text key.
            var lineSaccade = _e1PreviousFireWasText ? KeyboardE1Slip.None : saccadeClass;
            var lineRestarts = _e1PreviousFireWasText ? -1 : _e1Restarts;
            EnsureLayoutLineE1();
            LastKeySeq = E1.RecordKey(new KeyE1(
                timestampUs, row, col, cls, action,
                boxW, boxH, ActiveSubLayoutId, geometry.Edge,
                geometry.X, geometry.Y, geometry.Width, geometry.Height, geometry.SinceMoveMs,
                (int)Math.Round(dx), (int)Math.Round(dy), (int)Math.Round(meanDx), (int)Math.Round(meanDy),
                edgeQuarter, lineRestarts, lineSaccade,
                KeyDwellMsFor(keyId), PenaltyApplied: false,
                CurrentInput.Length > 0, backspaceClass));
            _e1PreviousFireWasText = textKey;

            var corr = new CorrE1(saccadeClass, _e1Restarts, likely, wordStart, noContinuation);
            _e1Restarts = 0;

            TrackSlipE1(cls, backspaceClass, center, boxRect, TextRowBand(row), pitch, corr, LastKeySeq);

            // Hold-on: from the fire, how long does the gaze stay in this key's box (capped)?
            if (LastKeySeq > 0)
            {
                _holdOnActive = true;
                _holdOnSeq = LastKeySeq;
                _holdOnKeyId = keyId;
                _holdOnFireTimestampUs = timestampUs;
                _holdOnLastInsideUs = timestampUs;
            }
        }
        catch (Exception)
        {
            // Logging must never swallow a key press.
            _holdOnActive = false;
        }
    }

    /// <summary>
    /// The slip state machine: letter/digit → in-word Backspace within 3 s → replacement letter/digit
    /// logs ONE relation line (adjacent?, compass point, distance in pitches, ms to the Backspace,
    /// row band of the slipped key among the text rows, and the slipped key's own saccade class,
    /// restarts and likelihood bits). The Backspace that takes the letter back also logs a keyed
    /// <c>corr</c> line with the same arrival facts. Anything else in between (Space, Shift, a second
    /// Backspace, a mode key) drops the candidate. The slipped key is kept as geometry only.
    /// </summary>
    private void TrackSlipE1(string cls, string backspaceClass, PhysicalPoint centre, PhysicalRect? box,
        string band, double pitch, CorrE1 corr, long seq)
    {
        var now = E1.NowMs;
        if (cls is KeyE1.Letter or KeyE1.Digit)
        {
            if (_slipArmed && _slipCandidate is { } slipped)
            {
                var adjacent = pitch > 0 && slipped.Box is { } slippedBox && box is { } replacementBox
                    && KeyboardE1Slip.Adjacent(slippedBox, replacementBox, pitch);
                E1.RecordSlip(adjacent,
                    adjacent ? KeyboardE1Slip.Direction(slipped.Centre, centre) : KeyboardE1Slip.None,
                    KeyboardE1Slip.DistancePitches(slipped.Centre, centre, pitch),
                    (int)(_slipBackspaceAtMs - slipped.AtMs), slipped.Band, slipped.Corr);
            }

            _slipCandidate = new SlipCandidate(centre, box, band, now, corr);
            _slipArmed = false;
            return;
        }

        if (cls == KeyE1.Backspace && backspaceClass == "word" && !_slipArmed
            && _slipCandidate is { } candidate && now - candidate.AtMs <= KeyboardE1Recorder.SlipWindowMs)
        {
            _slipArmed = true;
            _slipBackspaceAtMs = now;
            E1.RecordCorrection(seq, candidate.Corr);
            return;
        }

        _slipCandidate = null;
        _slipArmed = false;
    }

    private void UpdateHoldOn(Dictionary<string, PhysicalPoint> keyScreenPositions,
        IReadOnlyDictionary<string, PhysicalRect>? keyScreenBoxes, PhysicalPoint gazePoint,
        bool isGazeInsideWindow, long timestampUs, long sinceLastSampleUs)
    {
        bool inside;
        if (!isGazeInsideWindow || sinceLastSampleUs > MaxContinuousSampleGapUs)
            inside = false;
        else if (keyScreenBoxes is not null && keyScreenBoxes.TryGetValue(_holdOnKeyId, out var box))
            inside = box.Contains(gazePoint);
        else if (keyScreenBoxes is null)
            inside = KeyTargeting.PickKey(gazePoint, keyScreenPositions, null) == _holdOnKeyId;
        else
            inside = false; // the key's box is gone (a rebuild): the hold ended

        if (!inside)
        {
            FinishHoldOn(capped: false);
            return;
        }

        _holdOnLastInsideUs = timestampUs;
        if (timestampUs - _holdOnFireTimestampUs >= HoldOnCapMs * MathConstants.MicrosecondsPerMillisecond)
            FinishHoldOn(capped: true);
    }

    private void FinishHoldOn(bool capped)
    {
        if (!_holdOnActive) return;
        _holdOnActive = false;
        var ms = (int)Math.Min(HoldOnCapMs,
            (_holdOnLastInsideUs - _holdOnFireTimestampUs) / MathConstants.MicrosecondsPerMillisecond);
        E1.RecordHoldOn(_holdOnSeq, ms, capped);
    }

    public void SetLayout(KeyboardLayout layout)
    {
        _layout = layout;
        ActiveSubLayoutId = layout.DefaultSubLayout;

        // M3: a layout switch is an unsupervised swap — clear the D13 fields like
        // ResetInteractionState does. The anchor only ever blocks, but a stale one measured
        // against the OLD layout's grid is meaningless on the new one.
        _swapSuppressedAnchor = null;
        _lastGazeInsideWindowPoint = null;

        // M7 (impl review M2), corrected r2 N1/N4: raised AFTER the assignment above, so a
        // raise landing before it would repaint the OLD grid. On the production reload path
        // this raise alone does NOT hold — KeyboardWindow.LoadLayout follows SetLayout with
        // RebuildKeys(), whose own end-of-rebuild repaint (r2 N1) is what makes a reload
        // with Caps armed land lit; the raise repaints a grid the rebuild then discards. It
        // remains for paths that set a layout WITHOUT a window rebuild. Without either, the
        // stale grid shows the Caps key unlit and the SHIFT-BEARING labels unshifted (QWERTZ
        // ß, AZERTY é è ç à) while the keys type capitals — plain letter labels are already
        // capitals and never go stale.
        OnPropertyChanged(nameof(ActiveModifiers));
    }

    public void SetOrchestrator(PredictionOrchestrator? orchestrator)
    {
        if (ReferenceEquals(_orchestrator, orchestrator))
            return;

        _orchestrator?.Dispose();
        _orchestrator = orchestrator;
    }

    public void ToggleModifier(ModifierKey modifier)
    {
        var wasOn = _activeModifiers.Remove(modifier);
        if (!wasOn)
            _activeModifiers.Add(modifier);
        E1.RecordMode(KeyboardE1Classifier.ModifierKind(modifier), wasOn ? "on" : "off", wasOn ? "off" : "on");
        OnPropertyChanged(nameof(ActiveModifiers));
        OnPropertyChanged(nameof(IsShifted));
    }

    private void ClearOneShotModifiers()
    {
        _activeModifiers.Remove(ModifierKey.Shift);
        _activeModifiers.Remove(ModifierKey.Ctrl);
        _activeModifiers.Remove(ModifierKey.Alt);
        _activeModifiers.Remove(ModifierKey.Win);
        OnPropertyChanged(nameof(ActiveModifiers));
        OnPropertyChanged(nameof(IsShifted));
    }

    /// <summary>D15: entering an auto-return sub-layout clears armed one-shot SYSTEM modifiers.
    /// Accents carries no modifier keys, so an armed modifier would be invisible there — and a
    /// stray Win plus an accent is a system shortcut on the OS layout (Win+é = Win+2 on French
    /// Windows). Shift and Caps Lock survive: accents carry their own capitals (D11).</summary>
    private void ClearSystemOneShotModifiers()
    {
        _activeModifiers.Remove(ModifierKey.Ctrl);
        _activeModifiers.Remove(ModifierKey.Alt);
        _activeModifiers.Remove(ModifierKey.Win);
        OnPropertyChanged(nameof(ActiveModifiers));
    }

    private bool HasSystemModifiers =>
        _activeModifiers.Contains(ModifierKey.Ctrl)
        || _activeModifiers.Contains(ModifierKey.Alt)
        || _activeModifiers.Contains(ModifierKey.Win);

    private IReadOnlySet<ModifierKey>? ModifiersSnapshot =>
        _activeModifiers.Count > 0 ? new HashSet<ModifierKey>(_activeModifiers) : null;

    public void ActivateKey(string output, string? shiftOutput)
    {
        var actualOutput = IsShifted && !string.IsNullOrEmpty(shiftOutput)
            ? shiftOutput
            : output;

        // System modifier combos (Ctrl+C, Alt+F4, etc.) are shortcuts, not text input
        if (!HasSystemModifiers)
        {
            CurrentInput += actualOutput;
            _orchestrator?.LearnWord(actualOutput);
        }
        Messenger.Send(new KeyActivatedMessage(actualOutput, ModifiersSnapshot));
        ClearOneShotModifiers();
        AutoReturnIfNeeded();
    }

    public void HandleBackspace()
    {
        if (!HasSystemModifiers && CurrentInput.Length > 0)
            CurrentInput = CurrentInput[..^1];
        else if (!HasSystemModifiers && CurrentInput.Length == 0 && _recentText.Length > 0)
        {
            // User is deleting previously committed text, update _recentText
            _recentText = _recentText[..^1];
        }
        Messenger.Send(new KeyActivatedMessage("\b", ModifiersSnapshot));
        ClearOneShotModifiers();
    }

    /// <summary>
    /// Ctrl+Backspace word deletion, mirroring <see cref="HandleBackspace"/>: the keystroke goes
    /// to the focused app while <see cref="CurrentInput"/> (and, once that is empty, the committed
    /// text) loses the same word, so the prediction context keeps tracking what the target app
    /// now shows.
    /// </summary>
    public void HandleDeleteWord()
    {
        if (!HasSystemModifiers)
        {
            if (CurrentInput.Length > 0)
                CurrentInput = TrimLastWord(CurrentInput);
            else if (_recentText.Length > 0)
                _recentText = TrimLastWord(_recentText);
        }
        var modifiers = new HashSet<ModifierKey>(_activeModifiers) { ModifierKey.Ctrl };
        Messenger.Send(new KeyActivatedMessage("\b", modifiers));
        ClearOneShotModifiers();
    }

    /// <summary>
    /// The "Add Word" layout key (Fn clipboard row): teaches the predictor the word currently
    /// being typed, on demand. Space-commit learning stays the silent default; this is the
    /// explicit path for a word the user WANTS predicted before it has ever been committed —
    /// the newly learned word surfaces in the bar on the caller's refresh. A local teach action,
    /// not a typing action: no keystroke to the OS, the input keeps its word, and the next-word
    /// context does not advance (committing is Space's job). Empty input is a no-op.
    /// </summary>
    public void HandleAddWord()
    {
        if (CurrentInput.Length == 0) return;
        _orchestrator?.LearnWord(CurrentInput);
    }

    /// <summary>
    /// Cuts the final word and the separators after it ("thank you " → "thank "). A word is a
    /// run of letters or digits, because mainstream apps' Ctrl+Backspace stops at hyphens and
    /// other punctuation ("well-known " loses "known", keeps "well-") — the bookkeeping must
    /// agree with the keystroke the target app just received.
    /// </summary>
    private static string TrimLastWord(string text)
    {
        var end = text.Length;
        while (end > 0 && !char.IsLetterOrDigit(text[end - 1])) end--;
        var start = end;
        while (start > 0 && char.IsLetterOrDigit(text[start - 1])) start--;
        return text[..start];
    }

    public void HandleEnter()
    {
        if (!HasSystemModifiers)
        {
            CurrentInput = "";
            _previousWord = null;
            _recentText = ""; // Clear on new paragraph/line
        }
        Messenger.Send(new KeyActivatedMessage("\n", ModifiersSnapshot));
        ClearOneShotModifiers();
    }

    public void HandleSpace()
    {
        if (!HasSystemModifiers)
        {
            if (CurrentInput.Length > 0)
            {
                _orchestrator?.LearnWord(CurrentInput);
                _previousWord = CurrentInput;
                AppendRecentText(CurrentInput);
            }
            CurrentInput = "";
        }
        Messenger.Send(new KeyActivatedMessage(" ", ModifiersSnapshot));
        ClearOneShotModifiers();
    }

    public void HandleTab()
    {
        Messenger.Send(new KeyActivatedMessage("\t", ModifiersSnapshot));
        ClearOneShotModifiers();
    }

    public void SwitchSubLayout(string subLayoutId)
    {
        if (_layout == null || !_layout.SubLayouts.ContainsKey(subLayoutId)) return;
        if (ActiveSubLayoutId == subLayoutId) return;
        var fromSubLayout = ActiveSubLayoutId;

        // D13: arm the suppression BEFORE the swap — and the "before" is load-bearing:
        // _lastKeyScreenPositions IS the window's live _keyScreenPositions dictionary
        // (declared in KeyboardWindow, handed to ProcessKeyGaze by UpdateGaze — an alias,
        // not a snapshot), and the `ActiveSubLayoutId = subLayoutId;` assignment below runs
        // RebuildKeys → ClearKeyState SYNCHRONOUSLY (the ActiveSubLayoutId property-changed
        // case in OnViewModelPropertyChanged), which EMPTIES that dictionary. Computing the
        // anchor/radius after the assignment would read a cleared dict (positions.Count < 2),
        // null the anchor and silently disarm D13 in the real app while every VM test stays
        // green (VM tests pass a static dict nothing clears) — the ordering pinned by the
        // real-window resting-gaze probe in KeyboardGateProbeTests (impl-review I1).
        // Anchor only when the gaze was actually on the key area; a prediction pick
        // happens with the gaze on the bar and its auto-return swap must suppress nothing
        // (plan review H3). Radius = half the distance to the second-nearest old centre
        // (≈ half a key width; a resting-on-centre gaze yields d1≈0, d2≈pitch). Fewer than
        // two positioned old centres means no suppression — otherwise d2 stays
        // double.MaxValue and the radius becomes effectively infinite (plan-r2).
        ArmDwellSuppression();

        ActiveSubLayoutId = subLayoutId;
        if (!_inAutoReturn)
            E1.RecordMode("sublayout", fromSubLayout, subLayoutId);

        // D15 … and M7: RebuildKeys (synchronous, on the assignment above) draws static
        // labels and — since impl-review r2 N1 — repaints modifier state itself at its end,
        // so this raise is REDUNDANT on every rebuild path and kept as backup for any future
        // rebuild path that forgets to (defense in depth, mirroring the notes on the
        // ResetInteractionState/SetLayout raises). It must still run AFTER the swap so any
        // repaint it causes lands on the NEW grid (plan review H1).
        if (_layout.SubLayouts[subLayoutId].AutoReturn)
            ClearSystemOneShotModifiers();
        OnPropertyChanged(nameof(ActiveModifiers));
    }

    /// <summary>
    /// Arms the D13 spatial suppression at the gaze's last in-grid position: while the gaze stays
    /// within about half a key of it, nothing dwells. Used by a sub-layout swap (manual or
    /// auto-return), which must re-anchor against the OLD grid; a window move uses the
    /// non-destructive <see cref="ArmDwellSuppressionForMove"/>.
    ///
    /// <para>
    /// The anchor is only set when the last gaze sample was on the key area (a prediction pick
    /// happens on the bar and must suppress nothing — plan review H3). The radius is half the
    /// distance to the second-nearest centre of the OLD grid (≈ half a key width; a
    /// resting-on-centre gaze yields d1≈0, d2≈pitch). Fewer than two positioned centres means
    /// no suppression — otherwise d2 stays double.MaxValue and the radius becomes effectively
    /// infinite (plan-r2).
    /// </para>
    /// </summary>
    public void ArmDwellSuppression() => ArmDwellSuppressionCore(clearWhenUnavailable: true);

    /// <summary>
    /// The arm for a WINDOW move (R2-1). It is deliberately NON-destructive: the move that
    /// follows a sub-layout swap runs during RebuildKeys, after ClearKeyState has emptied the
    /// key-position dictionary but while the swap's anchor is live. Re-arming there would take
    /// the "no centres" path and NULL the swap's anchor, letting a resting gaze type the key
    /// that landed under it (measured: qwertz letters→Fn types space). So: a live anchor is
    /// kept; a fresh one is computed only when none is live and the old centres are known; and
    /// an unavailable computation leaves the anchor alone instead of clearing it.
    /// </summary>
    public void ArmDwellSuppressionForMove() => ArmDwellSuppressionCore(clearWhenUnavailable: false);

    private void ArmDwellSuppressionCore(bool clearWhenUnavailable)
    {
        if (!clearWhenUnavailable && _swapSuppressedAnchor is not null)
            return;   // a live swap/yield arm wins; the rebuild's move must not disturb it

        if (_lastGazeInsideWindowPoint is { } anchor
            && _lastKeyScreenPositions is { } positions
            && positions.Count >= 2)
        {
            var d1 = double.MaxValue;
            var d2 = double.MaxValue;
            foreach (var center in positions.Values)
            {
                var d = anchor.DistanceSquaredTo(center);
                if (d < d1)
                {
                    d2 = d1;
                    d1 = d;
                }
                else if (d < d2)
                {
                    d2 = d;
                }
            }

            _swapSuppressedAnchor = anchor;
            _swapSuppressedClearRadiusSquared = 0.25 * d2;
            return;
        }

        if (clearWhenUnavailable)
            _swapSuppressedAnchor = null;
    }

    /// <summary>
    /// Returns to the layout's default sub-layout after TEXT was emitted from an
    /// <c>autoReturn</c> sub-layout (spec D5/D14): a non-special key activation or a prediction
    /// selection. Never fires in emoji mode — emoji keys carry Output and flow through the same
    /// activation path while IsEmojiMode overlays the text sub-layout, and swapping the grid
    /// under the open emoji surface is the RebuildKeys trap (the IsEmojiMode property-changed
    /// case in KeyboardWindow's OnViewModelPropertyChanged).
    /// </summary>
    private void AutoReturnIfNeeded()
    {
        if (IsEmojiMode) return;
        if (_layout is null
            || !_layout.SubLayouts.TryGetValue(ActiveSubLayoutId, out var sub)
            || !sub.AutoReturn)
            return;
        // The keyboard going back by itself is not the user's mode change, so E1 does not log it.
        _inAutoReturn = true;
        try { SwitchSubLayout(_layout.DefaultSubLayout); }
        finally { _inAutoReturn = false; }
    }

    public List<string> GetSuggestions()
    {
        // Implement in terms of GetSuggestionsWithContext to avoid duplication
        return GetSuggestionsWithContext()
            .Select(r => r.Word)
            .ToList();
    }

    /// <summary>
    /// Gets prediction suggestions with full context metadata for UI display.
    /// Returns PredictionResult objects containing type, confidence, and scoring information.
    /// </summary>
    public List<PredictionResult> GetSuggestionsWithContext()
    {
        if (_orchestrator == null) return new List<PredictionResult>();

        // Pass full text context for smart capitalization if enabled
        var fullText = _settings.Keyboard.SmartCapitalizationEnabled ? _recentText : null;

        return _orchestrator.GetSuggestions(CurrentInput, _previousWord,
            maxCount: _settings.Keyboard.MaxSuggestions, fullText: fullText);
    }

    public void HandlePredictionSelected(string word)
    {
        // Sentence completions and phrases arrive as multi-word text. LearnWord stores its argument
        // verbatim as one dictionary key, so learning that would put a whole sentence — punctuation
        // included — into the persisted user dictionary and then suggest it back as a single word.
        // LearnSequence learns the individual words plus their bigrams and phrase, which is what the
        // user actually typed; only the last word can serve as bigram context for the next one.
        var words = word.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1)
            _orchestrator?.LearnSequence(words);
        else
            _orchestrator?.LearnWord(word);

        var deleteCount = CurrentInput.Length;
        CurrentInput = "";
        _previousWord = words.Length > 0 ? words[^1] : word;
        AppendRecentText(word);
        Messenger.Send(new PredictionSelectedMessage(word, deleteCount));
        AutoReturnIfNeeded();
    }

    public void HandleSpecialKey(string keyName)
    {
        Messenger.Send(new SpecialKeyMessage(keyName, ModifiersSnapshot));
        ClearOneShotModifiers();
    }

    /// <summary>
    /// A "shortcut:&lt;chord&gt;" layout key (the Fn clipboard row, or any chord a layout author
    /// wants as a single dwell). The chord's modifiers are unioned with the active one-shot
    /// modifiers — a shortcut key composes with them like any other key — then routed over the
    /// same messages a hand-built combo uses: a single-character payload rides
    /// <see cref="KeyActivatedMessage"/> (whose dispatcher path resolves the character to a VK
    /// and merges modifiers the character itself requires, e.g. Shift for '!'), anything else
    /// rides <see cref="SpecialKeyMessage"/> (named keys carry the extended-key flag there).
    /// </summary>
    public void HandleShortcut(string chord)
    {
        if (KeyboardShortcut.TryParse(chord, out var chordModifiers, out var key))
        {
            var modifiers = new HashSet<ModifierKey>(_activeModifiers);
            modifiers.UnionWith(chordModifiers);

            if (key.Length == 1)
                Messenger.Send(new KeyActivatedMessage(key, modifiers));
            else
                Messenger.Send(new SpecialKeyMessage(key, modifiers));
        }

        // Cleared on both paths like every other Handle*: an armed one-shot modifier must not
        // leak into the next dwell just because this chord was unparseable.
        ClearOneShotModifiers();
    }

    /// <summary>
    /// Appends text to recent text buffer for capitalization context, maintaining ~100 char limit.
    /// </summary>
    private void AppendRecentText(string text)
    {
        _recentText += text + " ";
        if (_recentText.Length > 100)
            _recentText = _recentText[^100..];
    }

    protected override void OnDispose()
    {
        if (_effectiveSettings is not null) _effectiveSettings.Changed -= _onEffectiveSettingsChanged;
        _orchestrator?.Dispose();
    }
}
