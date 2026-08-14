namespace EyeCtrl.Sdk.Abstractions;

public sealed class DwellHeadCore
{
    private readonly string _idPrefix;
    private int _started;
    private long _idCounter;

    private DwellClient? _client;

    /// <summary>
    /// The client. Created on first access rather than by a field initialiser, so it can be given
    /// the latched <see cref="DwellHead.PipeName"/> — a field initialiser ran before any host could
    /// set it.
    /// <para>
    /// Lazily, not in <see cref="EnsureStarted"/>: this property is public on a <b>shipped</b>
    /// assembly, and constructing only in <c>EnsureStarted</c> would mean a consumer touching
    /// <c>Client</c> first got an exception where it previously got a usable object. Creating on
    /// demand keeps the original contract exactly — <c>Client</c> is always available, and
    /// <see cref="EnsureStarted"/> is still the only thing that calls <c>Start()</c>.
    /// </para>
    /// <para>
    /// <see cref="LazyInitializer"/> rather than a lock: the WPF and WinUI heads reach this from a
    /// per-dispatcher refresh timer, so it is genuinely multi-threaded. The factory may run more
    /// than once under a race but only one result is ever published, and
    /// <see cref="DwellHead.Latch"/> is idempotent — a second call returns the value the first one
    /// fixed, which is the whole point of the latch.
    /// </para>
    /// </summary>
    public DwellClient Client =>
        LazyInitializer.EnsureInitialized(ref _client, () => new DwellClient(DwellHead.Latch()));

    public DwellHeadCore(string idPrefix) => _idPrefix = idPrefix;

    public void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        Client.Start();
    }

    public string NextId() => $"{_idPrefix}{Interlocked.Increment(ref _idCounter)}";
}
