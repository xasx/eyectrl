namespace EyeCtrl.Supervisor.Tests.Ipc;

using System.Diagnostics;
using System.IO.Pipes;
using EyeCtrl.Core.Models;
using EyeCtrl.Supervisor.Ipc;

/// <summary>
/// The Supervisor's request-channel client. Every outcome other than an acknowledgement means
/// "the App is not available" (a null status), and every one of them completes within the bounds:
/// the Supervisor must never wait on a hung App. Pipes are GUID-suffixed; nothing here touches the
/// production pipe.
/// </summary>
public sealed class AppRequestClientTests : IDisposable
{
    private readonly string _pipeName = SettingRequestChannel.PipeName + "_test_" + Guid.NewGuid().ToString("N");
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private FakeAppPipe Serve(FakeAppPipe.Behaviour behaviour, SettingRequestStatus status = SettingRequestStatus.Saved)
    {
        var server = new FakeAppPipe(_pipeName, behaviour, status);
        _disposables.Add(server);
        return server;
    }

    private AppRequestClient Client(TimeSpan? connect = null, TimeSpan? ack = null, Func<NamedPipeClientStream, bool>? serverIsApp = null) =>
        new(serverIsApp ?? (_ => true), _pipeName, connect ?? TimeSpan.FromSeconds(2), ack ?? TimeSpan.FromSeconds(2));

    [Theory]
    [InlineData(SettingRequestStatus.Saved)]
    [InlineData(SettingRequestStatus.Failed)]
    [InlineData(SettingRequestStatus.Rejected)]
    public async Task AnAcknowledgement_ReturnsItsStatus(SettingRequestStatus status)
    {
        var server = Serve(FakeAppPipe.Behaviour.Answer, status);
        var request = SettingRequest.SetStartOnBoot(true);

        var result = await Client().SendAsync(request).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Check.That(result.Status).IsEqualTo(status);
        Check.That(server.Received.Select(r => r.Id)).ContainsExactly(request.Id);
    }

    [Fact]
    public async Task NoServer_IsNotAvailable_WithinTheConnectBound()
    {
        var watch = Stopwatch.StartNew();
        var result = await Client(connect: TimeSpan.FromMilliseconds(300)).SendAsync(SettingRequest.SetStartOnBoot(true)).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        watch.Stop();

        Check.That(result.Status).IsNull();
        Check.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task AHungApp_AcceptsAndNeverAnswers_TimesOutWithinTheAckBound()
    {
        var server = Serve(FakeAppPipe.Behaviour.Hang);
        var client = Client(ack: TimeSpan.FromMilliseconds(400));

        var call = Stopwatch.StartNew();
        var sending = client.SendAsync(SettingRequest.SetStartOnBoot(true));
        var returnedAfter = call.Elapsed;   // the call itself hands back a task; it never waits
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        call.Stop();

        Check.That(await server.WaitForRequestAsync(TimeSpan.Zero)).IsTrue();   // it really was accepted
        Check.That(result.Status).IsNull();
        Check.That(result.Detail).Contains("no acknowledgement");
        Check.That(returnedAfter).IsLessThan(TimeSpan.FromMilliseconds(250));
        Check.That(call.Elapsed > TimeSpan.FromMilliseconds(350)).IsTrue();   // it did wait the bound out
        Check.That(call.Elapsed).IsLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task TheAppClosingMidRequest_IsNotAvailable_WithoutWaitingForTheBound()
    {
        // An App that exits or restarts after reading the request: a clean "not available", and
        // promptly — the close is the answer, not the 10 s acknowledgement bound.
        Serve(FakeAppPipe.Behaviour.CloseAfterRequest);

        var watch = Stopwatch.StartNew();
        var result = await Client(ack: TimeSpan.FromSeconds(10)).SendAsync(SettingRequest.SetStartOnBoot(true)).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        watch.Stop();

        Check.That(result.Status).IsNull();
        Check.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AnAcknowledgementForAnotherRequest_IsNotAvailable()
    {
        Serve(FakeAppPipe.Behaviour.WrongId);

        var result = await Client().SendAsync(SettingRequest.SetStartOnBoot(true)).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Check.That(result.Status).IsNull();
    }

    [Fact]
    public async Task AServerThatIsNotTheApp_IsNotAvailable_AndHearsNothing()
    {
        var server = Serve(FakeAppPipe.Behaviour.Answer);

        var result = await Client(serverIsApp: _ => false).SendAsync(SettingRequest.SetStartOnBoot(true)).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Check.That(result.Status).IsNull();
        Check.That(await server.WaitForRequestAsync(TimeSpan.FromMilliseconds(300))).IsFalse();
    }

    [Fact]
    public async Task TheProductionServerCheck_MatchesTheServingExe_Exactly()
    {
        // The server is this test host, so it IS the exe at Environment.ProcessPath — and is not
        // an App exe anywhere else.
        using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accepting = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        await client.ConnectAsync(5000, TestContext.Current.CancellationToken);
        await accepting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Check.That(AppRequestClient.ServerIsApp(client, Environment.ProcessPath!)).IsTrue();
        Check.That(AppRequestClient.ServerIsApp(client, Environment.ProcessPath!.ToUpperInvariant())).IsTrue();
        Check.That(AppRequestClient.ServerIsApp(client, @"C:\Elsewhere\EyeCtrl.App.exe")).IsFalse();
    }
}
