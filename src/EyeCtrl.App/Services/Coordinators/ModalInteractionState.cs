namespace EyeCtrl.App.Services.Coordinators;

using Serilog;

/// <summary>
/// Shared modal-interaction flags consumed by the gaze-routing early-out (now in
/// ToolbarLifecycleCoordinator.SetupGazeRouting after the cluster-8 fold) and by the keyboard/
/// settings sub-coordinators (T5/T7). The flags live on a single shared object so every coordinator
/// reads/writes the same volatile state — there is no stale-flag window when the writer methods
/// move out of EventCoordinator.
/// </summary>
internal sealed class ModalInteractionState
{
    // volatile: read on the gaze thread (ToolbarLifecycleCoordinator.SetupGazeRouting's per-sample
    // early-out), written on the UI thread.
    public volatile bool KeyboardVisible;
    public volatile bool SettingsOpen;

    // volatile: read on the gaze thread through the quick-actions summon's and the toolbar
    // auto-hide's calibrationActive gate (both Feed paths run on the gaze thread), written on the
    // UI thread by the probe coordinator (sub-pixel build plan C7, not in this ticket).
    private volatile bool _probeOpen;

    /// <summary>
    /// True while the sub-pixel probe owns the screen (build plan C9). Every consumer gates on it;
    /// C7 sets it for the duration of a run, and while it is false each gate is a no-op so the
    /// pre-probe behaviour is unchanged. Assigning raises <see cref="ProbeOpenChanged"/>.
    /// </summary>
    public bool ProbeOpen
    {
        get => _probeOpen;
        set
        {
            if (_probeOpen == value) return;
            _probeOpen = value;
            RaiseProbeOpenChanged(value);
        }
    }

    /// <summary>
    /// Raised after <see cref="SettingsOpen"/> flips, on the UI thread. The activation-offer
    /// coordinator suspends offers while the settings window is open (ticket 8 fixup, review B F3);
    /// the field remains a plain volatile flag for its gaze-thread readers.
    /// </summary>
    public Action<bool>? SettingsOpenChanged;

    /// <summary>
    /// Raised after <see cref="ProbeOpen"/> flips, on the UI thread. Unlike
    /// <see cref="SettingsOpenChanged"/> (invoked bare), each subscriber is invoked in its own
    /// try/catch: one coordinator's recompute throwing must not stop the remaining gates from
    /// observing the flip — half-armed gates with the probe down is the failure this exists to
    /// prevent.
    /// <para>
    /// Subscribers must read <see cref="ProbeOpen"/>, not the event argument: a re-entrant flip
    /// inside this raise (a subscriber that clears the flag, e.g. via an EndTest path) delivers a
    /// stale value to the subscribers after it.
    /// </para>
    /// </summary>
    public event Action<bool>? ProbeOpenChanged;

