namespace EyeCtrl.Supervisor.Supervision;

/// <summary>
/// Abstracts the subset of <see cref="System.Diagnostics.Process"/> that
/// <see cref="ProcessController"/> uses, so the kill-initiated state machine
/// is unit-testable without a real OS process.
/// </summary>
public interface IProcessHandle
{
    int Id { get; }
    bool HasExited { get; }
    string? ExePath { get; }
    DateTime? StartTime { get; }
    event Action Exited;
    void Kill(bool entireProcessTree);
    void Dispose();
}
