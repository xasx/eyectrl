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
    /// with it never set in this ticket, each gate is a no-op and the pre-probe behaviour is
    /// byte-identical. Assigning raises <see cref="ProbeOpenChanged"/>.
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
}
