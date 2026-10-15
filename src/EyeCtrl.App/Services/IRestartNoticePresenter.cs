namespace EyeCtrl.App.Services;

using System;
using EyeCtrl.Core.Models;

/// <summary>
/// Renders the restart notices when no Settings window is open to show them. Separate from
/// <see cref="RestartNoticeHost"/> so the host's when-and-what decisions are headless-testable with
/// a fake; the WPF <c>RestartNoticeWindow</c> is the runtime implementation.
/// </summary>
public interface IRestartNoticePresenter
{
    /// <summary>Whether a notice is on screen (gaze is only fed to it while it is).</summary>
    bool IsShowing { get; }

    /// <summary>
    /// Shows <paramref name="message"/> with one gaze-reachable close button, replacing any notice
    /// already up. The notice takes itself down after <paramref name="hold"/> whatever the gaze does.
    /// </summary>
    /// <param name="message">The notice text, already in the language the restart flow speaks.</param>
    /// <param name="closeLabel">The close button's label, in the same language.</param>
    /// <param name="hold">How long the notice stays when nobody dwells the button.</param>
    void ShowNotice(string message, string closeLabel, TimeSpan hold);

    /// <summary>Feeds one gaze sample to the close button's dwell.</summary>
    /// <param name="gaze">The gaze point in physical pixels.</param>
    /// <param name="timestampMicroseconds">The sample time.</param>
    void OnGaze(PhysicalPoint gaze, long timestampMicroseconds);

    /// <summary>Takes the notice down at once. No-op when none is showing.</summary>
    void HideNotice();
}
