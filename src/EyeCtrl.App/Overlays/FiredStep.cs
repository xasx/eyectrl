namespace EyeCtrl.App.Overlays;

using System;
using System.Windows.Threading;
using EyeCtrl.Controls;

/// <summary>
/// The keyboard's "fired" step (Wave D ticket F18), the sibling of <see cref="FiredFlash"/>: when
/// a key or a suggestion slot fires, its OWN <see cref="DwellOutlineOverlay"/> makes one brief
/// weight step and then returns to rest. It exists because the click is the only other per-key
/// confirmation, and clicks have come late or stopped mid-typing; a key whose outline simply
/// vanished is indistinguishable from an abandoned dwell.
/// <para>
/// The step is a thickness write on the two rectangles the target already owns
/// (<see cref="DwellOutlineOverlay.SetFired"/>), so it adds no visual tree and no layout pass, and
/// keeps the outline's own colours: never a recolour, never a fill. It starts from the fire event
/// (the click's own place), never from the per-sample gaze path.
/// </para>
/// <para>
/// The end reuses C1's mechanism and constants: a UI-thread <see cref="DispatcherTimer"/> at
/// <see cref="FiredFlash.EndPriority"/> with <see cref="FiredFlash.DurationMs"/>, so the same
/// number and the same queueing rule are shared, not restated. <see cref="End"/> is the test seam
/// too — a pumpless STA test never ticks a timer. One step is lit at a time: a newer fire takes it
/// from an older one, so each fire lights exactly one target and can never leave two lit.
/// </para>
/// </summary>
internal sealed class FiredStep
{
    /// <summary>The C1 duration, shared rather than restated: both fired cues last the same.</summary>
    internal const int DurationMs = FiredFlash.DurationMs;

    /// <summary>The C1 end-timer priority, shared: the tick stays behind the frame that drew the step.</summary>
    internal const DispatcherPriority EndPriority = FiredFlash.EndPriority;

    private readonly DispatcherTimer _timer;
    private DwellOutlineOverlay? _lit;

    internal FiredStep(Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(EndPriority, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(DurationMs),
        };
        _timer.Tick += (_, _) => End();
    }

    /// <summary>True while a step is showing.</summary>
    internal bool IsLit => _lit is not null;

    /// <summary>The stepped outline, or null at rest (tests read which target took the step).</summary>
    internal DwellOutlineOverlay? LitOutline => _lit;

    /// <summary>True while the end is scheduled; false at rest.</summary>
    internal bool IsEndScheduled => _timer.IsEnabled;

    /// <summary>The end timer itself, for tests that read its priority (DispatcherTimer has no public getter).</summary>
    internal DispatcherTimer Timer => _timer;

    /// <summary>The scheduled length of the step.</summary>
    internal TimeSpan Interval => _timer.Interval;

    /// <summary>
    /// Lights the step on <paramref name="outline"/> and (re)schedules its end. A null outline
    /// (a fire whose target is no longer in the grid) ends the previous step and lights nothing.
    /// A second fire during a live step ends the first at once and takes the step: the newest
    /// fire is the one being confirmed, and the older target's step has already served.
    /// </summary>
    internal void Start(DwellOutlineOverlay? outline)
    {
        End();
        if (outline is null) return;

        _lit = outline;
        outline.SetFired(true);
        _timer.Start();
    }

    /// <summary>Returns the lit outline to its rest look at once. Idempotent.</summary>
    internal void End()
    {
        _timer.Stop();
        _lit?.SetFired(false);
        _lit = null;
    }
}
