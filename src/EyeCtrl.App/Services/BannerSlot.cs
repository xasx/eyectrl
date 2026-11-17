namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Models;
using Serilog;

/// <summary>
/// The one bottom-centre banner slot (B4): exactly one banner is on screen at a time, and the
/// occupants queue in FIFO order — the next shows when the live one closes. A caregiver alert
/// PRE-EMPTS: it takes the slot at once, and the banner it displaced goes back to the FRONT of the
/// queue, so it is resumed rather than lost. Future occupants (B5's status cards) join through the
/// same <see cref="IBannerSlotOccupant"/> API.
///
/// <para>
/// The slot owns WHEN a banner may be on screen, never how it looks: an occupant shows and hides
/// itself and raises <see cref="IBannerSlotOccupant.Closed"/> when it has left the screen (its own
/// hold/fade ended, or the slot took it down). The live occupant's bounds are the note gate's ONE
/// aggregate occluder source, and the restart notice keeps clear of them as well as the chrome.
/// </para>
///
/// <para>
/// UI thread only: every caller is the dispatcher (the two banner presenters and the adaptation
/// subscribers that drive them), and no member is thread-safe.
/// </para>
/// </summary>
public sealed class BannerSlot : IBannerSlot
{
    private readonly List<IBannerSlotOccupant> _queue = new();
    private IBannerSlotOccupant? _live;

    /// <summary>The banner on screen, or null when the slot is free. For tests and diagnostics.</summary>
    internal IBannerSlotOccupant? Live => _live;

    /// <inheritdoc />
    public void Enqueue(IBannerSlotOccupant banner)
    {
        ArgumentNullException.ThrowIfNull(banner);

        if (ReferenceEquals(banner, _live))
        {
            // Re-showing the live banner refreshes it (a later message on the same surface); it is
            // never queued behind itself.
            banner.ShowInSlot();
            return;
        }

        if (_queue.Contains(banner))
            return; // already waiting: the queue holds occupants, not requests

        _queue.Add(banner);
        ShowNextIfFree();
    }

    /// <inheritdoc />
    public void Preempt(IBannerSlotOccupant banner)
    {
        ArgumentNullException.ThrowIfNull(banner);

        if (ReferenceEquals(banner, _live))
        {
            // The same surface with a newer condition: refresh it.
            banner.ShowInSlot();
            return;
        }

        _queue.Remove(banner); // it is taking the slot now, not later

        if (_live is { } displaced)
        {
            // The pre-emptor becomes live BEFORE the displaced banner is hidden, so the Closed that
            // HideFromSlot raises cannot advance the queue out from under the pre-emptor.
            _live = banner;
            try
            {
                displaced.HideFromSlot();
            }
            catch (Exception ex)
            {
                // Exception safety on the safety channel (B4 fixup-01 L1): a faulting displaced
                // banner must never stop the caregiver alert from taking the slot.
                Log.Error(ex, "A pre-empted banner failed to hide; the pre-emptor still takes the slot.");
            }
            _queue.Insert(0, displaced);
        }

        Show(banner);
        _livePreempted = ReferenceEquals(_live, banner);   // the safety channel is up (Show may have failed)
    }

    /// <inheritdoc />
    public void Withdraw(IBannerSlotOccupant banner)
    {
        ArgumentNullException.ThrowIfNull(banner);

        if (!ReferenceEquals(banner, _live))
        {
            _queue.Remove(banner); // it was only waiting: dropping it is enough
            return;
        }

        _live = null;
        _livePreempted = false;    // the slot took the pre-emptor down: the marker dies with it
        banner.Closed -= OnClosed;
        banner.HideFromSlot();
        ShowNextIfFree();
    }

    // The guide's hold (spec §5 claim 4): while held, the live banner hides and re-queues at the
    // FRONT (it resumes first) and dequeue pauses — nothing is dropped, `_live` cannot wedge
    // because the hold clears it. Preempt deliberately bypasses the hold: caregiver alerts are
    // the safety channel and take the slot even mid-guide (pinned by test) — and a pre-emptor
    // that is ALREADY live stays live through a later Hold (it is never demoted into the queue;
    // the marker clears when it closes or the slot withdraws it). UI thread only.
    private bool _held;

    /// <summary>Whether the live occupant took the slot via <see cref="Preempt"/> — the safety
    /// channel stays up through a hold. Cleared by every non-preempting show, a natural close and
    /// a slot-driven withdrawal.</summary>
    private bool _livePreempted;

    /// <summary>Hold for a fullscreen surface (the first-run guide). Idempotent. A live
    /// pre-emptor is left up (S3: Hold() used to demote it into the queue, contradicting the
    /// documented bypass).</summary>
    public void Hold()
    {
        if (_held) return;
        _held = true;
        if (_live is { } live && !_livePreempted)
        {
            _live = null;
            live.Closed -= OnClosed;
            try
            {
                live.HideFromSlot();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "A held banner failed to hide; it still re-queues.");
            }
            _queue.Insert(0, live);
        }
    }

    /// <summary>Release: dequeue resumes at once with the front of the queue (nothing dropped).
    /// A live pre-emptor is untouched.</summary>
    public void Release()
    {
        if (!_held) return;
        _held = false;
        ShowNextIfFree();
    }

    /// <inheritdoc />
    public bool IsLive(IBannerSlotOccupant banner) => ReferenceEquals(banner, _live);

    /// <inheritdoc />
    public bool TryGetVisibleBounds(out PhysicalRect bounds)
    {
        bounds = default;
        return _live is { } live && live.TryGetVisibleBounds(out bounds);
    }

    /// <inheritdoc />
    public IReadOnlyList<PhysicalRect> LiveBoundsOrEmpty() =>
        TryGetVisibleBounds(out var bounds) ? new[] { bounds } : Array.Empty<PhysicalRect>();

    /// <inheritdoc />
    public void ReassertForOrder() => _live?.ReassertForOrder();

    private void ShowNextIfFree()
    {
        // A loop, because an occupant may close synchronously inside ShowInSlot (nothing to show):
        // the next queued banner then takes the slot in the same turn instead of waiting forever.
        while (!_held && _live is null && _queue.Count > 0)
        {
            var next = _queue[0];
            _queue.RemoveAt(0);
            Show(next);
        }
    }

    private void Show(IBannerSlotOccupant banner)
    {
        _live = banner;
        _livePreempted = false;   // a queue advance is not a pre-emption (Preempt re-marks after)
        banner.Closed -= OnClosed;
        banner.Closed += OnClosed;
        try
        {
            banner.ShowInSlot();
        }
        catch (Exception ex)
        {
            // Exception safety (B4 fixup-01 L1): a banner that cannot show must not stall the slot
            // with an invisible live occupant — drop it and move on to the next queued banner.
            Log.Error(ex, "A banner failed to show; the slot drops it and moves on to the next queued banner.");
            _live = null;
            banner.Closed -= OnClosed;
            ShowNextIfFree();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (sender is not IBannerSlotOccupant banner || !ReferenceEquals(banner, _live))
            return; // a stale close from a banner that is no longer live

        _live = null;
        _livePreempted = false;   // a live pre-emptor that closed naturally no longer bypasses a hold
        banner.Closed -= OnClosed;
        ShowNextIfFree();
    }
}
