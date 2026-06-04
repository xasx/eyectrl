namespace EyeCtrl.App.Services;

using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using EyeCtrl.App.Overlays;
using EyeCtrl.Contracts;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.Logging;

public sealed class ExternalDwellService : IDisposable
{
    private const string PipeName = "eyectrl_dwell";

    private readonly ExternalDwellSettings _settings;
    private readonly GazeEngine _gazeEngine;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<ExternalDwellService> _logger;
    private readonly ExternalDwellArbiter _arbiter;
    private readonly DwellFeedbackOverlay _feedback;
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly object _stateLock = new();
    private readonly List<PipeConnection> _connections = new();
    private readonly Dictionary<string, PipeConnection> _connByTarget = new();

    private string? _feedbackId;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public ExternalDwellService(
        ExternalDwellSettings settings,
        GazeEngine gazeEngine,
        Dispatcher dispatcher,
        ILogger<ExternalDwellService> logger,
        Func<PhysicalPoint, long?>? rootWindowAtPoint = null)
    {
        _settings = settings;
        _gazeEngine = gazeEngine;
        _dispatcher = dispatcher;
        _logger = logger;
        _arbiter = new ExternalDwellArbiter(rootWindowAtPoint ?? RootWindowAtPointImpl, settings.CooldownMs);
        _feedback = new DwellFeedbackOverlay();
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExternalDwellSettings.CooldownMs))
                lock (_stateLock) _arbiter.SetCooldown(_settings.CooldownMs);
        };
        _gazeEngine.CursorMoved += OnCursorMoved;
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);
    }

    private void OnCursorMoved(PhysicalPoint gaze, long timestampUs)
    {
        if (!_settings.Enabled) return;

        ArbiterResult result;
        lock (_stateLock) result = _arbiter.Process(gaze, timestampUs);

        if (result.ActivatedId is { } activated)
        {
            SendActivated(activated);
            _gazeEngine.SuppressFixation = false;
            SetFeedback(null, default, 0);
            return;
        }

        if (result.FeedbackId is { } fid)
        {
            _gazeEngine.SuppressFixation = true;
            SetFeedback(fid, result.FeedbackRect!.Value, result.Progress);
        }
        else
        {
            _gazeEngine.SuppressFixation = false;
            SetFeedback(null, default, 0);
        }
    }

    private void SetFeedback(string? id, PhysicalRect rect, double progress)
    {
        if (id == null)
        {
            if (_feedbackId == null) return;
            _feedbackId = null;
            _dispatcher.BeginInvoke(() => _feedback.HideFeedback());
            return;
        }

        bool isNew = _feedbackId != id;
        _feedbackId = id;
        _dispatcher.BeginInvoke(() =>
        {
            if (isNew) _feedback.ShowAt(rect.X, rect.Y, rect.Width, rect.Height);
            _feedback.SetProgress(progress);
        });
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(token);
            }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
            catch (Exception ex) { _logger.LogWarning(ex, "pipe accept failed"); pipe.Dispose(); continue; }

            var conn = new PipeConnection(pipe);
            lock (_stateLock) _connections.Add(conn);
            _ = HandleConnectionAsync(conn, token);
        }
    }

    private async Task HandleConnectionAsync(PipeConnection conn, CancellationToken token)
    {
        try
        {
            using var reader = new StreamReader(conn.Pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            while (!token.IsCancellationRequested && conn.Pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(token);
                if (line == null) break;
                try { HandleMessage(JsonSerializer.Deserialize<WireMessage>(line, _json), conn); }
                catch (Exception ex) { _logger.LogWarning(ex, "bad pipe message: {Line}", line); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "connection handler error"); }
        finally
        {
            lock (_stateLock)
            {
                _connections.Remove(conn);
                foreach (var id in conn.TargetIds)
                {
                    _connByTarget.Remove(id);
                    _arbiter.Remove(id);
                }
            }
            conn.Dispose();
        }
    }

    private void HandleMessage(WireMessage? msg, PipeConnection conn)
    {
        switch (msg)
        {
            case RegisterMessage r when _settings.Enabled:
                lock (_stateLock)
                {
                    conn.Hwnd = r.Hwnd;
                    conn.TargetIds.Add(r.Id);
                    _connByTarget[r.Id] = conn;
                    _arbiter.AddOrUpdate(r.Id, ToRect(r.Rect), r.Hwnd, _settings.DefaultDwellMs);
                }
                break;

            case UpdateMessage u:
                lock (_stateLock)
                {
                    if (conn.TargetIds.Contains(u.Id))
                        _arbiter.AddOrUpdate(u.Id, ToRect(u.Rect), conn.Hwnd, _settings.DefaultDwellMs);
                }
                break;

            case UnregisterMessage un:
                lock (_stateLock)
                {
                    conn.TargetIds.Remove(un.Id);
                    _connByTarget.Remove(un.Id);
                    _arbiter.Remove(un.Id);
                }
                break;
        }
    }

    private void SendActivated(string id)
    {
        PipeConnection? conn;
        lock (_stateLock) _connByTarget.TryGetValue(id, out conn);
        if (conn is null) return;
        var json = JsonSerializer.Serialize<WireMessage>(new ActivatedMessage { Id = id }, _json);
        _ = Task.Run(() => conn.Send(json));
    }

    private static PhysicalRect ToRect(SdkRect r) => new(r.X, r.Y, r.Width, r.Height);

    private static long? RootWindowAtPointImpl(PhysicalPoint p)
    {
        var hwnd = WindowFromPoint(new POINT { X = (int)Math.Round(p.X), Y = (int)Math.Round(p.Y) });
        if (hwnd == IntPtr.Zero) return null;
        var root = GetAncestor(hwnd, GA_ROOT);
        return (root == IntPtr.Zero ? hwnd : root).ToInt64();
    }

    private const uint GA_ROOT = 2;
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gazeEngine.CursorMoved -= OnCursorMoved;
        _cts?.Cancel();
        lock (_stateLock)
        {
            foreach (var c in _connections) c.Dispose();
            _connections.Clear();
            _connByTarget.Clear();
        }
        _cts?.Dispose();
        _dispatcher.BeginInvoke(() => _feedback.Close());
    }

    private sealed class PipeConnection : IDisposable
    {
        private readonly object _writeLock = new();
        private readonly StreamWriter _writer;
        public NamedPipeServerStream Pipe { get; }
        public HashSet<string> TargetIds { get; } = new();
        public long Hwnd { get; set; }

        public PipeConnection(NamedPipeServerStream pipe)
        {
            Pipe = pipe;
            _writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        }

        public void Send(string line)
        {
            lock (_writeLock) { try { _writer.WriteLine(line); } catch { } }
        }

        public void Dispose()
        {
            lock (_writeLock) { try { _writer.Dispose(); } catch { } }
            Pipe.Dispose();
        }
    }
}
