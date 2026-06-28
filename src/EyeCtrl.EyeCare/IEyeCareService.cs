namespace EyeCtrl.EyeCare;

using EyeCtrl.Core.Models;

/// <summary>Public contract the host calls into to start/stop eye-care. (Lives in EyeCare in v1;
/// relocates to Core for full plugin swappability — see Future Milestones in the spec.)</summary>
public interface IEyeCareService
{
    /// <summary>Feeds one raw gaze sample + paused state. Drives scheduler + fatigue monitor.</summary>
    void OnGazeSample(GazePoint sample, bool isPaused);

    /// <summary>Force-stops any in-flight break/banner (e.g. when Enabled toggled off).</summary>
    void Cancel();
}
