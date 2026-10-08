namespace EyeCtrl.App.Tests.Services;

using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The App's side of the Supervisor → App request channel. Every test serves on a GUID-suffixed
/// pipe and saves under a GUID-suffixed writer mutex, so nothing here touches a live EyeCtrl's
/// pipe or mutex on this box. The client is a minimal one written against the Core wire records —
/// the same records the Supervisor's client uses.
/// </summary>
public sealed class SupervisorRequestServiceTests : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private readonly string _pipeName = SupervisorRequestService.DefaultPipeName + "_test_" + Guid.NewGuid().ToString("N");
    private readonly string _mutexName = @"Local\EyeCtrl.Settings.Writer.Test." + Guid.NewGuid().ToString("N");
    private readonly string _settingsPath = Path.Combine(
        Path.GetTempPath(), "eyectrl-request-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly DispatcherThread _ui = new();
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        _ui.Dispose();
        try { if (File.Exists(_settingsPath)) File.Delete(_settingsPath); } catch { }
    }

    // ── Round trip ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RoundTrip_SavesTheValue_ReconcilesTheRunKey_AndAcknowledges(bool startOnBoot)
    {
        var app = CreateApp(initialStartOnBoot: !startOnBoot);
        var request = SettingRequest.SetStartOnBoot(startOnBoot);

        var ack = await SendAsync(request);

        Check.That(ack).IsNotNull();
        Check.That(ack!.Id).IsEqualTo(request.Id);
        Check.That(ack.Status).IsEqualTo(SettingRequestStatus.Saved);
        Check.That(ReadStartOnBootFromDisk()).IsEqualTo(startOnBoot);
        Check.That(app.Settings.General.StartOnBoot).IsEqualTo(startOnBoot);
        // Standalone (supervision off): the reconciler registers exactly when StartOnBoot is on.
        Check.That(app.Registrar.Calls).ContainsExactly(startOnBoot);
        // The handler ran on the dispatcher, where every App surface reads the settings object.
        Check.That(app.HandledOnThread).ContainsExactly(_ui.ManagedThreadId);
    }

    [Fact]
    public async Task TheAcknowledgement_WaitsForTheSaveToComplete()
    {
        using var saveMayFinish = new ManualResetEventSlim(false);
        var saveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CreateApp(initialStartOnBoot: false, beforeSave: () =>
        {
            saveEntered.TrySetResult();
            saveMayFinish.Wait(Generous);
        });

        await using var client = await ConnectAsync();
        await WriteAsync(client, SettingRequest.SetStartOnBoot(true));
        await saveEntered.Task.WaitAsync(Generous, TestContext.Current.CancellationToken);

        var reading = ReadAckAsync(client);
        var early = await Task.WhenAny(reading, Task.Delay(300, TestContext.Current.CancellationToken));
        Check.WithCustomMessage("an acknowledgement arrived while the save was still running")
            .That(early == reading).IsFalse();

        saveMayFinish.Set();
        var ack = await reading.WaitAsync(Generous, TestContext.Current.CancellationToken);
        Check.That(ack!.Status).IsEqualTo(SettingRequestStatus.Saved);
        Check.That(ReadStartOnBootFromDisk()).IsTrue();
    }

    // ── Who may call ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ANonSupervisorClient_IsRefused_AndNothingChanges()
    {
        // The production check: this test host is not the Supervisor exe, so its connection is
        // dropped before its first line is read.
        var app = CreateApp(initialStartOnBoot: false, productionClientCheck: true);

        var ack = await SendAsync(SettingRequest.SetStartOnBoot(true));

        Check.That(ack).IsNull();
        Check.That(app.HandledOnThread).IsEmpty();
        Check.That(app.Settings.General.StartOnBoot).IsFalse();
        Check.That(app.Registrar.Calls).IsEmpty();
        Check.That(ReadStartOnBootFromDisk()).IsFalse();
    }

    [Fact]
    public async Task TheProductionClientCheck_RefusesThisTestHost()
    {
        using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accepting = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        await client.ConnectAsync(5000, TestContext.Current.CancellationToken);
        await accepting.WaitAsync(Generous, TestContext.Current.CancellationToken);

        Check.That(SupervisorRequestService.IsSupervisorClient(server)).IsFalse();
    }

    // ── Bad requests and failed saves ───────────────────────────────────────────────────────

    [Fact]
    public async Task AnUnknownKind_IsRejected()
    {
        var app = CreateApp(initialStartOnBoot: false);
        var request = new SettingRequest("id-1", "setSomethingElse", JsonSerializer.SerializeToElement(true), false);

        var ack = await SendAsync(request);

        Check.That(ack!.Status).IsEqualTo(SettingRequestStatus.Rejected);
        Check.That(app.Settings.General.StartOnBoot).IsFalse();
        Check.That(app.SaveCount).IsEqualTo(0);
    }

    [Fact]
    public async Task AValueOfTheWrongType_IsRejected_AndNotSaved()
    {
        var app = CreateApp(initialStartOnBoot: false);
        var request = new SettingRequest(
            "id-2", SettingRequestKinds.SetStartOnBoot, JsonSerializer.SerializeToElement("true"), false);

        var ack = await SendAsync(request);

        Check.That(ack!.Status).IsEqualTo(SettingRequestStatus.Rejected);
        Check.That(app.Settings.General.StartOnBoot).IsFalse();
        Check.That(app.SaveCount).IsEqualTo(0);
    }

    [Fact]
    public async Task AFailedSave_IsAcknowledgedAsFailed()
    {
        CreateApp(initialStartOnBoot: false, beforeSave: () => throw new IOException("disk full"));

        var ack = await SendAsync(SettingRequest.SetStartOnBoot(true));

        Check.That(ack!.Status).IsEqualTo(SettingRequestStatus.Failed);
        Check.That(ReadStartOnBootFromDisk()).IsFalse();
    }

    [Fact]
    public async Task AThrowingDispatcher_IsAcknowledgedAsFailed()
    {
        var server = new SupervisorRequestService(
            _ => SettingRequestStatus.Saved,
            runOnUi: _ => Task.FromException<SettingRequestStatus>(new InvalidOperationException("dispatcher shut down")),
            NullLogger.Instance, _pipeName, isAuthorizedClient: _ => true);
        _disposables.Add(server);
        server.Start();

        var ack = await SendAsync(SettingRequest.SetStartOnBoot(true));

        Check.That(ack!.Status).IsEqualTo(SettingRequestStatus.Failed);
    }

    // ── App exit mid-request ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheAppExitingMidRequest_ClosesTheConnection_WithoutAnAcknowledgement()
    {
        // The dispatcher is busy (a hung or exiting App): the request is read but never handled.
        using var dispatcherFree = new ManualResetEventSlim(false);
        _ = _ui.Dispatcher.BeginInvoke(() => dispatcherFree.Wait(Generous));
        var app = CreateApp(initialStartOnBoot: false);

        await using var client = await ConnectAsync();
        await WriteAsync(client, SettingRequest.SetStartOnBoot(true));
        var reading = ReadAckAsync(client);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        var watch = Stopwatch.StartNew();
        app.Server.Dispose();   // OnExit's "dispose supervisor request service" step
        var ack = await reading.WaitAsync(Generous, TestContext.Current.CancellationToken);
        watch.Stop();
        dispatcherFree.Set();

        Check.That(ack).IsNull();
        Check.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(2));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private sealed record TestApp(
        EyeCtrlSettings Settings,
        FakeRegistrar Registrar,
        SupervisorRequestService Server,
        List<int> HandledOnThread,
        Func<int> SaveCountReader)
    {
        public int SaveCount => SaveCountReader();
    }

    private TestApp CreateApp(
        bool initialStartOnBoot,
        Action? beforeSave = null,
        bool productionClientCheck = false)
    {
        var service = new SettingsService(_settingsPath, SettingsWriter.App, _mutexName);
        var settings = new EyeCtrlSettings();
        settings.General.StartOnBoot = initialStartOnBoot;
        service.Save(settings);

        // The App's production wiring: the live reconciler re-runs on every StartOnBoot change.
        var registrar = new FakeRegistrar();
        SupervisionComposition.ReconcileRunKeyOnStartOnBootChange(
            settings, SupervisionComposition.CreateRunKeyReconciler(settings, registrar, () => false));

        var saves = 0;
        var handler = new SupervisorRequestHandler(
            settings,
            s =>
            {
                beforeSave?.Invoke();
                service.Save(s);
                Interlocked.Increment(ref saves);
            },
            NullLogger.Instance);

        var handledOn = new List<int>();
        var server = new SupervisorRequestService(
            request =>
            {
                lock (handledOn) handledOn.Add(Environment.CurrentManagedThreadId);
                return handler.Handle(request);
            },
            runOnUi: work => _ui.Dispatcher.InvokeAsync(work).Task,
            NullLogger.Instance,
            _pipeName,
            // The test host is the client, so tests that are not ABOUT the client check let it in.
            isAuthorizedClient: productionClientCheck ? null : _ => true);
        _disposables.Add(server);
        server.Start();
        return new TestApp(settings, registrar, server, handledOn, () => Volatile.Read(ref saves));
    }

    private bool ReadStartOnBootFromDisk()
    {
        var root = JsonNode.Parse(File.ReadAllText(_settingsPath))!;
        return root["general"]!["startOnBoot"]!.GetValue<bool>();
    }

    private async Task<SettingRequestAck?> SendAsync(SettingRequest request)
    {
        await using var client = await ConnectAsync();
        try
        {
            await WriteAsync(client, request);
        }
        catch (IOException)
        {
            return null;   // the server dropped us before the write: refused
        }
        return await ReadAckAsync(client).WaitAsync(Generous, TestContext.Current.CancellationToken);
    }

    private async Task<NamedPipeClientStream> ConnectAsync()
    {
        var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, TestContext.Current.CancellationToken);
        return client;
    }

    private static async Task WriteAsync(NamedPipeClientStream client, SettingRequest request)
    {
        var bytes = Encoding.UTF8.GetBytes(SettingRequestChannel.Serialize(request) + "\n");
        await client.WriteAsync(bytes, TestContext.Current.CancellationToken);
        await client.FlushAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The acknowledgement, or null when the server closed the connection first.</summary>
    private static async Task<SettingRequestAck?> ReadAckAsync(NamedPipeClientStream client)
    {
        try
        {
            using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);
            var line = await reader.ReadLineAsync(TestContext.Current.CancellationToken);
            return line is null ? null : SettingRequestChannel.ParseAck(line);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private sealed class FakeRegistrar : IStartupRegistrar
    {
        private readonly List<bool> _calls = new();

        public IReadOnlyList<bool> Calls
        {
            get { lock (_calls) return _calls.ToList(); }
        }

        public bool IsRegistered { get; private set; }

        public void SetStartOnBoot(bool enabled)
        {
            lock (_calls) _calls.Add(enabled);
            IsRegistered = enabled;
        }
    }

    /// <summary>A real WPF dispatcher on its own thread — the App's UI thread, headless.</summary>
    private sealed class DispatcherThread : IDisposable
    {
        private readonly Thread _thread;

        public DispatcherThread()
        {
            using var ready = new ManualResetEventSlim(false);
            Dispatcher? dispatcher = null;
            _thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            }) { IsBackground = true, Name = "test-ui" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait();
            Dispatcher = dispatcher!;
            ManagedThreadId = _thread.ManagedThreadId;
        }

        public Dispatcher Dispatcher { get; }

        public int ManagedThreadId { get; }

        public void Dispose()
        {
            Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}
