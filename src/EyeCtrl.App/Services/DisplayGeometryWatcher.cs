namespace EyeCtrl.App.Services;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Messages;
using Serilog;

/// <summary>
/// Watches for display, resolution and scaling changes and re-publishes the geometry the app had
/// been caching since startup.
/// </summary>
/// <remarks>
/// <b>What this closes.</b> Nothing in the application listened for a display change. Three caches
/// were filled once at startup and never refreshed — the DPI scale, the gaze provider's screen
/// bounds that normalised 0..1 gaze is multiplied by, and the toolbar's screen size plus summon
/// rectangle. A dock, undock, resolution change, scaling change or RDP reconnect therefore left
/// gaze addressing the wrong pixel space, with dwell targets displaced from where they were drawn.
/// <para>
/// <b>Why it could not be noticed.</b> The supervisor's only signals are process exit and
/// heartbeat freshness, and a stale-geometry app still delivers gaze frames — so the watchdog saw
/// a perfectly healthy process while the user had no usable control. The OS cursor only moves in
/// FreeLook and Dragging, so there was no cursor fallback either, and the tray menu was
/// unreachable. It needed no bug: only a hardware event people do routinely.
/// </para>
/// <para>
/// <b>Why a plausibility check on the gaze stream is not a substitute</b>, though it looks like a
/// cheaper one: <c>CalibrationValidator.IsPlausibleSample</c> rejects coordinates outside
/// [-0.5·W, 1.5·W], so a screen that GROWS (1920 → 3840) produces coordinates that are all
/// plausible while the user is confined to the left half. It detects only gross corruption, and
/// only if it is already comparing against fresh bounds — which is this class's job. An output
/// check cannot replace knowing the geometry changed.
/// </para>
/// <para>
/// <b>Threading.</b> <c>SystemEvents.DisplaySettingsChanged</c> arrives on the SystemEvents
/// listener thread, not the dispatcher, and arrives in bursts during a mode switch. Everything
/// here is marshalled onto the UI thread and coalesced by comparing against the last applied
/// snapshot, so a burst reporting the same geometry costs one comparison rather than repeatedly
/// repositioning overlays and republishing dwell targets.
/// </para>
/// </remarks>
public sealed class DisplayGeometryWatcher : IDisposable
{
    private readonly Func<DisplayGeometry> _read;
    private readonly Action<Action> _postToUi;
    private readonly IGazeProvider _gazeProvider;
    private readonly IMessenger _messenger;
    private readonly Action<EventHandler> _subscribe;
    private readonly Action<EventHandler> _unsubscribe;

    private EventHandler? _handler;
    private DisplayGeometry _lastApplied;
    private bool _disposed;

    /// <summary>Creates the watcher. Nothing is observed until <see cref="Start"/>.</summary>
    /// <param name="gazeProvider">Receives the new bounds; providers that emit physical pixels ignore them.</param>
    /// <param name="messenger">Carries <see cref="DisplayGeometryChangedMessage"/> to UI-side consumers.</param>
    /// <param name="read">
    /// Reads the current geometry. Injected rather than called directly so the whole class is
    /// headlessly testable — the real implementation reads WPF <c>SystemParameters</c> and the
    /// Win32 system DPI, neither of which a test can move.
    /// </param>
    /// <param name="postToUi">Marshals onto the dispatcher.</param>
    /// <param name="initial">
    /// The geometry already in force at startup, so the first notification only fires if something
    /// genuinely changed.
    /// </param>
    /// <param name="subscribe">Hooks the OS notification. Injected for the same reason as <paramref name="read"/>.</param>
    /// <param name="unsubscribe">Unhooks it.</param>
    public DisplayGeometryWatcher(
        IGazeProvider gazeProvider,
        IMessenger messenger,
        Func<DisplayGeometry> read,
        Action<Action> postToUi,
        DisplayGeometry initial,
        Action<EventHandler> subscribe,
        Action<EventHandler> unsubscribe)
    {
        _gazeProvider = gazeProvider;
        _messenger = messenger;
        _read = read;
        _postToUi = postToUi;
        _lastApplied = initial;
        _subscribe = subscribe;
        _unsubscribe = unsubscribe;
    }

