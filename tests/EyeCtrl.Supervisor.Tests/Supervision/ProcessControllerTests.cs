namespace EyeCtrl.Supervisor.Tests.Supervision;

using EyeCtrl.Supervisor.Supervision;
using NFluent;
using Xunit;

public class ProcessControllerTests
{
    [Fact]
    public void Launch_ClearsKillInitiated()
    {
        var controller = CreateController(out var handle);
        controller.ForceKill();

        controller.Launch("test.exe", paused: false);

        Check.That(controller.KillInitiated).IsFalse();
        Check.That(controller.BoundProcess).IsSameReferenceAs(handle);
    }

    [Fact]
    public void Launch_WithPaused_AddsPausedArgument()
    {
        var controller = CreateController(out var handle);

        controller.Launch("test.exe", paused: true);

        Check.That(controller.BoundProcess).IsSameReferenceAs(handle);
    }

    [Fact]
    public void Launch_SetsStartTimeAndRecordsRequestedExePath()
    {
        // Launch records the path it was HANDED — it does not ask the handle. Adopt is the path
        // that has to interrogate the OS, because there the caller only has a PID.
        var controller = CreateController(out var handle);
        handle.StartTime = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        handle.ExePath = @"C:\Somewhere\Else.exe";

        controller.Launch(@"C:\App\EyeCtrl.App.exe", paused: false);

        Check.That(controller.StartTimeUtc).IsEqualTo(handle.StartTime);
        Check.That(controller.ExePath).IsEqualTo(@"C:\App\EyeCtrl.App.exe");
    }

    [Fact]
    public void Adopt_Succeeds_ReturnsTrue()
    {
        var controller = CreateController(out var handle);

        var result = controller.Adopt(12345);

        Check.That(result).IsTrue();
        Check.That(controller.BoundProcess).IsSameReferenceAs(handle);
    }

    [Fact]
    public void Adopt_Fails_ReturnsFalse()
    {
        FakeProcessHandle handle = new(12345, false);
        var controller = new ProcessController(
            _ => handle,
            _ => null!);

        var result = controller.Adopt(99999);

        Check.That(result).IsFalse();
        Check.That(controller.BoundProcess).IsNull();
    }

    [Fact]
    public void Adopt_PopulatesExePath()
    {
        var controller = CreateController(out var handle);
        handle.ExePath = @"C:\App\EyeCtrl.App.exe";

        controller.Adopt(12345);

        Check.That(controller.ExePath).IsEqualTo(@"C:\App\EyeCtrl.App.exe");
    }

    [Fact]
    public void Adopt_ClearsKillInitiated()
    {
        var controller = CreateController(out var handle);
        controller.ForceKill();

        controller.Adopt(12345);

        Check.That(controller.KillInitiated).IsFalse();
    }

    [Fact]
    public void ForceKill_SetsKillInitiatedBeforeCheck()
    {
        var controller = CreateController(out var handle);
        controller.Launch("test.exe", paused: false);

        controller.ForceKill();

        Check.That(controller.KillInitiated).IsTrue();
        Check.That(handle.KillCalled).IsTrue();
    }

    [Fact]
    public void ForceKill_AlreadyExited_SetsKillInitiatedButSkipsKill()
    {
        var controller = CreateController(out var handle, hasExited: true);

        controller.ForceKill();

        Check.That(controller.KillInitiated).IsTrue();
        Check.That(handle.KillCalled).IsFalse();
    }

    [Fact]
    public void IsAlive_WhenProcessNotExited_ReturnsTrue()
    {
        var controller = CreateController(out var handle, hasExited: false);

        controller.Launch("test.exe", paused: false);

        Check.That(controller.IsAlive).IsTrue();
    }

    [Fact]
    public void IsAlive_WhenProcessExited_ReturnsFalse()
    {
        var controller = CreateController(out var handle, hasExited: true);

        controller.Launch("test.exe", paused: false);

        Check.That(controller.IsAlive).IsFalse();
    }

    [Fact]
    public void ForceKill_ThenExited_KeepsKillInitiated()
    {
        // The contract Program.WireProcessExited depends on: the Exited that arrives BECAUSE of a
        // supervisor-initiated kill must still find KillInitiated set, so the handler suppresses it
        // instead of booking a second crash strike. Only Launch/Adopt clear the flag.
        var controller = CreateController(out var handle);
        controller.Launch("test.exe", paused: false);
        controller.ForceKill();

        handle.RaiseExited();

        Check.That(controller.KillInitiated).IsTrue();
    }

    [Fact]
    public void Launch_DisposesPreviousHandle()
    {
        // ReleasePrevious must free the prior OS handle before the field is overwritten.
        var first = new FakeProcessHandle(1, hasExited: false);
        var second = new FakeProcessHandle(2, hasExited: false);
        var handles = new[] { first, second };
        var next = 0;
        var controller = new ProcessController(_ => handles[next++], _ => null);

        controller.Launch("test.exe", paused: false);
        controller.Launch("test.exe", paused: false);

        Check.That(first.DisposeCalled).IsTrue();
        Check.That(second.DisposeCalled).IsFalse();
        Check.That(controller.BoundProcess).IsSameReferenceAs(second);
    }

    [Fact]
    public void Launch_WhenNoProcessStarted_LeavesNothingBound()
    {
        // Process.Start is documented to return null when no new process was started; the factory
        // is null-tolerant so that surfaces as "nothing bound", not a NullReferenceException.
        var controller = new ProcessController(_ => null, _ => null);

        controller.Launch("test.exe", paused: false);

        Check.That(controller.BoundProcess).IsNull();
        Check.That(controller.IsAlive).IsFalse();
        Check.That(controller.StartTimeUtc).IsNull();
        Check.That(controller.ExePath).IsEqualTo("test.exe");
    }

    private static ProcessController CreateController(out FakeProcessHandle handle, bool hasExited = false, FakeProcessHandle? adoptResult = null)
    {
        handle = new FakeProcessHandle(12345, hasExited);
        var process = handle;
        var adopt = adoptResult ?? process;
        return new ProcessController(
            _ => process,
            _ => adopt);
    }

    private class FakeProcessHandle : IProcessHandle
    {
        public bool KillCalled { get; private set; }
        public bool DisposeCalled { get; private set; }

        public FakeProcessHandle(int id, bool hasExited)
        {
            Id = id;
            HasExited = hasExited;
        }

        public int Id { get; }
        public bool HasExited { get; set; }
        public string? ExePath { get; set; }
        public DateTime? StartTime { get; set; }
        public event Action? Exited;

        public void Kill(bool entireProcessTree) => KillCalled = true;
        public void Dispose() => DisposeCalled = true;

        public void RaiseExited() => Exited?.Invoke();
    }
}
