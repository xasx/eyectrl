namespace EyeCtrl.Setup.Abstractions;

using EyeCtrl.Core.Input;

/// <summary>One process as the image check sees it: a PID and its full image path (null when the
/// OS refused or the process exited between enumeration and the query).</summary>
public readonly record struct RunningProcess(int Id, string? ImagePath);

/// <summary>
/// The process seam: enumerate images, start the App through ShellExecute, stop the Supervisor,
/// kill the App, and read a token. Production binds Win32; tests record calls.
/// </summary>
public interface IProcessControl
{
    /// <summary>Every process the caller may enumerate (the implementation skips what it cannot open).</summary>
    IReadOnlyList<RunningProcess> Snapshot();

    /// <summary>Starts an exe with ShellExecute (never CreateProcess) in an explicit working
    /// directory — always <see cref="SetupWorkingDirectory.Neutral"/>, never a folder the swap
    /// renames, because the child would otherwise inherit and hold it — and returns its PID, or null.</summary>
    int? Start(string exePath, string workingDirectory, IReadOnlyList<string>? arguments = null);

    /// <summary>Stops the Supervisor (a kill today: the Supervisor has no graceful stop IPC).</summary>
    void Stop(int processId);

    /// <summary>Kills the App (the coordinator/uninstall path), including its process tree.</summary>
    void Kill(int processId);

    /// <summary>Waits for a process to exit; true when it is gone by the deadline.</summary>
    bool WaitForExit(int processId, TimeSpan timeout);

    /// <summary>True while the process is alive.</summary>
    bool IsAlive(int processId);

    /// <summary>Reads a process's token facts (High/UIAccess are the pass rule's two bits).</summary>
    ProcessTokenFacts ReadToken(int processId);
}
