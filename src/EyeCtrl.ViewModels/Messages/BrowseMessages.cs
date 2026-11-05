namespace EyeCtrl.ViewModels.Messages;

/// <summary>
/// Sent by <see cref="EyeCtrl.ViewModels.BrowseViewModel"/> whenever the browse surface's dwell
/// target set changes structurally — a page snapshot was reconciled (elements added, moved past
/// the reset threshold, removed), the chrome row was (re)registered, web targets were cleared for
/// a navigation, or the window hid and cleared everything. The D17 gaze-consumer inventory re-runs
/// the preview panel's collision re-check on it (the <c>ExternalDwellTargetsChangedMessage</c>
/// precedent, GazeMessages.cs): a page adding or moving targets under a visible panel must be
/// re-checked before gaze is already dwelling the new target.
/// Dispatcher-marshalled like its external-dwell twin, which the pipe threads send from off the
/// UI thread; the browse surface's sends arrive from the WebView2 message handler the same way.
/// Empty payload by design: the inventory re-reads the live rects.
/// </summary>
public record BrowseTargetsChangedMessage;
