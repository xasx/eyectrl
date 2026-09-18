namespace EyeCtrl.App.Services;

using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.Contracts;
using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Platform.Ipc;
using EyeCtrl.Platform.Interop;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging;

public sealed class ExternalDwellService : IDisposable
{
    /// <summary>
    /// The production pipe name the cooperating dwell app connects to. Shared with the SDK client
    /// via <see cref="DwellPipe.Name"/> so server and client cannot drift.
    /// </summary>
    public const string DefaultPipeName = DwellPipe.Name;

    /// <summary>
    /// Named pipe this instance serves on. Defaults to <see cref="DefaultPipeName"/>; tests pass a
    /// unique GUID-suffixed name because pipe names are machine-global — any other process binding
    /// the production name (a <c>dotnet test</c> run in a sibling worktree, or a live
    /// <c>EyeCtrl.App</c>/<c>EyeCtrl.Supervisor</c>) can accept a test's client and then drop it
    /// mid-test (<c>IOException: Pipe is broken</c>). xUnit's <c>DisableParallelization</c> cannot
    /// prevent that, being process-local; not sharing the name is the only real fix. See issue #76.
    /// </summary>
    private readonly string _pipeName;

    /// <summary>Registration caps: a hostile or buggy client must not be able to grow the
    /// target tables (and per-frame gating cost) without bound. A register over a cap is
    /// rejected and logged; the connection itself stays alive for its existing targets.</summary>
    internal const int MaxTargetsPerConnection = 512;
    internal const int MaxTotalTargets = 4096;

    private readonly ExternalDwellSettings _settings;
    private readonly GazeEngine _gazeEngine;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<ExternalDwellService> _logger;
    private readonly IMessenger _messenger;
    private readonly Func<PhysicalPoint, long?> _rootWindowAtPoint;
    private readonly DwellDetector _detector;
    private readonly Dictionary<string, DwellTarget> _targetsById = new();
    private readonly Dictionary<string, long> _hwndById = new();
    private readonly DwellFeedbackOverlay _feedback;
    private readonly IDwellDetectorRegistry? _dwellRegistry;
    // Named so Dispose can detach it — an anonymous lambda here would keep every disposed
    // service reachable from the (singleton) settings object forever.
    private readonly PropertyChangedEventHandler _settingsChangedHandler;

    private readonly object _stateLock = new();
    private readonly List<PipeConnection> _connections = new();
    private readonly Dictionary<string, PipeConnection> _connByTarget = new();
    private readonly NewlineJsonPipeServer<PipeConnection> _pipeServer;

    private string? _feedbackId;
    private PhysicalRect _feedbackRect;
    private bool _suppressing;
    private string? _frameProgressId;
    private double _frameProgress;
    private string? _frameActivatedId;
    private bool _disposed;

