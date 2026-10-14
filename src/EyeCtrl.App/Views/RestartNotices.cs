namespace EyeCtrl.App.Views;

using System.Collections.Generic;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.ViewModels;

/// <summary>
/// The restart flow's crisis strings (D5, surface <c>language-recovery</c>): the "restart is under
/// way" notice and the refusal notice's acknowledgement button, resolved in the TARGET language —
/// the saved selection resolved against the machine culture, i.e. the language the replacement
/// will actually run. Extracted from <see cref="SettingsWindow"/>'s messenger handlers so the
/// English text and the German path are headlessly pinnable — the window itself cannot be
/// constructed without a display and its eight dependencies.
/// </summary>
/// <remarks>
/// The refusal's MESSAGE is <see cref="EyeCtrl.ViewModels.Messages.RestartUnavailableMessage.Reason"/>,
/// which arrives already localized in the same target (<c>SelfRestartStrategy.Describe</c> or the
/// coordinator's failure frame). Only the button lives here.
/// </remarks>
internal static class RestartNotices
{
    /// <summary>
    /// The language the restart flow speaks (F1, D5): the SAVED selection resolved against the
    /// machine culture, the language the replacement will actually run. An ordinary restart (no
    /// pending change) resolves to the running language, so its notices are unchanged. One rule for
    /// the Settings window's notices and the App-level host's.
    /// </summary>
    /// <param name="savedSelection">The saved <c>General.UiLanguage</c>.</param>
    /// <returns>The language the notices and failure reasons speak.</returns>
    internal static UiLanguage TargetLanguage(UiLanguage savedSelection) =>
        StringTable.ResolveLanguage(savedSelection, StringTable.Current.Culture);

    /// <summary>The "restart accepted, waiting for the successor" modal body.</summary>
    /// <param name="target">The language the whole restart flow speaks.</param>
    /// <returns>The resolved text.</returns>
    internal static string InProgressMessage(UiLanguage target) =>
        StringTable.Current.T(target, "language-recovery.restart.in_progress.message");

    /// <summary>The in-progress notice's single choice: hide the notice, never cancel.</summary>
    /// <param name="target">The language the whole restart flow speaks.</param>
    /// <returns>One neutral choice.</returns>
    internal static IReadOnlyList<ModalChoice> InProgressChoices(UiLanguage target) =>
    [
        new ModalChoice(
            "restart_progress_hide",
            StringTable.Current.T(target, "language-recovery.restart.in_progress.hide"),
            ModalChoiceKind.Neutral,
            null)
    ];

    /// <summary>The refusal notice's single acknowledgement choice.</summary>
    /// <param name="target">The language the whole restart flow speaks.</param>
    /// <returns>One affirmative choice.</returns>
    internal static IReadOnlyList<ModalChoice> RefusedChoices(UiLanguage target) =>
    [
        new ModalChoice(
            "restart_failed_ok",
            StringTable.Current.T(target, "language-recovery.restart.refused.ok"),
            ModalChoiceKind.Affirmative,
            null)
    ];
}
