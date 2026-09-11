namespace EyeCtrl.Core.Diagnostics;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// An <see cref="IGazeProvider"/> that wraps another one. Exists so code that must reason about the
/// REAL provider — <c>GazeProviderFactory.GetProviderType</c>, which the toolbar uses to show which
/// engine is active — can unwrap without knowing any particular decorator: a wrapped Tobii provider
/// would otherwise be reported as Mouse, telling the user their eye tracker is not in use.
/// </summary>
public interface IGazeProviderDecorator : IGazeProvider
{
    /// <summary>The wrapped provider. Never null; may itself be a decorator.</summary>
    IGazeProvider Inner { get; }
}

/// <summary>
/// The side-channel a recording needs to be replayable: everything about the session that is NOT a
/// gaze sample, captured once at the head of the file.
/// <para>
/// Replay reconstructs <c>isInGap</c> and the filtered point from raw samples alone, but only if it
/// knows the pipeline's inputs — hence the screen size (raw samples are in tracker space and the
/// provider scales by it), the calibration state (a transform is applied to every gaze point, so a
/// recording made with one is not comparable to one made without) and a settings version the
/// analyzer can join adaptation changes against.
/// </para>
/// <para>
/// Privacy: this is the complete list of context ever written. No window titles, no target ids, no
/// application names — a recording says where its owner looked and when, and nothing about what they
/// were looking at.
/// </para>
/// </summary>
/// <param name="ProviderType">Which gaze provider produced the samples, e.g. <c>StreamEngine</c>.</param>
/// <param name="ScreenWidthPhysical">Screen width in physical pixels at record time.</param>
/// <param name="ScreenHeightPhysical">Screen height in physical pixels at record time.</param>
/// <param name="CalibrationActive">Whether an EyeCtrl calibration transform was applied during the session.</param>
/// <param name="CalibrationVersion">
/// Identifies WHICH calibration was active — the persisted calibration's UTC timestamp in ISO-8601,
/// or empty when none. Two recordings with different values are not comparable.
/// </param>
/// <param name="SettingsVersion">
/// Monotonic counter bumped whenever the settings or adaptation state that shapes the pipeline
/// changes, so the analyzer can join a recording against the configuration in force.
/// </param>
public readonly record struct GazeRecordingHeader(
    string ProviderType,
    double ScreenWidthPhysical,
    double ScreenHeightPhysical,
    bool CalibrationActive,
    string CalibrationVersion,
    long SettingsVersion);

/// <summary>
/// Destination for recorded raw samples. Implementations are called from the gaze thread and MUST
/// NOT throw, block, or allocate per sample — see <see cref="GazeRecorder"/> for the shipped one.
/// </summary>
public interface IGazeRecordingSink : IDisposable
{
    /// <summary>Records one raw sample. Called on the gaze thread; must be non-blocking and must not throw.</summary>
    /// <param name="sample">The raw sample exactly as the provider produced it.</param>
    void Record(in GazePoint sample);

    /// <summary>
    /// Asks the sink to persist what it holds, without waiting for it. Called from
    /// <see cref="IGazeProvider.Stop"/>, which must not be delayed.
    /// </summary>
    void RequestFlush();

    /// <summary><c>false</c> once the sink has self-disabled after a fault; it then does nothing.</summary>
    bool IsRecording { get; }
}
