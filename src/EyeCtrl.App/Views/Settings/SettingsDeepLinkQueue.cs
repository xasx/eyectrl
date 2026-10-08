namespace EyeCtrl.App.Views.Settings;

/// <summary>
/// A one-slot pending deep link: a request that arrives while a modal is up is remembered and
/// applied when the modal closes — the open-modal case of T04's three window states. The modal is
/// never discarded to satisfy the link, and the link is never silently dropped: a user who aimed
/// at the tray item cannot be told to try again without reading, which is exactly the situation
/// the language item exists for.
/// </summary>
/// <remarks>
/// Extracted from <see cref="SettingsWindow"/> so the queueing is headlessly testable — the window
/// needs a display and eight dependencies to construct.
/// </remarks>
internal sealed class SettingsDeepLinkQueue
{
    private bool _pending;

    /// <summary>Whether a request is waiting for a modal to close.</summary>
    internal bool IsPending => _pending;

    /// <summary>
    /// Records a request. Returns true when it may be applied immediately; false when a modal is
    /// up, in which case <see cref="TryApply"/> reports true once that modal closes.
    /// </summary>
    /// <param name="modalVisible">Whether a modal is currently up.</param>
    /// <returns>Whether the caller should apply the navigation now.</returns>
    internal bool Request(bool modalVisible)
    {
        if (modalVisible)
        {
            _pending = true;
            return false;
        }

        _pending = false;
        return true;
    }

    /// <summary>
    /// Whether a queued request should run now. Consumes it: a second call without a new
    /// <see cref="Request"/> returns false.
    /// </summary>
    /// <param name="modalVisible">Whether a modal is currently up.</param>
    /// <returns>True exactly once per queued request.</returns>
    internal bool TryApply(bool modalVisible)
    {
        if (!_pending || modalVisible) return false;

        _pending = false;
        return true;
    }
}
