namespace EyeCtrl.App.Services;

using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Models;

/// <summary>
/// The caregiver alert as a bottom-centre slot occupant (B4). It PRE-EMPTS: an alert takes the slot
/// at once, and the banner it displaced goes back to the front of the queue (it resumes after the
/// alert clears). The window still owns the banner's look, the chime, its condition-specific sizing
/// and its fade; the slot owns when it may show.
///
/// <para>
/// One window shows one condition at a time, so a newer onset on the same presenter is a REFRESH of
/// the live occupant (the slot re-invokes <see cref="ShowInSlot"/>), never a second queued alert.
/// </para>
/// </summary>
internal sealed class SlotCaregiverAlertPresenter : ICaregiverAlertPresenter, IBannerSlotOccupant
{
    private readonly IBannerSlot _slot;
    private readonly CaregiverAlertWindow _window;
    private CaregiverAlertCondition _condition;

    /// <summary>Wraps the real alert window for the slot.</summary>
    /// <param name="slot">The one bottom-centre slot this banner shows through.</param>
    /// <param name="window">The alert window (a DI singleton).</param>
    public SlotCaregiverAlertPresenter(IBannerSlot slot, CaregiverAlertWindow window)
    {
        _slot = slot;
        _window = window;
        _window.Hidden += OnHidden;
    }

    /// <inheritdoc />
    public event EventHandler? Closed;

    /// <inheritdoc />
    public void ShowCaregiverAlert(CaregiverAlertCondition condition)
    {
        _condition = condition;
        // At once, ahead of every queued banner; the live banner is taken down and returns later.
        _slot.Preempt(this);
    }

    /// <inheritdoc />
    public void FadeOutCaregiverAlert()
    {
        if (_slot.IsLive(this))
            _window.FadeOutCaregiverAlert(); // its Closed advances the slot once the fade has hidden it
        else
            _slot.Withdraw(this); // the condition cleared before the alert ever took the slot
    }

    /// <inheritdoc />
    public void HideCaregiverAlert()
    {
        if (_slot.IsLive(this))
        {
            _slot.Withdraw(this); // Withdraw hides through HideFromSlot, then shows the next banner
            return;
        }

        _slot.Withdraw(this); // drop it if it was only waiting
        _window.HideCaregiverAlert(); // not live: still make sure nothing is left on screen
    }

    /// <inheritdoc />
    public void ShowInSlot() => _window.ShowCaregiverAlert(_condition);

    /// <inheritdoc />
    public void HideFromSlot() => _window.HideCaregiverAlert();

    /// <inheritdoc />
    public bool TryGetVisibleBounds(out PhysicalRect bounds) => _window.TryGetVisibleBounds(out bounds);

    private void OnHidden(object? sender, EventArgs e) => Closed?.Invoke(this, EventArgs.Empty);
}
