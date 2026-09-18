namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Drives a dwell surface at a realistic 30 ms cadence. The <c>DwellDetector</c> gap clamp
/// restarts a dwell when a single step reaches its bound (150 ms), so the old two-samples-far-
/// apart pattern no longer models a hold. <see cref="Hold(IMessenger, PhysicalPoint, long, long)"/>
/// sends from <c>fromUs</c> through <c>toUs</c> inclusive, so the exact end timestamp these tests
/// asserted against is preserved — the migration adds samples, it does not move endpoints.
/// </summary>
internal static class DwellDrive
{
    private const long StepUs = 30_000;

    /// <summary>Sends real samples at <paramref name="at"/> from <paramref name="fromUs"/> through <paramref name="toUs"/>.</summary>
    public static void Hold(IMessenger messenger, PhysicalPoint at, long fromUs, long toUs) =>
        Hold(fromUs, toUs, t => messenger.Send(new GazeCursorMovedMessage(at, t)));

    /// <summary>Sends timestamps from <paramref name="fromUs"/> through <paramref name="toUs"/> through the given sender.</summary>
    public static void Hold(long fromUs, long toUs, Action<long> send)
    {
        for (var t = fromUs; t < toUs; t += StepUs)
            send(t);
        send(toUs);
    }
}
