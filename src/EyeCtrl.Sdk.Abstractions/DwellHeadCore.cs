namespace EyeCtrl.Sdk.Abstractions;

public sealed class DwellHeadCore
{
    private readonly string _idPrefix;
    private int _started;
    private long _idCounter;

    public DwellClient Client { get; } = new();

    public DwellHeadCore(string idPrefix) => _idPrefix = idPrefix;

    public void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) Client.Start();
    }

    public string NextId() => $"{_idPrefix}{Interlocked.Increment(ref _idCounter)}";
}
