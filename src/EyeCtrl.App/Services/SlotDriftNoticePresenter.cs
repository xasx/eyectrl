namespace EyeCtrl.App.Services;

using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Models;

/// <summary>
/// The drift notice as a bottom-centre slot occupant (B4). The service's
/// <see cref="ShowDriftNotice"/> ENQUEUES, so the notice is never on screen outside the one banner
/// slot: it waits behind a live banner and returns — at the front of the queue — after a caregiver
/// alert pre-empts it. The window still owns the banner's look, its 8 s hold and its fade; the slot
/// owns when it may show.
/// </summary>
internal sealed class SlotDriftNoticePresenter : IDriftNoticePresenter, IBannerSlotOccupant
{
    private readonly IBannerSlot _slot;
    private readonly DriftNoticeWindow _window;

    /// <summary>Wraps the real notice window for the slot.</summary>
    /// <param name="slot">The one bottom-centre slot this banner shows through.</param>
    /// <param name="window">The notice window (a DI singleton).</param>
    public SlotDriftNoticePresenter(IBannerSlot slot, DriftNoticeWindow window)
    {
        _slot = slot;
        _window = window;
        _window.Hidden += OnHidden;
    }

    /// <inheritdoc />
    public event EventHandler? Closed;

    /// <inheritdoc />
    public void ShowDriftNotice() => _slot.Enqueue(this);

    /// <inheritdoc />
    public void ShowInSlot() => _window.ShowDriftNotice();

    /// <inheritdoc />
    public void HideFromSlot() => _window.HideDriftNotice();

    /// <inheritdoc />
    public bool TryGetVisibleBounds(out PhysicalRect bounds) => _window.TryGetVisibleBounds(out bounds);

    private void OnHidden(object? sender, EventArgs e) => Closed?.Invoke(this, EventArgs.Empty);
}
