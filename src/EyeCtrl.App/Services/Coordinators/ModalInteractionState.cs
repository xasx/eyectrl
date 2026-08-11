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
}
