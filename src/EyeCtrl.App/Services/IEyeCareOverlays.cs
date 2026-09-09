namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

// Seams for the two eye-care overlays, following IEdgeButtonBar's template.
//
// EyeCareHooks took the concrete EyeCareBannerWindow and EyeCareExerciseWindow, so the only test
// covering its gaze routing had to construct both real windows — which needs an STA thread, the
// process-wide Application singleton, and the app resource dictionary their XAML resolves
// StaticResource tokens against. That is why the test carries [Trait("Category", "UI")], and CI
// runs `--filter "Category!=UI"`: the routing contract has never executed in CI.
//
// The surfaces are small and already intent-level — three and four members — so unlike the
// keyboard (twelve setup members) there is nothing here that makes an interface a facade.

/// <summary>The eye-care break banner.</summary>
public interface IEyeCareBanner
{
    /// <summary>
    /// Shows the banner and resolves true when the user takes the break, false when they snooze
    /// or it auto-snoozes.
    /// </summary>
    Task<bool> ShowAsync(string message, PhysicalPoint screenCenter, int autoSnoozeSeconds);

    /// <summary>Feeds a gaze sample to the banner's own dwell detector.</summary>
    void OnGaze(PhysicalPoint gaze, long timestampUs);

    void Cancel();
}

/// <summary>The guided eye-care exercise overlay.</summary>
public interface IEyeCareExercise
{
    Task RunAsync(string exerciseName, long durationMicroseconds,
        Func<long, PhysicalPoint> getTargetAt, Action<PhysicalPoint, long> recordGaze);

    /// <summary>Feeds a gaze sample to the running exercise.</summary>
    void OnGaze(PhysicalPoint gaze, long timestampUs);

    Task ShowSummaryAsync(EyeCareExerciseOutcome outcome);

    void Cancel();
}
