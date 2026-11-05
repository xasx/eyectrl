namespace EyeCtrl.Setup;

using System.Threading;

/// <summary>
/// One Setup at a time, across processes: a session-local named mutex held for the whole life of
/// every public mutating verb.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a global session and not a mutex per verb (C9 S6e fixup-05):</b> the sitting's double
/// launch produced two Q8 windows whose dwell target ids collided (<c>wpf_1</c>..<c>wpf_3</c>):
/// the App's external-dwell pipe gives a target id to the first connection that registers it and
/// rejects the second. Two verbs must never overlap either (an update and an uninstall both rename
/// Program Files\EyeCtrl), so the guard covers update, rollback, uninstall and repair with one
/// name. <c>status</c>, <c>help</c> and the <c>--internal</c> phases are read-only or are children
/// of an already-guarded public verb; the internal phases must never take the guard, or the run
/// copy an update hands off to could never become the session.
/// </para>
/// <para>
/// <b>Why a run copy waits:</b> the medium parent in <c>Program Files\EyeCtrl</c> starts the run
/// copy and only then returns from <see cref="SetupComposition.Run"/>, so the child process exists
/// a few milliseconds before the parent releases the mutex. The child therefore waits bounded
/// (<see cref="HandoffTakeoverWait"/>); every other start waits zero and refuses immediately when
/// the session is taken. The App's own hand-off guard (SetupGateways) closes the same window from
/// the other side.
/// </para>
/// <para>
/// <b>Not a security boundary:</b> another process of the same user can hold the name and make
/// Setup refuse. That is a local denial of a maintenance action, never a privilege or a data
/// change, and the alternative (a per-install secret) would need a store the App cannot read.
/// </para>
/// </remarks>
public static class SetupSessionGuard
{
    /// <summary>
    /// The session-local name every public mutating verb holds. Shared with the App's pre-hand-off
    /// probe through this constant, so the two sides cannot drift.
    /// </summary>
    public const string SessionMutexName = @"Local\EyeCtrl.Setup.Session";

    /// <summary>
    /// How long a run copy (the hand-off child) waits for its launcher to release the session.
    /// The launcher releases within milliseconds after starting the child; the bound only keeps a
    /// broken parent from hanging the child forever.
    /// </summary>
    public static readonly TimeSpan HandoffTakeoverWait = TimeSpan.FromSeconds(15);

    /// <summary>True for the four verbs that change the installation or its data.</summary>
    public static bool Guards(SetupJob job) => job is
        SetupJob.Update or SetupJob.Rollback or SetupJob.Uninstall or SetupJob.Repair;

    /// <summary>
    /// The wait a process may spend acquiring the session: only a process running from a run-copy
    /// folder replaces a launcher that still holds it; everything else must answer immediately.
    /// </summary>
    public static TimeSpan AcquisitionWait(
        ParsedSetupCommand command, SetupPaths paths, string? processPath)
    {
        if (!Guards(command.Job) || processPath is null)
            return TimeSpan.Zero;

        return SetupPaths.IsUnder(paths.RunRoot, processPath)
            ? HandoffTakeoverWait
            : TimeSpan.Zero;
    }

    /// <summary>
    /// Tries to become the one Setup session. Returns null when another process holds it, so the
    /// caller can exit before any log record, elevation, window or file write.
    /// </summary>
    /// <param name="wait">How long to wait for the current holder; <see cref="TimeSpan.Zero"/> for
    /// every start but a run-copy takeover.</param>
    /// <param name="name">The mutex name; production uses <see cref="SessionMutexName"/>, tests a
    /// GUID-suffixed name because mutex names are machine-global.</param>
    public static IDisposable? TryEnter(TimeSpan wait, string? name = null)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, name ?? SessionMutexName);
        }
        catch (Exception)
        {
            // An unopenable name (ACL, invalid name, platform) is not permission: fail closed.
            return null;
        }

        try
        {
            if (mutex.WaitOne(wait))
                return new Session(mutex);

            mutex.Dispose();
            return null;
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died while holding it. WaitOne has handed ownership to this
            // thread, which is exactly the takeover we want.
            return new Session(mutex);
        }
        catch (Exception)
        {
            mutex.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Whether a Setup session is held right now. The App's pre-hand-off probe; never creates the
    /// mutex and never throws.
    /// </summary>
    public static bool IsSessionLive(string? name = null)
    {
        try
        {
            if (!Mutex.TryOpenExisting(name ?? SessionMutexName, out var existing))
                return false;

            existing.Dispose();
            return true;
        }
        catch (Exception)
        {
            // Unable to tell (e.g. the name exists with an ACL this process cannot open) means the
            // caller must behave as if a session is live: fail closed, launch nothing.
            return true;
        }
    }

    /// <summary>The owned mutex, released on the acquiring thread (the process's main thread).</summary>
    private sealed class Session : IDisposable
    {
        private Mutex? _mutex;

        public Session(Mutex mutex) => _mutex = mutex;

        public void Dispose()
        {
            var mutex = Interlocked.Exchange(ref _mutex, null);
            if (mutex is null)
                return;

            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned (abandoned-takeover edge or a double dispose): nothing to release.
            }
            finally
            {
                mutex.Dispose();
            }
        }
    }
}
