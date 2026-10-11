namespace EyeCtrl.Supervisor.Supervision;

/// <summary>
/// The on-disk <c>general.startOnBoot</c> the setup window shows. Every <see cref="Current"/>
/// evaluation reads the settings file; when the value cannot be told (damaged, unparseable,
/// contended) it falls back to the last value successfully READ FROM DISK, and before any read
/// succeeded to the value the Supervisor loaded at startup — which is also a disk value, because
/// nothing in the Supervisor writes this key into its snapshot any more.
/// </summary>
internal sealed class StartOnBootTracker
{
    private readonly string _settingsPath;
    private readonly string? _writerMutexName;
    private bool _lastKnown;

    /// <param name="settingsPath">The settings file to read.</param>
    /// <param name="loadedAtStartup">The value the Supervisor loaded at startup.</param>
    /// <param name="writerMutexName">Writer mutex name passed to the reader; injectable so tests
    /// use a GUID name and never touch the production mutex. Null = the production name.</param>
    public StartOnBootTracker(string settingsPath, bool loadedAtStartup, string? writerMutexName = null)
    {
        _settingsPath = settingsPath;
        _lastKnown = loadedAtStartup;
        _writerMutexName = writerMutexName;
    }

    /// <summary>The value to show. Reads the disk every time; only successful reads are kept.</summary>
    public bool Current
    {
        get
        {
            var disk = StartOnBootReader.TryRead(_settingsPath, _writerMutexName);
            if (disk.HasValue) _lastKnown = disk.Value;
            return _lastKnown;
        }
    }
}
