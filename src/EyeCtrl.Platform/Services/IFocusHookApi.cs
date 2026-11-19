namespace EyeCtrl.Platform.Services;

using System.Windows.Automation;

/// <summary>
/// The two UI-Automation focus-event calls <see cref="UiaSensitiveContextDetector"/> needs, behind a
/// seam so the threading contract is testable without a real system-wide subscription. The real
/// implementation is <see cref="AutomationFocusHookApi"/>; the detector must call both members on
/// its own MTA thread (Microsoft: add on a non-UI thread, remove on the same thread).
/// </summary>
public interface IFocusHookApi
{
    /// <summary>Subscribes <paramref name="handler"/> to system-wide focus-changed events.</summary>
    /// <param name="handler">The handler to subscribe; the same delegate instance is removed later.</param>
    void Add(AutomationFocusChangedEventHandler handler);

    /// <summary>Unsubscribes <paramref name="handler"/> from system-wide focus-changed events.</summary>
    /// <param name="handler">The instance previously passed to <see cref="Add"/>.</param>
    void Remove(AutomationFocusChangedEventHandler handler);
}

/// <summary>The production <see cref="IFocusHookApi"/>: straight delegation to <see cref="Automation"/>.</summary>
public sealed class AutomationFocusHookApi : IFocusHookApi
{
    /// <inheritdoc />
    public void Add(AutomationFocusChangedEventHandler handler) =>
        Automation.AddAutomationFocusChangedEventHandler(handler);

    /// <inheritdoc />
    public void Remove(AutomationFocusChangedEventHandler handler) =>
        Automation.RemoveAutomationFocusChangedEventHandler(handler);
}
