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
}
