namespace EyeCtrl.Supervisor.Tests.Ipc;

using System.IO;
using System.IO.Pipes;
using System.Text;
using EyeCtrl.Core.Models;

/// <summary>
/// A stand-in for the App's request-channel server, speaking the Core wire records. Each mode is
/// one way a real App can behave towards the Supervisor. Serves on the name it is given — tests
/// pass <c>SettingRequestChannel.PipeName + "_test_" + Guid</c>, never the production name.
/// </summary>
internal sealed class FakeAppPipe : IDisposable
{
    public enum Behaviour
    {
        /// <summary>Answers every request with the configured status.</summary>
        Answer,

        /// <summary>A hung App: accepts, reads the request, never answers.</summary>
        Hang,

        /// <summary>An App that exits (or restarts) mid-request: reads the request, then closes.</summary>
        CloseAfterRequest,

        /// <summary>Answers with an id that does not match the request.</summary>
        WrongId,
    }

    private readonly string _pipeName;
    private readonly Behaviour _behaviour;
    private readonly SettingRequestStatus _status;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<SettingRequest> _received = new();
    private readonly List<NamedPipeServerStream> _pipes = new();
    private readonly SemaphoreSlim _requestArrived = new(0);

    public FakeAppPipe(string pipeName, Behaviour behaviour, SettingRequestStatus status = SettingRequestStatus.Saved)
    {
        _pipeName = pipeName;
        _behaviour = behaviour;
        _status = status;
        _ = AcceptLoopAsync();
    }

    public IReadOnlyList<SettingRequest> Received
    {
        get { lock (_received) return _received.ToList(); }
    }

    /// <summary>Waits until a request line has been read.</summary>
    public Task<bool> WaitForRequestAsync(TimeSpan timeout) => _requestArrived.WaitAsync(timeout);

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            lock (_pipes) _pipes.Add(pipe);
            try
            {
                await pipe.WaitForConnectionAsync(_cts.Token);
            }
            catch
            {
                pipe.Dispose();
                return;
            }
            _ = ServeAsync(pipe);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            var line = await reader.ReadLineAsync(_cts.Token);
            if (line is null) return;
            var request = SettingRequestChannel.ParseRequest(line)!;
            lock (_received) _received.Add(request);
            _requestArrived.Release();

            switch (_behaviour)
            {
                case Behaviour.Answer:
                    await WriteAsync(pipe, new SettingRequestAck(request.Id, _status));
                    break;
                case Behaviour.WrongId:
                    await WriteAsync(pipe, new SettingRequestAck("not-" + request.Id, _status));
                    break;
                case Behaviour.Hang:
                    await Task.Delay(Timeout.Infinite, _cts.Token);
                    break;
                case Behaviour.CloseAfterRequest:
                    pipe.Dispose();
                    return;
            }

            // Keep the connection open until the client closes it, as the real server does.
            await reader.ReadLineAsync(_cts.Token);
        }
        catch
        {
        }
        finally
        {
            pipe.Dispose();
        }
    }

    private async Task WriteAsync(NamedPipeServerStream pipe, SettingRequestAck ack)
    {
        var bytes = Encoding.UTF8.GetBytes(SettingRequestChannel.Serialize(ack) + "\n");
        await pipe.WriteAsync(bytes, _cts.Token);
        await pipe.FlushAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_pipes)
        {
            foreach (var pipe in _pipes)
            {
                try { pipe.Dispose(); } catch { }
            }
            _pipes.Clear();
        }
    }
}