    private void RaiseProbeOpenChanged(bool open)
    {
        var subscribers = ProbeOpenChanged;
        if (subscribers is null) return;

        foreach (var subscriber in subscribers.GetInvocationList())
        {
            try
            {
                ((Action<bool>)subscriber)(open);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "A ProbeOpenChanged subscriber threw; the remaining subscribers still run");
            }
        }
    }

    // volatile: read on the gaze thread (ring predicates, toolbar feed checks), written on the
    // UI thread by the guide's raise/close path.
    private volatile bool _guideOpen;

    /// <summary>
    /// True while the first-run guide owns the screen. The guide's card is interactive but the
    /// window is click-through outside it and every proactive surface defers behind it, so every
    /// consumer gates on it like <see cref="SettingsOpen"/>. Assigning raises
    /// <see cref="GuideOpenChanged"/>.
    /// </summary>
    public bool GuideOpen
    {
        get => _guideOpen;
        set
        {
            if (_guideOpen == value) return;
            _guideOpen = value;
            RaiseGuideOpenChanged(value);
        }
    }

    /// <summary>Raised after <see cref="GuideOpen"/> flips, on the UI thread. Per-subscriber
    /// try/catch like <see cref="ProbeOpenChanged"/>; subscribers read <see cref="GuideOpen"/>,
    /// not the argument (re-entrancy).</summary>
    public event Action<bool>? GuideOpenChanged;

    private void RaiseGuideOpenChanged(bool open)
    {
        var subscribers = GuideOpenChanged;
        if (subscribers is null) return;

        foreach (var subscriber in subscribers.GetInvocationList())
        {
            try
            {
                ((Action<bool>)subscriber)(open);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "A GuideOpenChanged subscriber threw; the remaining subscribers still run");
            }
        }
    }

    // volatile: read on the gaze thread (RingMayReshow, the keyboard/settings feed gates, the note
    // gate's polled predicate), written on the UI thread by the Backstage coordinator.
    private volatile bool _backstageOpen;

    /// <summary>
    /// True while the fullscreen Backstage menu owns the screen (Quit / Restart / Calibrate and the
    /// install-maintenance cards). It is a full-surface modal like <see cref="GuideOpen"/>: every
    /// proactive surface defers behind it and no input is fed to anything it covers. Assigning
    /// raises <see cref="BackstageOpenChanged"/>.
    /// </summary>
    public bool BackstageOpen
    {
        get => _backstageOpen;
        set
        {
            if (_backstageOpen == value) return;
            _backstageOpen = value;
            RaiseBackstageOpenChanged(value);
        }
    }

    /// <summary>Raised after <see cref="BackstageOpen"/> flips, on the UI thread. Per-subscriber
    /// try/catch like <see cref="GuideOpenChanged"/>: one gate's recompute throwing must not leave
    /// the rest holding a stale half-armed state. Subscribers read <see cref="BackstageOpen"/>, not
    /// the argument (a re-entrant flip inside this raise delivers a stale value to later subscribers).</summary>
    public event Action<bool>? BackstageOpenChanged;

    private void RaiseBackstageOpenChanged(bool open)
    {
        var subscribers = BackstageOpenChanged;
        if (subscribers is null) return;

        foreach (var subscriber in subscribers.GetInvocationList())
        {
            try
            {
                ((Action<bool>)subscriber)(open);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "A BackstageOpenChanged subscriber threw; the remaining subscribers still run");
            }
        }
    }

    // volatile: read on the gaze thread (the ring's re-show predicates — RingMayReshow and the
    // cursor-visibility binding), written on the UI thread by the guide wiring. Task 1's pattern:
    // a plain volatile field, no event, no property — the wiring sets it directly.
    /// <summary>
    /// True while the first-run guide WANTS the cursor ring up despite owning the screen: step 5
    /// (ToolbarTask) shows the ring so the owner can summon the toolbar, and a pause/resume round
    /// trip during that step must not strand it hidden — the resume's visibility flip consults
    /// <c>RingMayReshow</c>, which would otherwise refuse while <see cref="GuideOpen"/> is still
    /// true. Set by the guide's StepEntered handler (step 5 only), cleared on every other step and
    /// in the guide's restore/unwind paths.
    /// </summary>
    public volatile bool GuideRingWanted;

    // volatile: read on the gaze thread (RingMayReshow's calibration exception, like GuideRingWanted),
    // written on the UI thread by the guide's calibration lifecycle.
    private volatile bool _guideCalibrationActive;

    /// <summary>
    /// True while a calibration the guide started is between its first Collecting/Training message
    /// and its terminal Idle/Cancelled — INCLUDING the Active/Failed result card, whose
    /// Retry/Accept a gaze user can only reach through the activation path the guide suspends
    /// (ruling D3, PR-review n1): the guide's wiring releases its fixation-suppression claim and
    /// sets this flag so the guide's offer-suspension reason and the ring re-show gate stand down
    /// for the calibration surface. That is exactly a NORMAL calibration's state — the engine's
    /// own <c>FixationSuppressor.Calibration</c> claim (not a suspension reason) is what blocks
    /// offers while gaze is collected. Assigning raises
    /// <see cref="GuideCalibrationActiveChanged"/>. <see cref="GuideOpen"/> itself is NOT flipped
    /// mid-calibration: the guide's other claims (banner hold, toolbar hold, notice defers) stay
    /// held, and the deferred notices must not replay over the calibration window.
    /// </summary>
    public bool GuideCalibrationActive
    {
        get => _guideCalibrationActive;
        set
        {
            if (_guideCalibrationActive == value) return;
            _guideCalibrationActive = value;
            RaiseGuideCalibrationActiveChanged(value);
        }
    }

    /// <summary>Raised after <see cref="GuideCalibrationActive"/> flips, on the UI thread.
    /// Per-subscriber try/catch like <see cref="GuideOpenChanged"/>; subscribers read the
    /// property, not the argument (re-entrancy).</summary>
    public event Action<bool>? GuideCalibrationActiveChanged;

    private void RaiseGuideCalibrationActiveChanged(bool active)
    {
        var subscribers = GuideCalibrationActiveChanged;
        if (subscribers is null) return;

        foreach (var subscriber in subscribers.GetInvocationList())
        {
            try
            {
                ((Action<bool>)subscriber)(active);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "A GuideCalibrationActiveChanged subscriber threw; the remaining subscribers still run");
            }
        }
    }
}
