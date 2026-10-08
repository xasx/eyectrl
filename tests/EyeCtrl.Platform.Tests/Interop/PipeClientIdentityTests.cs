namespace EyeCtrl.Platform.Tests.Interop;

using System.IO.Pipes;
using EyeCtrl.Platform.Interop;

public class PipeClientIdentityTests
{
    // The mirror of PipeServerIdentityTests: the kernel, not the peer, says which process is
    // connected. With an in-process client the reported identity must be this test host itself.
    [Fact]
    public async Task ClientIdentity_ReportsTheConnectedProcess()
    {
        var pipeName = "eyectrl_clientid_test_" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepting = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000, TestContext.Current.CancellationToken);
        await accepting;

        Check.That(PipeClientIdentity.TryGetClientProcessId(server, out var pid)).IsTrue();
        Check.That(pid).IsEqualTo((uint)Environment.ProcessId);

        Check.That(PipeClientIdentity.TryGetClientExePath(server, out var exe)).IsTrue();
        Check.That(string.Equals(exe, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)).IsTrue();

        Check.That(PipeClientIdentity.ClientIsSameSession(server)).IsTrue();
    }

    // Fails closed: with no client connected there is nobody to identify, and nothing throws.
    [Fact]
    public void ClientIdentity_WithNoClient_FailsClosed()
    {
        var pipeName = "eyectrl_clientid_test_" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Check.That(PipeClientIdentity.TryGetClientProcessId(server, out _)).IsFalse();
        Check.That(PipeClientIdentity.TryGetClientExePath(server, out var exe)).IsFalse();
        Check.That(exe).IsNull();
        Check.That(PipeClientIdentity.ClientIsSameSession(server)).IsFalse();
    }
}