    /// <summary>Begins observing. Idempotent.</summary>
    public void Start()
    {
        if (_handler != null || _disposed) return;
        _handler = (_, _) => OnDisplaySettingsChanged();
        _subscribe(_handler);
    }

    /// <summary>
    /// Applies a geometry change if there is one. <c>internal</c> so tests drive it without an OS
    /// event, following the coordinator precedent.
    /// </summary>
    internal void OnDisplaySettingsChanged() => _postToUi(Apply);

    /// <summary>
    /// Reads the geometry and hands the same snapshot to each sink, in dependency order.
    /// </summary>
    /// <remarks>
    /// The order is the design, not incidental:
    /// <list type="number">
    /// <item><c>DpiHelper</c> first, because it is a pure conversion factor that everything else
    /// converts through — including the toolbar's own re-measurement.</item>
    /// <item>The message next, so UI-side consumers re-measure and reposition using the new
    /// scale. Messenger delivery is synchronous on this thread, so this completes before the
    /// provider changes.</item>
    /// <item>The gaze provider last. It is the source of the coordinate space, so switching it
    /// once the consumers can already interpret the new space keeps the inconsistent window as
    /// short as possible.</item>
    /// </list>
    /// Total atomicity across three independently-read sinks is not achievable while the gaze
    /// thread runs lock-free, and buying it would mean putting a lock on the 30–90 Hz hot path.
    /// What is guaranteed instead: each sink publishes its own state by a single reference swap, so
    /// no reader ever sees a half-applied value, and the toolbar's screen bounds and summon
    /// rectangle — the pair whose disagreement would make the escape hatch unreachable — move as
    /// one indivisible unit. A few samples during a mode switch may mix old and new space; that is
    /// bounded and self-correcting, unlike the permanent mismatch this replaces.
    /// </remarks>
    private void Apply()
    {
        if (_disposed) return;

        DisplayGeometry geometry;
        try
        {
            geometry = _read();
        }
        catch (Exception ex)
        {
            // A failed read must not end the session: the app keeps its previous geometry, which
            // is what it would have had anyway before this class existed.
            Log.Error(ex, "Failed to read display geometry after a display change; keeping the previous snapshot.");
            return;
        }

        // Windows reports 0x0 briefly during a mode switch and while every monitor is detached.
        // Applying that would scale every gaze sample to the origin and shrink the summon region to
        // nothing — a worse and more persistent failure than being one notification stale, since
        // the correct value arrives on the next notification in the burst.
        if (!geometry.IsUsable)
        {
            Log.Warning("Ignoring unusable display geometry {Geometry}.", geometry);
            return;
        }

        if (!geometry.DiffersFrom(_lastApplied)) return;

        Log.Information("Display geometry changed {Old} -> {New}; refreshing gaze and overlay geometry.",
            _lastApplied, geometry);
        _lastApplied = geometry;

        DpiHelper.Update(geometry.ScaleX, geometry.ScaleY);

        // Guarded separately: a throwing consumer must not prevent the gaze provider being told,
        // or gaze keeps arriving in the old space — the very failure being fixed — while the
        // overlays have already moved.
        try
        {
            _messenger.Send(new DisplayGeometryChangedMessage(geometry));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "A display-geometry consumer threw; continuing to update the gaze provider.");
        }

        try
        {
            _gazeProvider.UpdateScreenBounds(geometry.WidthPx, geometry.HeightPx);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to update gaze provider screen bounds.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handler != null)
        {
            try { _unsubscribe(_handler); }
            catch (Exception ex) { Log.Error(ex, "Failed to unsubscribe from display-settings notifications."); }
            _handler = null;
        }
    }
}
