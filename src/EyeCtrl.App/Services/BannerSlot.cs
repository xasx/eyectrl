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
        banner.Closed -= OnClosed;
        banner.HideFromSlot();
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

    private void ShowNextIfFree()
    {
        // A loop, because an occupant may close synchronously inside ShowInSlot (nothing to show):
        // the next queued banner then takes the slot in the same turn instead of waiting forever.
        while (_live is null && _queue.Count > 0)
        {
            var next = _queue[0];
            _queue.RemoveAt(0);
            Show(next);
        }
    }

    private void Show(IBannerSlotOccupant banner)
    {
        _live = banner;
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
        banner.Closed -= OnClosed;
        ShowNextIfFree();
    }
}
