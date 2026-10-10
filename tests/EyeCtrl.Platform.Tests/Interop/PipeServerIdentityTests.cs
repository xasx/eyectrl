namespace EyeCtrl.Platform.Tests.Interop;

using System.IO.Pipes;
using EyeCtrl.Platform.Interop;
using static EyeCtrl.Platform.Tests.TestTimeouts;

public class PipeServerIdentityTests
{
    // The identity seam name-squatting defences are built on: the kernel, not the peer, says
    // which process serves the connected pipe instance. An in-process test server is the
    // simplest ground truth: the reported identity must be this test host itself.
    [Fact]
    public async Task ServerIdentity_ReportsTheServingProcess()
    {
        var pipeName = "eyectrl_peerid_test_" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _ = Task.Run(async () =>
        {
            try { await server.WaitForConnectionAsync(); } catch { /* client-side dispose ends this */ }
        });

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(PositiveWaitMs);

        Check.That(PipeServerIdentity.TryGetServerProcessId(client, out var pid)).IsTrue();
        Check.That(pid).IsEqualTo((uint)Environment.ProcessId);

        Check.That(PipeServerIdentity.TryGetServerExePath(client, out var exe)).IsTrue();
        Check.That(string.Equals(exe, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)).IsTrue();

        Check.That(PipeServerIdentity.ServerIsSameSession(client)).IsTrue();
    }
}
