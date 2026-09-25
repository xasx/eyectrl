namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
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

    public KeyboardViewModel(EyeCtrlSettings settings, IMessenger messenger,
        IStruggleTelemetrySink? sink = null, EffectiveSettingsProvider? effectiveSettings = null)
        : base(messenger)
    {
        _settings = settings;
        _sink = sink;
        _effectiveSettings = effectiveSettings;
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

        IsEmojiMode = false;
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
    /// Nearest-key dwell tracking: finds the closest key to the gaze point and
    /// tracks dwell time directly. Eliminates dead zones between keys and avoids
    /// circular hit-area overlap issues with rectangular keyboard keys.
    /// </summary>
    public void ProcessKeyGaze(Dictionary<string, PhysicalPoint> keyScreenPositions,
        PhysicalPoint gazePoint, bool isGazeInsideWindow, long timestampUs)
    {
        if (keyScreenPositions.Count == 0 || _keyDwellMs <= 0) return;

        _lastKeyScreenPositions = keyScreenPositions;
        if (isGazeInsideWindow)
            _lastGazeInsideWindowPoint = gazePoint;

        // How long the stream was silent since the last DELIVERED sample, measured before this
        // sample replaces the mark. See MaxContinuousSampleGapUs.
        long sinceLastSampleUs = timestampUs - _lastSampleTimestampUs;
        _lastSampleTimestampUs = timestampUs;

        if (!isGazeInsideWindow)
        {
            if (NearestKeyId != null)
            {
                EmitAbandonIfNeeded(keyScreenPositions, gazePoint, timestampUs, AbandonReason.GazeLeft);
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

        // Find nearest key
        string? nearestId = null;
        double nearestDist = double.MaxValue;

        foreach (var (id, center) in keyScreenPositions)
        {
            double dist = gazePoint.DistanceSquaredTo(center);
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearestId = id;
            }
        }

        if (nearestId != NearestKeyId)
        {
            EmitAbandonIfNeeded(keyScreenPositions, gazePoint, timestampUs, AbandonReason.NearestChanged);
            NearestKeyProgress = 0;
            NearestKeyId = nearestId;
            _nearestKeyEnteredTimestamp = timestampUs;
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
            }
            else
            {
                long elapsed = timestampUs - _nearestKeyEnteredTimestamp;
                int dwellMs = KeyDwellMsFor(nearestId);
                NearestKeyProgress = Math.Min(1.0, (double)elapsed / (dwellMs * MathConstants.MicrosecondsPerMillisecond));

                if (elapsed >= dwellMs * MathConstants.MicrosecondsPerMillisecond)
                {
                    EmitActivation(keyScreenPositions, gazePoint, nearestId, timestampUs);
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
        PhysicalPoint gazePoint, long timestampUs, AbandonReason reason)
    {
        if (_sink is null || NearestKeyId is null) return;
        long elapsed = timestampUs - _nearestKeyEnteredTimestamp;
        // Progress against the abandoned KEY's own dwell (the escaping floor included), so an
        // escaping key's abandonment is not over-reported relative to the letters around it.
        int dwellMs = KeyDwellMsFor(NearestKeyId);
        double progress = Math.Min(1.0, (double)elapsed / (dwellMs * MathConstants.MicrosecondsPerMillisecond));
        // Shared with DwellDetector: cross-context comparability in Phase-1b scoring depends on every
        // surface using the same cut-off.
        if (progress < TelemetryThresholds.AbandonProgress) return;
        var center = keyScreenPositions.TryGetValue(NearestKeyId, out var c) ? c : gazePoint;
        _sink.Report(new DwellOutcome(
            timestampUs, DwellContext.Keyboard, NearestKeyId,
            OutcomeKind.Abandoned, reason, progress, gazePoint, center, 0, null));
    }

    private void EmitActivation(Dictionary<string, PhysicalPoint> keyScreenPositions,
        PhysicalPoint gazePoint, string keyId, long timestampUs)
    {
        if (_sink is null) return;
        // gazePoint fallback is unreachable in practice (keyId just matched a dictionary entry).
        var center = keyScreenPositions.TryGetValue(keyId, out var c) ? c : gazePoint;
        _sink.Report(new DwellOutcome(
            timestampUs, DwellContext.Keyboard, keyId,
            OutcomeKind.Activated, AbandonReason.None, 1.0, gazePoint, center, 0, null));
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
        if (!_activeModifiers.Remove(modifier))
            _activeModifiers.Add(modifier);
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
        }
        else
        {
            _swapSuppressedAnchor = null;
        }

        ActiveSubLayoutId = subLayoutId;

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
        SwitchSubLayout(_layout.DefaultSubLayout);
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
