namespace EyeCtrl.App.Services;

using System;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Messages;
using Serilog;

/// <summary>
/// The T0(a) lag-mark host: owns the lag button's lifetime in an instrumented session and turns a
/// completed dwell into the <c>LAG MARK</c> line. Constructed ONLY when the dispatch probe exists
/// (<c>settings.Diagnostics.MeasureUiInstruments</c> was on at startup), so an ordinary session
/// constructs nothing and changes nothing.
///
/// <para>
/// The mark's time is the completing sample's own moment, not the log-write moment: the sample the
/// dwell completed on was enqueued one dispatch wait ago (<see cref="DispatchLatencyProbe.LastLatencyMs"/>),
/// so the wall clock is <c>now − that wait</c> and the probe's window ends at the matching stopwatch
/// instant. The log-write time would carry the very lag being marked.
/// </para>
///
/// <para>
/// The host also gates the live instrument switch (off mid-session hides the button and stops
/// marking, like the adapter's per-sample gate), hides while Settings is open, and tells the
/// activation-offer coordinator where the button is — as a zone to avoid, never a suspension.
/// </para>
/// </summary>
public sealed class LagMarkHost : IDisposable
{
    /// <summary>The window a mark quotes: what the queue did in the 15 s before the mark.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(15);

    private readonly IMessenger _messenger;
    private readonly ILagMarkPresenter _presenter;
    private readonly DispatchLatencyProbe _probe;
    private readonly Func<bool> _isInstrumentOn;
    private readonly Func<bool> _isSettingsOpen;
    private readonly Func<bool> _isProbeOpen;
    private readonly Func<DateTime> _now;
    private readonly Action<Action> _postToUi;
    private readonly Action<string> _log;

    /// <summary>Creates the host. Call <see cref="Start"/> once the overlays are up.</summary>
    /// <param name="messenger">The bus the gaze samples arrive on and the zone messages go out on.</param>
    /// <param name="presenter">The button renderer (the WPF window at runtime, a fake in tests).</param>
    /// <param name="probe">The dispatch probe the marks quote.</param>
    /// <param name="isInstrumentOn">The live <c>MeasureUiInstruments</c> reading.</param>
    /// <param name="isSettingsOpen">Whether Settings is open; its window covers the button.</param>
    /// <param name="isProbeOpen">Whether the sub-pixel probe window is up; it then owns the screen
    /// (Task 6, mirroring the restart card's probe seam).</param>
    /// <param name="now">The wall clock, a seam for tests.</param>
    /// <param name="postToUi">Runs work on the UI thread.</param>
    /// <param name="log">Writes one line to the session log.</param>
    public LagMarkHost(
        IMessenger messenger,
        ILagMarkPresenter presenter,
        DispatchLatencyProbe probe,
        Func<bool> isInstrumentOn,
        Func<bool> isSettingsOpen,
        Func<bool> isProbeOpen,
        Func<DateTime> now,
        Action<Action> postToUi,
        Action<string> log)
    {
        _messenger = messenger;
        _presenter = presenter;
        _probe = probe;
        _isInstrumentOn = isInstrumentOn;
        _isSettingsOpen = isSettingsOpen;
        _isProbeOpen = isProbeOpen;
        _now = now;
        _postToUi = postToUi;
        _log = log;
    }

    /// <summary>Marks logged this session; <c>k</c> in the mark lines counts from 1.</summary>
    public int MarkCount { get; private set; }

    /// <summary>
    /// Shows the button (when the instrument is on) and subscribes for gaze and marks. No pause
    /// input by design: the T0(a) launcher starts the app <c>--paused</c> and the button must exist
    /// and be shown then, so the owner can see it before resuming.
    /// </summary>
    public void Start()
    {
        _presenter.Marked += OnMarked;
        _presenter.Moved += OnMoved;
        _messenger.Register<GazeCursorMovedMessage>(this, (_, message) => OnGaze(message));

        if (CanShow())
            ShowButtonAndZone();
    }

    /// <summary>
    /// Settings opened or closed. Opening takes the button down — the window covers it, and its hit
    /// area would stay live underneath, invisible — and closing brings it back (the button is a
    /// session fixture, unlike the restart card).
    /// </summary>
    /// <param name="open">True when a Settings window has opened.</param>
    public void OnSettingsOpenChanged(bool open)
    {
        if (open)
            _postToUi(HideButtonAndZone);
        else if (CanShow())
            _postToUi(ShowButtonAndZone);
    }

