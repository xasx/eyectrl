namespace EyeCtrl.Supervisor.Supervision;

using EyeCtrl.Core.Enums;

/// <summary>
/// The live <c>uiLanguage</c> value the Supervisor shows its own surfaces in. Every
/// <see cref="Current"/> evaluation reads the settings file; when the value cannot be told apart
/// (damaged, unparseable, contended) it falls back to the last value successfully READ FROM DISK.
/// <see cref="UiLanguage.System"/> is used only while nothing has been read successfully — a
/// contention fallback of System would show German on this de-DE machine to a user who chose
/// English, right around crashes and restarts.
/// </summary>
internal sealed class SupervisorLanguageTracker
{
    private readonly string _settingsPath;
    private readonly string? _writerMutexName;
    private UiLanguage? _lastReadFromDisk;

    /// <param name="settingsPath">The settings file to read.</param>
    /// <param name="writerMutexName">Writer mutex name passed to the reader; injectable so tests
    /// use a GUID name and never touch the production mutex. Null = the production name.</param>
    public SupervisorLanguageTracker(string settingsPath, string? writerMutexName = null)
    {
        _settingsPath = settingsPath;
        _writerMutexName = writerMutexName;
    }

    /// <summary>The value to act on. Reads the disk every time; only successful reads are kept.</summary>
    public UiLanguage Current
    {
        get
        {
            var disk = UiLanguageReader.TryRead(_settingsPath, _writerMutexName);
            if (disk.HasValue) _lastReadFromDisk = disk.Value;
            return disk ?? _lastReadFromDisk ?? UiLanguage.System;
        }
    }
}
