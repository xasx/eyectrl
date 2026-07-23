namespace EyeCtrl.EyeCare;

using EyeCtrl.Core.Models;

/// <summary>Public contract the host calls into to start/stop eye-care. (Lives in EyeCare in v1;
/// relocates to Core for full plugin swappability — see Future Milestones in the spec.)</summary>
public interface IEyeCareService
{
    /// <summary>Feeds one raw gaze sample, the paused state, and whether the user can be
    /// interrupted right now (true only in FreeLook — false while a modal overlay is active).
    /// Drives the scheduler + fatigue monitor.</summary>
    void OnGazeSample(GazePoint sample, bool isPaused, bool canInterrupt = true);

    /// <summary>Force-stops any in-flight break/banner (e.g. when Enabled toggled off).</summary>
    void Cancel();

    /// <summary>Current fatigue score [0..1] from the fatigue monitor (0 when fatigue monitoring is off).</summary>
    double FatigueScore { get; }
}
