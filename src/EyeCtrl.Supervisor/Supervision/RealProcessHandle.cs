namespace EyeCtrl.Supervisor.Supervision;

using System.Diagnostics;

/// <summary>
/// Default <see cref="IProcessHandle"/> implementation wrapping a real <see cref="Process"/>.
/// </summary>
internal sealed class RealProcessHandle : IProcessHandle
{
    private readonly Process _handle;

    private readonly Action _raiseExited;

    public RealProcessHandle(Process process)
    {
        _handle = process;
        _raiseExited = () => Exited?.Invoke();
        _handle.EnableRaisingEvents = true;
        _handle.Exited += (_, _) => _raiseExited();
    }

    public int Id => _handle.Id;
    public bool HasExited => _handle.HasExited;
    public event Action? Exited;
    public void Kill(bool entireProcessTree) => _handle.Kill(entireProcessTree);
    public void Dispose()
    {
        _handle.Exited -= (_, _) => _raiseExited();
        _handle.Dispose();
    }
}
