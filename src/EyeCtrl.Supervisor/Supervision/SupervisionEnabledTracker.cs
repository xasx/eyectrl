namespace EyeCtrl.Supervisor.Supervision;

/// <summary>
/// Ticket 06-B: the live <c>supervision.enabled</c> value used for recovery decisions. Every
/// <see cref="Current"/> evaluation reads the settings file; when the file cannot be told apart
/// (damaged, unparseable, ambiguous) it falls back to the last value successfully READ FROM DISK,
/// seeded from the startup <c>Load()</c>.
/// </summary>
/// <remarks>
/// Deliberately exposes no in-memory input: the Supervisor's setup window mutates its own settings
/// object on open and on every unsaved toggle click, and that value must never influence recovery.
/// A damaged file after such a click resolves to the last disk value, never to the click.
/// </remarks>
internal sealed class SupervisionEnabledTracker
{
    private readonly string _settingsPath;
    private bool _lastReadFromDisk;

    public SupervisionEnabledTracker(string settingsPath, bool seedFromDisk)
    {
        _settingsPath = settingsPath;
        _lastReadFromDisk = seedFromDisk;
    }

    /// <summary>The value to act on. Reads the disk every time; never caches a disk value.</summary>
    public bool Current
    {
        get
        {
            var disk = SupervisionEnabledReader.TryRead(_settingsPath);
            if (disk.HasValue) _lastReadFromDisk = disk.Value;
            return disk ?? _lastReadFromDisk;
        }
    }
}
