namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Streams gaze into an <see cref="ExposedDwellToolbarViewModel"/> at a realistic 50 ms cadence.
/// The <c>DwellDetector</c> gap clamp restarts a dwell on a single step of 150 ms or more, so the
/// two-samples-far-apart pattern these tests used no longer accumulates — and a test asserting
/// "progress reached 0.5" would fail for a reason unrelated to what it checks.
/// </summary>
internal static class ToolbarGazeDrive
{
    public static void Stream(ExposedDwellToolbarViewModel vm, double x, double y, long throughUs)
    {
        for (var t = 0L; t <= throughUs; t += 50_000)
            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(x, y), t));
    }
}
