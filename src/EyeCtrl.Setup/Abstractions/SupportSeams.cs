namespace EyeCtrl.Setup.Abstractions;

using EyeCtrl.Core.Services;

/// <summary>A clock with a sleep, so the pass rule's 30 s and 20 s windows are exact in tests.</summary>
public interface ISetupClock
{
    /// <summary>UTC now.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Waits; tests advance their fake time instead of sleeping.</summary>
    void Sleep(TimeSpan duration);
}

/// <summary>A small log seam. Lines are diagnostics only: never a character, a path outside the
/// install folders, or a user name.</summary>
public interface ISetupLog
{
    /// <summary>An informational line.</summary>
    void Info(string message);

    /// <summary>A warning line.</summary>
    void Warn(string message);

    /// <summary>An error line.</summary>
    void Error(string message);
}

/// <summary>The install-time settings Setup reads without changing: supervision on/off and
/// start-on-boot on/off.</summary>
public readonly record struct SupervisionBootState(bool SupervisionEnabled, bool StartOnBoot);

/// <summary>Reads the two switches from <c>%APPDATA%\EyeCtrl\settings.json</c>, tolerantly.</summary>
public interface ISettingsSnapshotReader
{
    /// <summary>Reads the current switches; missing or damaged means both off.</summary>
    SupervisionBootState Read();
}

/// <summary>
/// Reads C10's startup token line out of the App's daily log. The line is written by
/// <c>src/EyeCtrl.App/App.xaml.cs</c> from the shared contract
/// <see cref="EyeCtrl.Core.Input.AppStartupTokenLog"/>
/// into <c>%APPDATA%\EyeCtrl\logs\eyectrl-YYYYMMDD.log</c> (LoggingInitializer, rolling daily).
/// </summary>
public interface IAppLogProbe
{
    /// <summary>Reads the newest startup token line for this exe at/after the launch time.</summary>
    AppStartupTokenLine ReadStartupTokenLine(string appExePath, DateTimeOffset sinceUtc);
}

/// <summary>What the startup token line says, or that no line was found.</summary>
public enum AppStartupTokenLine
{
    None,
    UiAccessTrue,
    UiAccessFalse,
}

/// <summary>The Q8 choice seam. S6e supplies the gaze window; S6a's default keeps the data.</summary>
public enum UninstallChoice
{
    Keep,
    Delete,
    Cancel,
}

/// <summary>
/// O6a: which Q8 route produced a cancel. The record's <c>detail</c> stays <c>cancelled</c> for
/// all of them (the App and the wrapper read it); this names the route in the log only.
/// </summary>
public enum UninstallCancelPath
{
    /// <summary>Not a cancel, or the path is not known.</summary>
    None,

    /// <summary>The Cancel button on the first screen.</summary>
    Button,

    /// <summary>Screen 2's safe answer, "keep the data".</summary>
    KeepData,

    /// <summary>Esc.</summary>
    Escape,

    /// <summary>The window was closed (Alt+F4, its close path, or a foreign close).</summary>
    Close,

    /// <summary>The 5-minute no-choice timeout.</summary>
    Timeout,

    /// <summary>The window could not be shown at all.</summary>
    Unavailable,
}

/// <summary>The Q8 answer: the choice and, for a cancel, the route that produced it.</summary>
public readonly record struct UninstallChoiceAnswer(
    UninstallChoice Choice, UninstallCancelPath CancelPath = UninstallCancelPath.None);

/// <summary>Asks the owner whether the data stays. <c>--quiet</c> never calls this.</summary>
public interface IUninstallChoice
{
    /// <summary>Returns the owner's choice; S6a's default is <see cref="UninstallChoice.Keep"/>.</summary>
    UninstallChoiceAnswer Ask();
}

/// <summary>What one app-data deletion did: success, what stayed, and whether the folder existed
/// and was actually removed (review-01 F2: the log must not claim a removal when there was none).</summary>
public readonly record struct AppDataDeletionResult(
    bool Succeeded, IReadOnlyList<string> FailedFiles, bool Removed = false);

/// <summary>
/// The Q8 deletion seam: the ONLY way <c>%APPDATA%\EyeCtrl</c> may be deleted. It is called after
/// a confirmed delete and only after the program removal succeeded.
/// </summary>
public interface IAppDataDeletion
{
    /// <summary>Deletes the app-data folder, naming what it could not remove.</summary>
    AppDataDeletionResult Delete();
}

/// <summary>One in-place held-button release (Core's <see cref="HeldButtonRelease"/>), behind a
/// seam so the Setup tests never construct a real marker or input sink.</summary>
public interface IHeldButtonRecovery
{
    /// <summary>Sends one button-up when a marker exists; otherwise does nothing.</summary>
    HeldButtonReleaseResult ReleaseIfMarked();
}

/// <summary>Creates the private run-folder id for one operation.</summary>
public interface IRunIdGenerator
{
    /// <summary>A filesystem-safe id.</summary>
    string NewId();
}

/// <summary>The current process's own image path (a verified Setup exe a repair run copy may be
/// made from). Tests inject one so no real process is involved.</summary>
public interface ISetupEnvironment
{
    /// <summary>The exe path, or null when the OS cannot answer.</summary>
    string? ProcessPath { get; }
}

/// <summary>The read-only status report's JSON sink.</summary>
public interface IStatusWriter
{
    /// <summary>Writes the JSON document.</summary>
    void Write(string json);
}
