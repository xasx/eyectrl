namespace EyeCtrl.App.Services;

using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Models;

/// <summary>
/// The drift notice as a bottom-centre slot occupant (B4). The service's
/// <see cref="ShowDriftNotice"/> ENQUEUES, so the notice is never on screen outside the one banner
/// slot: it waits behind a live banner and returns — at the front of the queue — after a caregiver
/// alert pre-empts it. The window still owns the banner's look, its 8 s hold and its fade; the slot
/// owns when it may show.
///
/// <para>
/// A queued notice may wait an unbounded time (a caregiver alert stays up as long as its condition
/// is sustained), so resuming it RE-VALIDATES through <c>canShow</c> (B4 fixup-01 H1): feedback not
/// suppressed, the adaptation runtime still running, and the drift episode still current. A notice
/// that fails any of those is dropped — never popped late in a sensitive context, after the disable,
/// or minutes after the drift cleared (§8.10).
/// </para>
/// </summary>
internal sealed class SlotDriftNoticePresenter : IDriftNoticePresenter, IBannerSlotOccupant
{
    private readonly IBannerSlot _slot;
    private readonly DriftNoticeWindow _window;
    private readonly Func<bool> _canShow;

    /// <summary>Wraps the real notice window for the slot.</summary>
    /// <param name="slot">The one bottom-centre slot this banner shows through.</param>
    /// <param name="window">The notice window (a DI singleton).</param>
    /// <param name="canShow">Whether the notice may still be shown when the slot reaches it — the
    /// service's live state (not suppressed, runtime running, episode current).</param>
    public SlotDriftNoticePresenter(IBannerSlot slot, DriftNoticeWindow window, Func<bool> canShow)
    {
        _slot = slot;
        _window = window;
        _canShow = canShow;
        _window.Hidden += OnHidden;
    }

    /// <inheritdoc />
    public event EventHandler? Closed;

    /// <inheritdoc />
    public void ShowDriftNotice() => _slot.Enqueue(this);

    /// <inheritdoc />
    public void WithdrawDriftNotice() => _slot.Withdraw(this);

    /// <inheritdoc />
    public void ShowInSlot()
    {
        if (!_canShow())
        {
            // The episode ended, feedback is suppressed, or the runtime is off: drop the queued
            // notice. Raising Closed lets the slot's loop move on to the next banner.
            Closed?.Invoke(this, EventArgs.Empty);
            return;
        }
        _window.ShowDriftNotice();
    }

    /// <inheritdoc />
    public void HideFromSlot() => _window.HideDriftNotice();

    /// <inheritdoc />
    public bool TryGetVisibleBounds(out PhysicalRect bounds) => _window.TryGetVisibleBounds(out bounds);

    private void OnHidden(object? sender, EventArgs e) => Closed?.Invoke(this, EventArgs.Empty);
}
