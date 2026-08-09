namespace EyeCtrl.Supervisor.Supervision;

using System.Diagnostics;

/// <summary>
/// Default <see cref="IProcessHandle"/> implementation wrapping a real <see cref="Process"/>.
/// </summary>
internal sealed class RealProcessHandle : IProcessHandle
{
    private readonly Process _handle;
    private readonly EventHandler _raiseExited;

    public RealProcessHandle(Process process)
    {
        _handle = process;
        _raiseExited = (_, _) => Exited?.Invoke();
        _handle.EnableRaisingEvents = true;
        _handle.Exited += _raiseExited;
    }

    public int Id => _handle.Id;
    public bool HasExited => _handle.HasExited;

    /// <summary>
    /// The bound process's image path, or null when it cannot be read. <see cref="Process.MainModule"/>
    /// throws on a 64/32-bit bitness mismatch or access denied, and once the process has exited — the
    /// guard here is what lets <see cref="ProcessController.Adopt"/> stay non-throwing, since Adopt is
    /// called from a Dispatcher callback where an escape would tear down the supervisor.
    /// </summary>
    public string? ExePath
    {
        get
        {
            try { return _handle.MainModule?.FileName; }
            catch { return null; }
        }
    }

    public DateTime? StartTime
    {
        get
        {
            try { return _handle.StartTime.ToUniversalTime(); }
            catch { return null; }
        }
    }
    public event Action? Exited;
    public void Kill(bool entireProcessTree) => _handle.Kill(entireProcessTree);
    public void Dispose()
    {
        _handle.Exited -= _raiseExited;
        _handle.Dispose();
    }
}
