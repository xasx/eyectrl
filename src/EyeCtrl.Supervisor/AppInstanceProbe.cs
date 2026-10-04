namespace EyeCtrl.Supervisor;

using System.Threading;
using EyeCtrl.Core.Services;
using Serilog;

/// <summary>
/// Ticket 06-A: probes whether an EyeCtrl.App instance is running, via the app's single-instance
/// mutex (see <see cref="SingleInstanceGuard"/>). The mutex object exists exactly while a live app
/// holds it, so a successful open means "one is running"; the handle is disposed immediately and
/// the mutex is never acquired.
/// </summary>
internal static class AppInstanceProbe
{
    public static bool IsRunning(string? mutexName = null)
    {
        try
        {
            if (Mutex.TryOpenExisting(mutexName ?? SingleInstanceGuard.DefaultMutexName, out var existing))
            {
                existing.Dispose();
                return true;
            }
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Documented TryOpenExisting throw: the named mutex EXISTS but this process may not
            // open it. Existence is exactly what the probe is asking about.
            return true;
        }
        catch (Exception ex)
        {
            // Unexpected probe failure. Treat as "not running" and keep the logon launch: a false
            // positive risks the F1 duplicate cascade, but a false negative would leave a user
            // whose app never starts with no supervisor recovery at all.
            Log.Warning(ex, "App-instance mutex probe failed; assuming no EyeCtrl is running.");
            return false;
        }
    }
}
