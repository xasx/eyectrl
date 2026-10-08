namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.ViewModels.Messages;

/// <summary>What a settings request should do to the (possibly open) window.</summary>
internal enum SettingsDeepLinkAction
{
    /// <summary>Nothing: a Default request with the window already open.</summary>
    None,

    /// <summary>Open a fresh window on its default tab.</summary>
    OpenOnly,

    /// <summary>Open a fresh window, then navigate to the language row.</summary>
    OpenThenNavigate,

    /// <summary>Navigate the already-open window (the window queues behind a modal, if one is up).</summary>
    NavigateOpen
}

/// <summary>
/// The deep link's whole decision, pure: from (is the window open, what did the request ask for)
/// to what <c>SettingsUiCoordinator</c> should do. Extracted from the coordinator so all four
/// states are unit-testable without constructing it (it needs the full coordination context).
/// </summary>
/// <remarks>
/// Two rules are easy to break and are why this exists: a Default request must never yank an
/// already-open window anywhere (the toolbar's Settings button), and a GeneralPage1 request to a
/// fresh window must actually navigate (the tray's 0-dwell reach). The modal case is the window's
/// <see cref="SettingsDeepLinkQueue"/>: any navigate action is queued there while a modal is up.
/// </remarks>
internal static class SettingsDeepLink
{
    /// <summary>Decides what to do with <paramref name="target"/> given the window state.</summary>
    /// <param name="windowOpen">Whether a settings window is already open.</param>
    /// <param name="target">What the request asked for.</param>
    /// <returns>The action to take.</returns>
    internal static SettingsDeepLinkAction Decide(bool windowOpen, SettingsTarget target) =>
        (windowOpen, target) switch
        {
            (true, SettingsTarget.GeneralPage1) => SettingsDeepLinkAction.NavigateOpen,
            (true, _) => SettingsDeepLinkAction.None,
            (false, SettingsTarget.GeneralPage1) => SettingsDeepLinkAction.OpenThenNavigate,
            (false, _) => SettingsDeepLinkAction.OpenOnly
        };

    /// <summary>
    /// Decides and runs, so no caller branches on the action. The open delegate runs first when the
    /// plan asks for it, then the navigate delegate — which is why the fresh-window navigation
    /// lands after construction without its own guard.
    /// </summary>
    /// <param name="windowOpen">Whether a settings window is already open.</param>
    /// <param name="target">What the request asked for.</param>
    /// <param name="openWindow">Opens a fresh window (and does nothing else).</param>
    /// <param name="navigate">Navigates the (now-open) window to the language row.</param>
    internal static void Dispatch(
        bool windowOpen, SettingsTarget target, Action openWindow, Action navigate) =>
        SettingsDispatchPlan.Execute(
            SettingsDispatchPlan.For(Decide(windowOpen, target)), openWindow, navigate);
}

/// <summary>
/// The mechanical effect of a decided action, as data: open a window, navigate, both, or neither.
/// Split from the decision so the dispatch has no branch of its own to mutate — the coordinator
/// hands the plan to <see cref="Execute"/> and nothing else.
/// </summary>
/// <param name="OpenWindow">Whether to open a fresh window first.</param>
/// <param name="Navigate">Whether to navigate to the language row (after the open, when both).</param>
internal sealed record SettingsDispatchPlan(bool OpenWindow, bool Navigate)
{
    /// <summary>The plan for an action, exactly one entry per action.</summary>
    /// <param name="action">The decided action.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The action is not defined.</exception>
    internal static SettingsDispatchPlan For(SettingsDeepLinkAction action) => action switch
    {
        SettingsDeepLinkAction.None => new(OpenWindow: false, Navigate: false),
        SettingsDeepLinkAction.OpenOnly => new(OpenWindow: true, Navigate: false),
        SettingsDeepLinkAction.OpenThenNavigate => new(OpenWindow: true, Navigate: true),
        SettingsDeepLinkAction.NavigateOpen => new(OpenWindow: false, Navigate: true),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
    };

    /// <summary>Runs the plan in order: open first (when asked), then navigate.</summary>
    /// <param name="plan">The plan to run.</param>
    /// <param name="openWindow">Opens a fresh window.</param>
    /// <param name="navigate">Navigates the (now-open) window.</param>
    internal static void Execute(SettingsDispatchPlan plan, Action openWindow, Action navigate)
    {
        if (plan.OpenWindow) openWindow();
        if (plan.Navigate) navigate();
    }
}
