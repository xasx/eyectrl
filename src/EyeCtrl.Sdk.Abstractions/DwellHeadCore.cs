namespace EyeCtrl.Sdk.Abstractions;

/// <summary>
/// Shared state and ID generator for an SDK UI head (WPF/WinUI).
/// </summary>
public sealed class DwellHeadCore
{
    private readonly string _idPrefix;
    private int _started;
    private long _idCounter;

    /// <summary>Gets the underlying named-pipe dwell client.</summary>
    public DwellClient Client { get; }

    /// <summary>Initializes a new instance of the <see cref="DwellHeadCore"/> class using the production pipe.</summary>
    /// <param name="idPrefix">Prefix for generated target IDs.</param>
    public DwellHeadCore(string idPrefix) : this(idPrefix, null) { }

    /// <summary>Initializes a new instance of the <see cref="DwellHeadCore"/> class with an optional test pipe name.</summary>
    /// <param name="idPrefix">Prefix for generated target IDs.</param>
    /// <param name="pipeName">Optional pipe name override for test isolation.</param>
    public DwellHeadCore(string idPrefix, string? pipeName)
    {
        _idPrefix = idPrefix;
        Client = new DwellClient(pipeName);
    }

    /// <summary>Starts <see cref="Client"/> on the first call; subsequent calls are no-ops.</summary>
    public void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) Client.Start();
    }

    /// <summary>Generates the next monotonically increasing target ID with the head's prefix.</summary>
    public string NextId() => $"{_idPrefix}{Interlocked.Increment(ref _idCounter)}";
}
