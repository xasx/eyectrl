namespace EyeCtrl.App.Services.Coordinators;

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

    /// <summary>
    /// Raised after <see cref="SettingsOpen"/> flips, on the UI thread. The activation-offer
    /// coordinator suspends offers while the settings window is open (ticket 8 fixup, review B F3);
    /// the field remains a plain volatile flag for its gaze-thread readers.
    /// </summary>
    public Action<bool>? SettingsOpenChanged;
}
