namespace EyeCtrl.Setup.Tests.TestSupport;

using EyeCtrl.Core.Input;
using EyeCtrl.Setup.Abstractions;

/// <summary>One recorded start: the exe, its argv and the explicit working directory.</summary>
public readonly record struct RecordedStart(
    string ExePath, IReadOnlyList<string> Arguments, string WorkingDirectory);

/// <summary>A recording process seam. Starts add a live PID; stop/kill remove it and record order.</summary>
public sealed class FakeProcessControl : IProcessControl
{
    private int _nextPid = 5000;

    /// <summary>Creates the fake with an optional shared operation log.</summary>
    public FakeProcessControl(List<string>? log = null) => Log = log ?? [];

    /// <summary>The shared ordering log.</summary>
    public List<string> Log { get; }

    /// <summary>Every start, with the working directory Setup asked for.</summary>
    public List<RecordedStart> Starts { get; } = [];

    /// <summary>The bounds every wait was given, for the refusal-restart tests.</summary>
    public List<TimeSpan> WaitTimeouts { get; } = [];

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

    /// <summary>Called with the exe path after a successful start (a test can model a child).</summary>
    public Action<string>? OnStart { get; set; }

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
    public int? Start(string exePath, string workingDirectory, IReadOnlyList<string>? arguments = null)
    {
        var argv = arguments?.ToList() ?? [];
        Starts.Add(new RecordedStart(exePath, argv, workingDirectory));
        Log.Add("start:" + exePath + (argv.Count > 0
            ? "|" + string.Join("|", argv)
            : string.Empty));
        if (!StartSucceeds)
            return null;

        var pid = _nextPid++;
        Processes.Add(new RunningProcess(pid, exePath));
        Alive.Add(pid);
        OnStart?.Invoke(exePath);
        return pid;
    }

    /// <inheritdoc />
    public void Stop(int processId)
    {
        Log.Add("stop:" + processId);
        Remove(processId);
        OnStop?.Invoke(processId);
    }

    /// <summary>Called with the process id after a kill (a test can model a Supervisor relaunch).</summary>
    public Action<int>? OnKill { get; set; }

    /// <inheritdoc />
    public void Kill(int processId)
    {
        Log.Add("kill:" + processId);
        Remove(processId);
        OnKill?.Invoke(processId);
    }

    /// <summary>Called with the process id after a supervisor stop.</summary>
    public Action<int>? OnStop { get; set; }

    /// <inheritdoc />
    public bool WaitForExit(int processId, TimeSpan timeout)
    {
        WaitTimeouts.Add(timeout);
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
