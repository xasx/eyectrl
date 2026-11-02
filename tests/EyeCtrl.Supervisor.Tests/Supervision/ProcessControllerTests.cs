namespace EyeCtrl.Supervisor.Tests.Supervision;

using System.Diagnostics;
using System.IO;
using EyeCtrl.Supervisor.Supervision;
using NFluent;
using Xunit;

public class ProcessControllerTests
{
    [Fact]
    public void Launch_ClearsKillInitiated()
    {
        ProcessStartInfo? capturedPsi = null;
        var handle = new FakeProcessHandle(12345, hasExited: false);
        var controller = new ProcessController(psi => { capturedPsi = psi; return handle; }, _ => handle);
        controller.ForceKill();

        controller.Launch("test.exe", paused: false);

        Check.That(controller.KillInitiated).IsFalse();
        Check.That(controller.BoundProcess).IsSameReferenceAs(handle);
        Check.That(capturedPsi).IsNotNull();
        Check.That(capturedPsi!.ArgumentList).IsEmpty();
    }

    [Fact]
    public void Launch_WithPaused_AddsPausedArgument()
    {
        ProcessStartInfo? capturedPsi = null;
        var handle = new FakeProcessHandle(12345, hasExited: false);
        var controller = new ProcessController(psi => { capturedPsi = psi; return handle; }, _ => handle);

        controller.Launch("test.exe", paused: true);

        Check.That(controller.BoundProcess).IsSameReferenceAs(handle);
        Check.That(capturedPsi).IsNotNull();
        Check.That(capturedPsi!.FileName).IsEqualTo("test.exe");
        Check.That(capturedPsi.ArgumentList).ContainsExactly("--paused");
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

    [Fact]
    public void BoundProcessExited_RaisedWhenTheBoundProcessExits()
    {
        var controller = CreateController(out var handle);
        IProcessHandle? seen = null;
        controller.BoundProcessExited += h => seen = h;
        controller.Launch("test.exe", paused: false);

        handle.RaiseExited();

        Check.That(seen).IsSameReferenceAs(handle);
    }

    [Fact]
    public void Unbind_DetachesTheExitedHandler_SoALaterExitIsNotReported()
    {
        // Ticket 06-B: after a stand-down, the released process's exit must book nothing. The
        // handler must be detached, not merely the handle dropped.
        var controller = CreateController(out var handle);
        var raised = 0;
        controller.BoundProcessExited += _ => raised++;
        controller.Launch("test.exe", paused: false);
        handle.RaiseExited();
        Check.That(raised).IsEqualTo(1);

        controller.Unbind();
        handle.RaiseExited();

        Check.That(raised).IsEqualTo(1);                    // detached: the unbound exit is silent
        Check.That(controller.BoundProcess).IsNull();
    }

    [Fact]
    public void Unbind_DoesNotKill_AndClearsKillInitiated()
    {
        var controller = CreateController(out var handle);
        controller.Launch("test.exe", paused: false);
        controller.ForceKill();

        controller.Unbind();

        Check.That(handle.KillCalled).IsTrue();             // only the explicit ForceKill killed
        Check.That(controller.KillInitiated).IsFalse();
        Check.That(controller.BoundProcess).IsNull();
        Check.That(controller.IsAlive).IsFalse();
    }

    [Fact]
    public void Unbind_IsIdempotent_AndRebindingWorks()
    {
        var controller = CreateController(out var handle);
        controller.Launch("test.exe", paused: false);

        controller.Unbind();
        controller.Unbind();
        controller.Launch("test.exe", paused: false);

        var raised = 0;
        controller.BoundProcessExited += _ => raised++;
        handle.RaiseExited();
        Check.That(raised).IsEqualTo(1);                    // the new binding is wired again
    }

    [Fact]
    public void TheProductionConstructor_WiresTheHeldButtonRelease()
    {
        // F1: this wiring is what makes the Supervisor release a drag button after a force-kill,
        // and nothing else in the suite observes it — every other test reaches the controller
        // through the 2/3-argument constructors, so a refactor that threaded `null` through (or a
        // merge that restored the old 2-argument call) would leave all 472 tests green while the
        // Supervisor hung-killed a mid-drag App and sent no button-up.
        //
        // Constructing is IO-free and input-free: FileHeldButtonMarker's constructor only stores a
        // path and Win32InputSimulator's only stores a mapper. NEVER call ForceKill on this
        // instance — it holds the production marker and the real SendInput sink.
        Check.That(new ProcessController().HasHeldButtonRelease).IsTrue();
    }

    [Fact]
    public void TheTestConstructors_DoNotWireTheHeldButtonRelease()
    {
        // The other half of the safety rule: existing tests call ForceKill, so the constructors
        // they use must never carry the production marker + real SendInput sink.
        Check.That(CreateController(out _).HasHeldButtonRelease).IsFalse();
    }

    // ── C3: a force-kill must release a drag button the App left held ─────────────────────
    //
    // TerminateProcess runs none of the App's release paths — not even its crash handlers. So the
    // marker the App writes on press is the ONLY way the Supervisor can know one button-up is
    // owed, and these tests pin that it is sent exactly once, in place (not MouseUp, whose absolute
    // move would jump the pointer), and never when no marker exists.

    [Fact]
    public void ForceKill_WithAHeldButtonMarker_SendsOneInPlaceReleaseAndClearsIt()
    {
        var marker = new FakeMarker { IsSet = true };
        var input = new RecordingInput();
        var controller = CreateControllerWithRelease(out var handle, marker, input);
        controller.Launch("test.exe", paused: false);

        controller.ForceKill();

        Check.That(handle.KillCalled).IsTrue();
        Check.That(input.Calls).ContainsExactly("ReleaseLeftButton");
        Check.That(marker.IsSet).IsFalse();
    }

    [Fact]
    public void ForceKill_WithoutAMarker_SendsNoInputAtAll()
    {
        // The owner may be holding a physical mouse; an unconditional button-up here would
        // release a button EyeCtrl never pressed.
        var marker = new FakeMarker { IsSet = false };
        var input = new RecordingInput();
        var controller = CreateControllerWithRelease(out _, marker, input);

        controller.ForceKill();

        Check.That(input.Calls).IsEmpty();
        Check.That(marker.ClearCount).IsEqualTo(0);
    }

    [Fact]
    public void ForceKill_AfterTheProcessAlreadyExited_StillReleases()
    {
        // A native crash stranded the button exactly like a kill: recovery must release it even
        // though the kill itself is skipped (HasExited). The relaunch follows immediately.
        var marker = new FakeMarker { IsSet = true };
        var input = new RecordingInput();
        var controller = CreateControllerWithRelease(out var handle, marker, input, hasExited: true);
        controller.Launch("test.exe", paused: false);

        controller.ForceKill();

        Check.That(handle.KillCalled).IsFalse();
        Check.That(input.Calls).ContainsExactly("ReleaseLeftButton");
    }

    [Fact]
    public void ForceKill_WhenTheReleaseThrows_DoesNotEscapeIntoTheDispatcher()
    {
        // ForceKill runs on the supervisor's dispatcher, where an escaping exception kills the
        // WATCHDOG exactly when it is recovering the App. A failed release keeps the marker, and
        // the App's startup backstop retries it moments later on the relaunch.
        var marker = new FakeMarker { IsSet = true };
        var input = new RecordingInput { ThrowOnRelease = true };
        var controller = CreateControllerWithRelease(out _, marker, input);

        Check.ThatCode(controller.ForceKill).DoesNotThrow();

        Check.That(marker.IsSet).IsTrue();
    }

    [Fact]
    public void ForceKill_WhenTheMarkerClearFails_SaysTheButtonUpWasSent()
    {
        // Fixup-01 F5: the LEFTUP went out and only the clear failed (AV/indexer holding the file).
        // The line must not read as a failed release — the button IS released; one later inert
        // button-up is the entire cost.
        var marker = new FakeMarker { IsSet = true, ClearFailure = new IOException("the marker file is locked") };
        var input = new RecordingInput();
        var controller = CreateControllerWithRelease(out _, marker, input);
        var sink = new CapturingSink();
        var previous = Serilog.Log.Logger;
        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        try
        {
            Check.ThatCode(controller.ForceKill).DoesNotThrow();
        }
        finally
        {
            Serilog.Log.Logger = previous;
        }

        Check.That(input.Calls).ContainsExactly("ReleaseLeftButton");
        Check.WithCustomMessage($"the log did not name the failed clear: {string.Join(" | ", sink.Messages)}")
             .That(sink.Messages.Any(m => m.Contains("clearing the marker failed", StringComparison.Ordinal)))
             .IsTrue();
        Check.WithCustomMessage($"the log blamed the release, which succeeded: {string.Join(" | ", sink.Messages)}")
             .That(sink.Messages.Any(m => m.Contains("release failed", StringComparison.Ordinal)))
             .IsFalse();
    }

    /// <summary>Captures the lines written through the process-global Serilog logger.</summary>
    private sealed class CapturingSink : Serilog.Core.ILogEventSink
    {
        public List<string> Messages { get; } = new();

        public void Emit(Serilog.Events.LogEvent logEvent) => Messages.Add(logEvent.RenderMessage());
    }

    private static ProcessController CreateControllerWithRelease(
        out FakeProcessHandle handle, FakeMarker marker, RecordingInput input, bool hasExited = false)
    {
        handle = new FakeProcessHandle(12345, hasExited);
        var process = handle;
        return new ProcessController(
            _ => process,
            _ => process,
            new EyeCtrl.Core.Services.HeldButtonRelease(marker, input));
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

    /// <summary>C3's cross-process marker, recorded instead of written (never the real app-data file).</summary>
    private sealed class FakeMarker : EyeCtrl.Core.Services.IHeldButtonMarker
    {
        public bool IsSet { get; set; }

        public Exception? ClearFailure { get; init; }

        public int ClearCount { get; private set; }

        public void Set() => IsSet = true;

        public void Clear()
        {
            ClearCount++;
            if (ClearFailure is not null) throw ClearFailure;
            IsSet = false;
        }
    }

    /// <summary>Records the in-place release; no real <c>SendInput</c> anywhere.</summary>
    private sealed class RecordingInput : EyeCtrl.Core.Interfaces.IInputSimulator
    {
        public List<string> Calls { get; } = new();

        public bool ThrowOnRelease { get; init; }

        public void ReleaseLeftButton()
        {
            Calls.Add("ReleaseLeftButton");
            if (ThrowOnRelease)
                throw new InvalidOperationException("SendInput refused the release");
        }

        public void MoveCursor(EyeCtrl.Core.Models.PhysicalPoint point) => Calls.Add("MoveCursor");
        public void LeftClick(EyeCtrl.Core.Models.PhysicalPoint point) => Calls.Add("LeftClick");
        public void RightClick(EyeCtrl.Core.Models.PhysicalPoint point) => Calls.Add("RightClick");
        public void DoubleClick(EyeCtrl.Core.Models.PhysicalPoint point) => Calls.Add("DoubleClick");
        public void MouseDown(EyeCtrl.Core.Models.PhysicalPoint point) => Calls.Add("MouseDown");
        public void MouseUp(EyeCtrl.Core.Models.PhysicalPoint point) => Calls.Add("MouseUp");
        public void Scroll(EyeCtrl.Core.Models.PhysicalPoint point, int deltaX, int deltaY) => Calls.Add("Scroll");
        public void SendKeyPress(ushort virtualKeyCode) { }
        public void SendKeyDown(ushort virtualKeyCode) { }
        public void SendKeyUp(ushort virtualKeyCode) { }
        public void SendText(string text) { }
        public void SendModifiedKeyPress(IReadOnlySet<EyeCtrl.Core.Keyboard.ModifierKey> modifiers, ushort virtualKeyCode) { }
    }
}
