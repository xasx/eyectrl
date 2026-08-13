namespace EyeCtrl.Sdk.Abstractions;

public sealed class DwellHeadCore
{
    private readonly string _idPrefix;
    private int _started;
    private long _idCounter;

    private DwellClient? _client;

    /// <summary>
    /// The client. Constructed on first <see cref="EnsureStarted"/> so the latched pipe name can be
    /// passed to it — a field initialiser ran too early for that.
    /// </summary>
    /// <exception cref="InvalidOperationException">Accessed before <see cref="EnsureStarted"/>.</exception>
    public DwellClient Client => _client
        ?? throw new InvalidOperationException(
            "DwellHeadCore.EnsureStarted has not run; there is no client yet.");

    public DwellHeadCore(string idPrefix) => _idPrefix = idPrefix;

    public void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        _client = new DwellClient(DwellHead.Latch());
        _client.Start();
    }

    public string NextId() => $"{_idPrefix}{Interlocked.Increment(ref _idCounter)}";
}
