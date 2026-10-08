namespace EyeCtrl.Supervisor.Tests.Supervision;

using System.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Supervisor.Ipc;
using EyeCtrl.Supervisor.Supervision;
using EyeCtrl.Supervisor.Tests.Ipc;

/// <summary>
/// The StartOnBoot delivery as the Supervisor runs it: on a real dispatcher thread, through the
/// real client, against a pipe server that behaves like a hung, absent or restarting App. Pipes
/// are GUID-suffixed; nothing here touches the production pipe.
/// </summary>
public sealed class StartOnBootDeliveryIntegrationTests : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    private readonly string _pipeName = SettingRequestChannel.PipeName + "_test_" + Guid.NewGuid().ToString("N");
    private readonly DispatcherThread _ui = new();
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        _ui.Dispose();
    }

    private FakeAppPipe Serve(FakeAppPipe.Behaviour behaviour)
    {
        var server = new FakeAppPipe(_pipeName, behaviour);
        _disposables.Add(server);
        return server;
    }

    /// <summary>Built on the UI thread, wired exactly as Program wires it.</summary>
    private SettingDelivery<bool> CreateDelivery(TimeSpan connect, TimeSpan ack)
    {
        var client = new AppRequestClient(_ => true, _pipeName, connect, ack);
        return _ui.Dispatcher.Invoke(() => new SettingDelivery<bool>(
            "StartOnBoot",
            (startOnBoot, _) => SettingRequest.SetStartOnBoot(startOnBoot),
            client.SendAsync,
            work => _ui.Dispatcher.BeginInvoke(work)));
    }

    private TimeSpan TimeOnUi(Action action)
    {
        var watch = Stopwatch.StartNew();
        _ui.Dispatcher.Invoke(action);
        return watch.Elapsed;
    }

    private async Task WaitForStateAsync(SettingDelivery<bool> delivery, SettingDeliveryState state)
    {
        var deadline = Stopwatch.StartNew();
        while (_ui.Dispatcher.Invoke(() => delivery.State) != state)
        {
            if (deadline.Elapsed > Settle)
                throw new TimeoutException($"delivery stayed {_ui.Dispatcher.Invoke(() => delivery.State)}, expected {state}");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task AHungApp_NeverBlocksTheUiThread_AndTheChoiceParksWithinTheBound()
    {
        var server = Serve(FakeAppPipe.Behaviour.Hang);
        var delivery = CreateDelivery(connect: TimeSpan.FromSeconds(2), ack: TimeSpan.FromMilliseconds(800));

        var started = Stopwatch.StartNew();
        var requestTook = TimeOnUi(() => delivery.Request(true));
        Check.That(await server.WaitForRequestAsync(Settle)).IsTrue();

        // The App holds the request; the UI thread is free the whole time.
        Check.That(_ui.Dispatcher.Invoke(() => delivery.State)).IsEqualTo(SettingDeliveryState.Sending);
        var pumpTook = TimeOnUi(() => { });

        await WaitForStateAsync(delivery, SettingDeliveryState.Parked);
        started.Stop();

        Check.That(requestTook).IsLessThan(TimeSpan.FromMilliseconds(300));
        Check.That(pumpTook).IsLessThan(TimeSpan.FromMilliseconds(300));
        Check.That(started.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
        Check.That(_ui.Dispatcher.Invoke(() => delivery.Pending)).IsEqualTo(true);
    }

    [Fact]
    public async Task HeartbeatsDuringAHungRequest_SendItOnlyOnce()
    {
        var server = Serve(FakeAppPipe.Behaviour.Hang);
        var delivery = CreateDelivery(connect: TimeSpan.FromSeconds(2), ack: TimeSpan.FromMilliseconds(800));

        _ui.Dispatcher.Invoke(() => delivery.Request(true));
        for (var i = 0; i < 10; i++)
            _ui.Dispatcher.Invoke(delivery.OnAppHeartbeat);
        await WaitForStateAsync(delivery, SettingDeliveryState.Parked);

        Check.That(server.Received).HasSize(1);
    }

    [Fact]
    public async Task AppDown_TheChoiceIsParked_DeliveredOnTheNextHeartbeat_AndClearedOnTheAcknowledgement()
    {
        var delivery = CreateDelivery(connect: TimeSpan.FromMilliseconds(300), ack: TimeSpan.FromSeconds(2));

        _ui.Dispatcher.Invoke(() => delivery.Request(true));
        await WaitForStateAsync(delivery, SettingDeliveryState.Parked);
        Check.That(_ui.Dispatcher.Invoke(() => delivery.Pending)).IsEqualTo(true);

        var app = Serve(FakeAppPipe.Behaviour.Answer);   // the App comes up and sends heartbeats
        _ui.Dispatcher.Invoke(delivery.OnAppHeartbeat);
        await WaitForStateAsync(delivery, SettingDeliveryState.Saved);

        Check.That(_ui.Dispatcher.Invoke(() => delivery.Pending)).IsNull();
        Check.That(app.Received).HasSize(1);
        Check.That(app.Received[0].Value.GetBoolean()).IsTrue();
        Check.That(app.Received[0].Restart).IsFalse();
    }

    [Fact]
    public async Task AnAppRestartMidRequest_ParksCleanly_ThenTheNextAppAcknowledges()
    {
        // The acknowledgement bound is long on purpose: parking must come from the closed
        // connection, promptly, not from waiting the bound out.
        var exiting = Serve(FakeAppPipe.Behaviour.CloseAfterRequest);
        var delivery = CreateDelivery(connect: TimeSpan.FromSeconds(2), ack: TimeSpan.FromSeconds(30));

        var watch = Stopwatch.StartNew();
        _ui.Dispatcher.Invoke(() => delivery.Request(false));
        await WaitForStateAsync(delivery, SettingDeliveryState.Parked);
        watch.Stop();
        Check.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
        Check.That(exiting.Received).HasSize(1);

        exiting.Dispose();
        var restarted = Serve(FakeAppPipe.Behaviour.Answer);
        _ui.Dispatcher.Invoke(delivery.OnAppHeartbeat);
        await WaitForStateAsync(delivery, SettingDeliveryState.Saved);

        Check.That(_ui.Dispatcher.Invoke(() => delivery.Pending)).IsNull();
        Check.That(_ui.Dispatcher.Invoke(() => delivery.LastSaved)).IsEqualTo(false);
        Check.That(restarted.Received).HasSize(1);
    }
}
