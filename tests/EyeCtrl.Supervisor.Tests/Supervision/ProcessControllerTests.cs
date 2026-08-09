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
    public void Launch_SetsStartTimeAndExePath()
    {
        var controller = CreateController(out var handle);
        handle.StartTime = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        handle.ExePath = @"C:\App\EyeCtrl.App.exe";

        controller.Launch("test.exe", paused: false);

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
    public void ForceKill_RaisesEvent_TriggersExitedCallback()
    {
        var controller = CreateController(out var handle);
        controller.Launch("test.exe", paused: false);
        var exited = false;
        handle.Exited += () => exited = true;

        handle.RaiseExited();

        Check.That(exited).IsTrue();
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
        public void Dispose() { }

        public void RaiseExited() => Exited?.Invoke();
    }
}
