namespace EyeCtrl.Setup.Tests.TestSupport;

using EyeCtrl.Core.Input;
using EyeCtrl.Setup.Abstractions;

/// <summary>A recording process seam. Starts add a live PID; stop/kill remove it and record order.</summary>
public sealed class FakeProcessControl : IProcessControl
{
    private int _nextPid = 5000;

    /// <summary>Creates the fake with an optional shared operation log.</summary>
    public FakeProcessControl(List<string>? log = null) => Log = log ?? [];

    /// <summary>The shared ordering log.</summary>
    public List<string> Log { get; }

    /// <summary>The current process table (images are used by the image check).</summary>
    public List<RunningProcess> Processes { get; } = [];

    /// <summary>PIDs considered alive.</summary>
    public HashSet<int> Alive { get; } = [];

    /// <summary>Overrides the token answer for one PID.</summary>
    public Func<int, ProcessTokenFacts>? TokenReader { get; set; }

    /// <summary>Overrides the alive answer (used by the 20 s check).</summary>
    public Func<int, bool>? AliveReader { get; set; }

    /// <summary>The result of the next Start calls; null means "started".</summary>
    public bool StartSucceeds { get; set; } = true;

    /// <summary>The result WaitForExit returns; true means the process is gone.</summary>
    public bool WaitForExitResult { get; set; } = true;

    /// <summary>Adds a running process without logging, for test setup.</summary>
    public int Add(string imagePath)
    {
        var pid = _nextPid++;
        Processes.Add(new RunningProcess(pid, imagePath));
        Alive.Add(pid);
        return pid;
    }

    /// <summary>Removes a process without logging, for test setup.</summary>
    public void Remove(int pid)
    {
        Processes.RemoveAll(p => p.Id == pid);
        Alive.Remove(pid);
    }

    /// <inheritdoc />
    public IReadOnlyList<RunningProcess> Snapshot() => Processes.ToList();

    /// <inheritdoc />
    public int? Start(string exePath)
    {
        Log.Add("start:" + exePath);
        if (!StartSucceeds)
            return null;

        var pid = _nextPid++;
        Processes.Add(new RunningProcess(pid, exePath));
        Alive.Add(pid);
        return pid;
    }

    /// <inheritdoc />
    public void Stop(int processId)
    {
        Log.Add("stop:" + processId);
        Remove(processId);
    }

    /// <inheritdoc />
    public void Kill(int processId)
    {
        Log.Add("kill:" + processId);
        Remove(processId);
    }

    /// <inheritdoc />
    public bool WaitForExit(int processId, TimeSpan timeout)
    {
        Log.Add("wait:" + processId);
        if (WaitForExitResult)
            Remove(processId);
        return WaitForExitResult;
    }

    /// <inheritdoc />
    public bool IsAlive(int processId) => AliveReader?.Invoke(processId) ?? Alive.Contains(processId);

    /// <inheritdoc />
    public ProcessTokenFacts ReadToken(int processId) =>
        TokenReader?.Invoke(processId)
        ?? new ProcessTokenFacts("EyeCtrl.App.exe", 0x3000, false, true, false);
}