    /// <summary>
    /// The sub-pixel probe opened or closed (Task 6, the restart card's seam): the full-screen probe
    /// covers the button, whose hit area would otherwise stay live on its corner — and its own Start
    /// overlaps nothing there, so a mark would advance nothing but would still be logged. Opening
    /// takes the button down; closing brings it back, because the button is a session fixture (unlike
    /// the restart card): the re-show happens when BOTH Settings and the probe are closed.
    /// </summary>
    /// <param name="open">True when the probe window has opened. The current flag is read as well:
    /// <c>ModalInteractionState.ProbeOpenChanged</c>'s contract says the argument can be stale.</param>
    public void OnProbeOpenChanged(bool open)
    {
        if (open || _isProbeOpen())
            _postToUi(HideButtonAndZone);
        else if (CanShow())
            _postToUi(ShowButtonAndZone);
    }

    /// <summary>The surfaces that own the screen when open, and the instrument's live gate.</summary>
    private bool CanShow() => _isInstrumentOn() && !_isSettingsOpen() && !_isProbeOpen();

    private void OnGaze(GazeCursorMovedMessage message)
    {
        if (!_presenter.IsShowing) return;

        // The live gate, read per sample like the adapter's: switched OFF mid-session hides the
        // button and stops marking. (OFF→ON deliberately does nothing, per the probe's rule.)
        if (!_isInstrumentOn())
        {
            HideButtonAndZone();
            return;
        }

        // A held (gap) sample must not accumulate dwell; and no gaze reaches the button under an
        // open Settings window or the probe. Paused does NOT gate this: a mark injects no input, and
        // the launcher starts the app paused.
        if (!message.IsInGap && !_isSettingsOpen() && !_isProbeOpen())
            _presenter.OnGaze(message.Position, message.TimestampMicroseconds);
    }

    private void ShowButtonAndZone()
    {
        _presenter.ShowButton();

        // Where it is, for the launcher's "Lag mark button shown" check (60 s after instruments ON).
        var rect = _presenter.ShownRectDip;
        _log(string.Create(
            CultureInfo.InvariantCulture,
            $"Lag mark button shown at {rect.Left:0},{rect.Top:0} {rect.Width:0}×{rect.Height:0} DIP (scale {DpiHelper.ScaleX:0.##})"));

        _messenger.Send(new LagMarkZoneMessage(_presenter.HitBounds));
    }

    private void HideButtonAndZone()
    {
        _presenter.HideButton();
        _messenger.Send(new LagMarkZoneMessage(null));
    }

    // The button moved itself for chrome (review 01 F1): the zone the offer engine avoids must
    // follow, or a fixation on the button's new place could raise an offer and a taken offer would
    // click whatever lies under the click-through button.
    private void OnMoved()
    {
        if (_presenter.IsShowing)
            _messenger.Send(new LagMarkZoneMessage(_presenter.HitBounds));
    }

    // Post-effect: the dwell has completed and the button already shows Marked. The logged `at` is
    // the completing SAMPLE's gaze moment: its dispatch wait is the most recent execute's wait
    // (LastLatencyMs — the completing sample IS that execute), so the wall clock is now minus that
    // wait. Before any execute the wait is 0 and the mark is immediate — the truthful reading, not
    // a guess.
    //
    // The WINDOW ends at the execute instant instead (review 01, lens 2; owner erratum E1 to
    // spec §2.3 / M1, approved 2026-10-16): the ring is keyed by execute time, and a freeze the
    // owner dwells THROUGH completes the dwell from the drained backlog the moment the freeze
    // clears — the completing sample was enqueued inside the freeze, so an enqueue-ended window cut
    // off the freeze's own long waits (measured: max 2.0 ms against 1000.1 ms). Spanning 15 s PLUS
    // the completing sample's wait covers (at − 15 s, now]: nothing the signed reading covered is
    // lost, and the drain at the mark is included. `at` keeps the gaze moment, so C3's coverage and
    // the startup exclusion are unchanged, and the line's format is unchanged (the readout parses
    // it). `coverageMs` can therefore read up to 15000 + the completing sample's wait (review 02,
    // R4); no rule reads it, and the readout only prints it.
    private void OnMarked(long timestampMicroseconds)
    {
        if (!_isInstrumentOn()) return;

        MarkCount++;

        var summary = _probe.Summarize(
            Stopwatch.GetTimestamp(), Window + TimeSpan.FromMilliseconds(_probe.LastLatencyMs));
        var at = _now().AddMilliseconds(-_probe.LastLatencyMs);

        _log(string.Create(
            CultureInfo.InvariantCulture,
            $"LAG MARK #{MarkCount} at {at:yyyy-MM-dd HH:mm:ss.fff} — last 15 s: {DispatchLatencyProbe.DescribeWindow(summary)}"));
    }

    /// <summary>Unregisters from the messenger and takes the button (and its zone) down.</summary>
    public void Dispose()
    {
        _messenger.UnregisterAll(this);
        _presenter.Marked -= OnMarked;
        _presenter.Moved -= OnMoved;
        HideButtonAndZone();
    }
}
