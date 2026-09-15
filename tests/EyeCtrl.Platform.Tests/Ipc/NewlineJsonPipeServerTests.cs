namespace EyeCtrl.Platform.Tests.Ipc;

using System.IO;
using System.IO.Pipes;
using System.Text;
using EyeCtrl.Platform.Ipc;

public class NewlineJsonPipeServerTests
{
    // Pipe names are machine-global; unique per test so a sibling dotnet-test run or a live
    // EyeCtrl process can never steal the connection (issue #76 pattern).
    private static string PipeName() => "eyectrl_nljs_test_" + Guid.NewGuid().ToString("N");

    private static NewlineJsonPipeServer<string> NewServer(
        string pipeName, List<string> received, EventWaitHandle? closed = null, int maxLineLength = 32) =>
        new(() => new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous),
            createState: _ => "",
            handleLine: (line, _) => { received.Add(line); return true; },
            onClosed: _ => closed?.Set(),
            maxLineLength: maxLineLength);

    // A line over the cap must drop only that connection: the server keeps accepting, and the
    // next client's good line is served. Without the bound, a client streaming bytes with no
    // newline grows the receiving process's memory without bound.
    [Fact]
    public async Task OverlongLine_DropsConnection_ServerServesNextClient()
    {
        var pipeName = PipeName();
        var received = new List<string>();
        using var badConnClosed = new AutoResetEvent(false);
        using var server = NewServer(pipeName, received, closed: badConnClosed);
        server.Start();

        await using (var bad = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await bad.ConnectAsync(2000);
            await using var w = new StreamWriter(bad, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await w.WriteAsync(new string('x', 40)); // > cap of 32, no newline
        }

        Check.That(badConnClosed.WaitOne(2000)).IsTrue();

        await using var good = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await good.ConnectAsync(2000);
        await using var wg = new StreamWriter(good, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await wg.WriteLineAsync("ok");

        for (var i = 0; i < 20 && received.Count == 0; i++)
            await Task.Delay(50);

        Check.That(received).HasSize(1);
        Check.That(received[0]).IsEqualTo("ok");
    }

    // Several lines arriving in one read chunk must all be delivered, in order — the bounded
    // reader carries the remainder of a chunk after each returned line instead of discarding it.
    [Fact]
    public async Task BatchedLinesInOneChunk_AllDeliveredInOrder()
    {
        var pipeName = PipeName();
        var received = new List<string>();
        using var server = NewServer(pipeName, received, maxLineLength: 8);
        server.Start();

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await using var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await w.WriteAsync("a\r\nb\n"); // \r\n and bare \n terminators, both in one write

        for (var i = 0; i < 20 && received.Count < 2; i++)
            await Task.Delay(50);

        Check.That(received).HasSize(2);
        Check.That(received[0]).IsEqualTo("a");
        Check.That(received[1]).IsEqualTo("b");
    }

    // A line of exactly the cap (newline not counted) is legitimate and must be delivered.
    [Fact]
    public async Task LineAtCap_IsDelivered()
    {
        var pipeName = PipeName();
        var received = new List<string>();
        using var server = NewServer(pipeName, received, maxLineLength: 8);
        server.Start();

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await using var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await w.WriteLineAsync("12345678");

        for (var i = 0; i < 20 && received.Count == 0; i++)
            await Task.Delay(50);

        Check.That(received).HasSize(1);
        Check.That(received[0]).IsEqualTo("12345678");
    }

    // Terminator semantics DELIBERATELY diverge from StreamReader.ReadLineAsync in one case: a
    // lone '\r' is CONTENT here (ReadLine treats it as a terminator), and the first CR of a
    // "\r\n" pair is never counted against the cap. Raw CRs are illegal inside JSON strings, so
    // no shipped message is affected; the choice is what lets a "\r\r\r…" stream keep growing
    // the line (below) instead of being swallowed forever.
    [Fact]
    public async Task LoneCarriageReturn_IsContent_NotATerminator()
    {
        var pipeName = PipeName();
        var received = new List<string>();
        using var server = NewServer(pipeName, received, maxLineLength: 8);
        server.Start();

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await using var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await w.WriteAsync("a\r\rb\n");

        for (var i = 0; i < 20 && received.Count == 0; i++)
            await Task.Delay(50);

        Check.That(received).HasSize(1);
        Check.That(received[0]).IsEqualTo("a\r\rb"); // both lone CRs are content; '\n' ends the line
    }

    // An endless "\r\r\r…" stream used to be swallowed one CR at a time without growing the
    // line: the cap never tripped and the connection was held open indefinitely. Consecutive
    // CRs now append, so the stream trips the cap and only that connection is dropped.
    [Fact]
    public async Task EndlessCarriageReturnStream_TripsTheCap_AndDropsTheConnection()
    {
        var pipeName = PipeName();
        var received = new List<string>();
        using var closed = new AutoResetEvent(false);
        using var server = NewServer(pipeName, received, closed: closed, maxLineLength: 8);
        server.Start();

        await using var bad = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await bad.ConnectAsync(2000);
        await using var w = new StreamWriter(bad, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await w.WriteAsync(new string('\r', 40));

        Check.That(closed.WaitOne(2000)).IsTrue();
        Check.That(received).IsEmpty();
    }
}