    public ExternalDwellService(
        ExternalDwellSettings settings,
        GazeEngine gazeEngine,
        Dispatcher dispatcher,
        ILogger<ExternalDwellService> logger,
        IMessenger messenger,
        Func<PhysicalPoint, long?>? rootWindowAtPoint = null,
        IStruggleTelemetrySink? sink = null,
        IDwellDetectorRegistry? dwellRegistry = null,
        string? pipeName = null)
    {
        _pipeName = pipeName ?? DefaultPipeName;
        _settings = settings;
        _gazeEngine = gazeEngine;
        _dispatcher = dispatcher;
        _logger = logger;
        _messenger = messenger;
        _rootWindowAtPoint = rootWindowAtPoint ?? RootWindowAtPointImpl;
        _detector = new DwellDetector(settings.CooldownMs, DwellContext.ExternalDwell, sink);
        _detector.TargetProgress += (id, p) =>
        {
            if (p > 0) { _frameProgressId = id; _frameProgress = p; }
            else if (_frameProgressId == id) { _frameProgressId = null; _frameProgress = 0; }
        };
        _detector.TargetActivated += id => _frameActivatedId = id;
        _dwellRegistry = dwellRegistry;
        // Dwell + geometry routing (Phase 1c): the External range, NOT Standard — DefaultDwellMs
        // legally climbs to 3000, and an identity restore through [200, 2000] would truncate such a
        // baseline. Both appliers wrap their call in _stateLock: this detector Updates on the gaze
        // thread (OnCursorMoved) under that same lock, so an unlocked map write could interleave.
        _dwellRegistry?.Register(this, new DwellRoutingRegistration(
            DwellContext.ExternalDwell,
            AdaptationDwellRanges.ExternalMinDwellMs, AdaptationDwellRanges.ExternalMaxDwellMs,
            map => { lock (_stateLock) _detector.SetEffectiveDwellTime(map); },
            map => { lock (_stateLock) _detector.SetEffectiveGeometry(map); }));
        _feedback = new DwellFeedbackOverlay();
        _settingsChangedHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(ExternalDwellSettings.CooldownMs))
                lock (_stateLock) _detector.SetCooldown(_settings.CooldownMs);
            // When disabled, OnCursorMoved's early-return releases suppression + hides feedback on
            // the next gaze frame (on the gaze thread, where those fields live). We intentionally do
            // NOT clear registered targets here: clearing would strand already-connected clients on
            // re-enable (their tracker would send Update, which the server drops once TargetIds is
            // cleared), and it's unnecessary — disabled frames are simply not processed.
        };
        _settings.PropertyChanged += _settingsChangedHandler;
        _gazeEngine.CursorMoved += OnCursorMoved;

        // The PipeConnection IS this channel's per-connection state: the server hands it back to
        // every line handler and to teardown, so nothing here has to map a pipe to its writer.
        //
        // SECURITY: the pipe carries register/unregister commands that shape the user's dwell-click
        // channel, so it must not be connectable by arbitrary local processes. The default DACL of
        // the plain NamedPipeServerStream constructor grants read to Everyone; the explicit ACL
        // below restricts the pipe to the current user (SDK client apps run as the same user).
        // Same construction as HeartbeatPipeServer — see the warning in that file about copying
        // the unsecured constructor.
        _pipeServer = new NewlineJsonPipeServer<PipeConnection>(
            () => NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBufferSize: 0, outBufferSize: 0,
                new NamedPipeSecurityBuilder().BuildCurrentUserOnly()),
            createState: pipe =>
            {
                var conn = new PipeConnection(pipe, ex => _logger.LogWarning(ex, "External dwell pipe: write failed"));
                lock (_stateLock)
                    _connections.Add(conn);
                return conn;
            },
            handleLine: (line, conn) =>
            {
                var msg = JsonSerializer.Deserialize<WireMessage>(line, WireJson.Options);
                if (msg is null)
                    return false;
                HandleMessage(msg, conn);
                return true;
            },
            onClosed: TeardownConnection,
            log: (ex, line, context) =>
            {
                if (line is not null)
                    _logger.LogWarning(ex, "External dwell pipe: bad message {Line}", line);
                else
                    _logger.LogWarning(ex, "External dwell pipe: {Context}", context ?? "connection handler error");
            });
    }

    public void Start()
    {
        _pipeServer.Start();
    }

    /// <summary>Diagnostics/testing hook: the number of registered dwell targets. Read under
    /// the state lock, so tests can POLL for batched registrations to settle instead of
    /// sleeping a fixed delay (review nit: a fixed sleep is the flake-shaped part).</summary>
    internal int TargetCountForTests
    {
        get { lock (_stateLock) return _targetsById.Count; }
    }

    private void OnCursorMoved(GazeFrame frame)
    {
        if (!_settings.Enabled
            || _gazeEngine.InteractionManager.CurrentState == InteractionState.Paused)
        {
            ReleaseSuppression();
            SetFeedback(null, default, 0);
            return;
        }

        // Gap-held sample: the position is the frozen last valid one, so it is no evidence that the
        // user is still on the target. Return BEFORE the detector update — and before touching
        // suppression or feedback, so an in-progress external dwell neither advances nor is torn down
        // by a blink; the overlay simply holds its last progress. Ordered after the disabled/paused
        // check so disabling still releases on the next frame. Allocation-free: the frame is a struct
        // and nothing here closes over it (this runs on the gaze thread, inside the provider lock).
        if (frame.IsInGap) return;

        var gaze = frame.Position;
        var timestampUs = frame.TimestampMicroseconds;

        string? activatedId;
        string? feedbackId = null;
        PhysicalRect feedbackRect = default;
        double progress = 0;

        lock (_stateLock)
        {
            long? root = _targetsById.Count == 0 ? null : _rootWindowAtPoint(gaze);

            _frameProgressId = null;
            _frameProgress = 0;
            _frameActivatedId = null;

            _detector.Update(gaze, timestampUs,
                t => root is { } r && _hwndById.TryGetValue(t.Id, out var h) && h == r);

            activatedId = _frameActivatedId;
            if (activatedId == null && _frameProgressId is { } fid
                && _targetsById.TryGetValue(fid, out var target) && target.Bounds is { } b)
            {
                feedbackId = fid;
                feedbackRect = b;
                progress = _frameProgress;
            }
        }

        if (activatedId is { } activated)
        {
            SendActivated(activated);
            ReleaseSuppression();
            SetFeedback(null, default, 0);
            return;
        }

        if (feedbackId is { } fid2)
        {
            AcquireSuppression();
            SetFeedback(fid2, feedbackRect, progress);
        }
        else
        {
            ReleaseSuppression();
            SetFeedback(null, default, 0);
        }
    }

    // SuppressFixation is a single bool shared with EventCoordinator (keyboard / calibration).
    // We manage ONLY our own contribution: set it true while an external target is active, and
    // clear it ONLY if we set it. We never write it on frames where we aren't involved — doing so
    // would clobber another owner (e.g. clearing suppression while the keyboard is open).
    private void AcquireSuppression()
    {
        if (_suppressing) return;
        _suppressing = true;
        _gazeEngine.SuppressFixation = true;
        SetCursorRingHidden(true);
    }

    private void ReleaseSuppression()
    {
        if (!_suppressing) return;
        _suppressing = false;
        _gazeEngine.SuppressFixation = false;
        SetCursorRingHidden(false);
    }

    // Hide the gaze cursor ring while an external target is active (the dwell-progress overlay
    // replaces it). Routed through a message so CursorCircleViewModel remains the single owner of
    // ring visibility (it combines this with interaction state); marshaled because the recipient
    // touches the cursor window on the UI thread.
    private void SetCursorRingHidden(bool hidden) =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new ExternalDwellActiveMessage(hidden)));

    private void SetFeedback(string? id, PhysicalRect rect, double progress)
    {
        if (id == null)
        {
            if (_feedbackId == null) return;
            _feedbackId = null;
            _feedbackRect = default;
            _dispatcher.BeginInvoke(() => _feedback.HideFeedback());
            return;
        }

        // Reposition on target change OR when the same target's rect moves (e.g. window dragged
        // mid-dwell), not only on first show.
        bool reposition = _feedbackId != id || _feedbackRect != rect;
        _feedbackId = id;
        _feedbackRect = rect;
        _dispatcher.BeginInvoke(() =>
        {
            if (reposition) _feedback.ShowAt(rect.X, rect.Y, rect.Width, rect.Height);
            _feedback.SetProgress(progress);
        });
    }

    private void TeardownConnection(PipeConnection conn)
    {
        lock (_stateLock)
        {
            _connections.Remove(conn);
            foreach (var id in conn.TargetIds)
            {
                _connByTarget.Remove(id);
                _targetsById.Remove(id);
                _hwndById.Remove(id);
                _detector.RemoveTarget(id);
            }
        }
        conn.Dispose();
    }

    private void HandleMessage(WireMessage? msg, PipeConnection conn)
    {
        switch (msg)
        {
            case RegisterMessage r:
                lock (_stateLock)
                {
                    // Ownership gate: a target id belongs to the connection that registered it.
                    // A second connection re-registering an existing id must not silently steal
                    // the channel — that would reroute the user's dwell activations to the new
                    // client (UpdateMessage below has always been gated this way). The owner
                    // keeps the id until it unregisters or disconnects; a same-connection
                    // re-register stays an idempotent rect refresh.
                    if (_connByTarget.TryGetValue(r.Id, out var owner) && !ReferenceEquals(owner, conn))
                    {
                        _logger.LogWarning(
                            "External dwell pipe: rejected register of target {Id} from a client that does not own it", r.Id);
                        break;
                    }
                    if (!conn.TargetIds.Contains(r.Id)
                        && (conn.TargetIds.Count >= MaxTargetsPerConnection || _targetsById.Count >= MaxTotalTargets))
                    {
                        // A same-connection re-register adds nothing to the tables (idempotent
                        // rect refresh) and must never be capped: a client at exactly the cap
                        // could otherwise no longer refresh ANY rect, freezing every target at
                        // stale geometry while the connection looks healthy.
                        _logger.LogWarning(
                            "External dwell pipe: rejected register of target {Id} over registration cap (connection {ConnCount}/{PerConn}, total {Total}/{MaxTotal})",
                            r.Id, conn.TargetIds.Count, MaxTargetsPerConnection, _targetsById.Count, MaxTotalTargets);
                        break;
                    }
                    conn.Hwnd = r.Hwnd;
                    conn.TargetIds.Add(r.Id);
                    _connByTarget[r.Id] = conn;
                    _hwndById[r.Id] = r.Hwnd;
                    var rect = ToRect(r.Rect);
                    var target = new DwellTarget(r.Id, rect, _settings.DefaultDwellMs);
                    _targetsById[r.Id] = target;
                    _detector.AddTarget(target);
                }
                break;

            case UpdateMessage u:
                lock (_stateLock)
                {
                    if (conn.TargetIds.Contains(u.Id))
                    {
                        var rect = ToRect(u.Rect);
                        if (_targetsById.ContainsKey(u.Id))
                        {
                            // Rebase, never a raw Bounds/Center write: resets the as-registered
                            // extents to the SDK's new rect and re-applies the stored geometry map,
                            // so a routed margin can neither be lost nor revert the new size
                            // (round-1 blocker). In-place — the in-progress dwell survives.
                            _detector.RebaseTarget(u.Id, rect);
                        }
                        else
                        {
                            var target = new DwellTarget(u.Id, rect, _settings.DefaultDwellMs);
                            _targetsById[u.Id] = target;
                            _hwndById[u.Id] = conn.Hwnd;
                            _detector.AddTarget(target);
                        }
                    }
                }
                break;

            case UnregisterMessage un:
                lock (_stateLock)
                {
                    // Ownership gate (mirrors Update): only the owning connection may unregister
                    // a target. Without this, any connected client could remove another client's
                    // targets and permanently kill the user's ability to dwell-click in that app.
                    if (!conn.TargetIds.Contains(un.Id))
                    {
                        _logger.LogWarning(
                            "External dwell pipe: rejected unregister of target {Id} from a client that does not own it", un.Id);
                        break;
                    }
                    conn.TargetIds.Remove(un.Id);
                    _connByTarget.Remove(un.Id);
                    _targetsById.Remove(un.Id);
                    _hwndById.Remove(un.Id);
                    _detector.RemoveTarget(un.Id);
                }
                break;
        }
    }

    private void SendActivated(string id)
    {
        PipeConnection? conn;
        lock (_stateLock) _connByTarget.TryGetValue(id, out conn);
        if (conn is null) return;
        var json = JsonSerializer.Serialize<WireMessage>(new ActivatedMessage { Id = id }, WireJson.Options);
        _ = Task.Run(() => conn.Send(json));
    }

    // Pipe-supplied doubles reach WPF window positioning (DwellFeedbackOverlay.ShowAt); JSON
    // rejects NaN/Infinity, but legal extremes (1e308) would flow through DpiHelper into
    // double-based layout math. Clamp to a range far beyond any physical screen so a hostile or
    // buggy rect can never push layout out of bounds.
    internal static PhysicalRect ToRect(SdkRect r)
    {
        const double limit = 100_000;
        return new PhysicalRect(
            Math.Clamp(r.X, -limit, limit),
            Math.Clamp(r.Y, -limit, limit),
            Math.Clamp(r.Width, 0, limit),
            Math.Clamp(r.Height, 0, limit));
    }

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
        _settings.PropertyChanged -= _settingsChangedHandler;
        ReleaseSuppression();
        // Unregister BEFORE further teardown: the registry entry holds this detector through its
        // applier, and a disposing service must not receive any further routed maps.
        _dwellRegistry?.Unregister(this);
        _pipeServer.Dispose();
        lock (_stateLock)
        {
            foreach (var c in _connections) c.Dispose();
            _connections.Clear();
            _connByTarget.Clear();
        }
        _dispatcher.BeginInvoke(() => _feedback.Close());
    }

    private sealed class PipeConnection : IDisposable
    {
        private readonly object _writeLock = new();
        private readonly StreamWriter _writer;
        private readonly Action<Exception> _onError;
        public NamedPipeServerStream Pipe { get; }
        public HashSet<string> TargetIds { get; } = new();
        public long Hwnd { get; set; }

        public PipeConnection(NamedPipeServerStream pipe, Action<Exception> onError)
        {
            Pipe = pipe;
            _writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            _onError = onError;
        }

        public void Send(string line)
        {
            lock (_writeLock) { try { _writer.WriteLine(line); } catch (Exception ex) { _onError(ex); } }
        }

        public void Dispose()
        {
            lock (_writeLock) { try { _writer.Dispose(); } catch { } }
            Pipe.Dispose();
        }
    }
}
