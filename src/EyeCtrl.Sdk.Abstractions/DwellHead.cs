namespace EyeCtrl.Sdk.Abstractions;

using EyeCtrl.Contracts;

/// <summary>
/// Process-wide configuration for the dwell client.
/// <para>
/// The SDK deliberately does <b>not</b> read <c>EYECTRL_INSTANCE_ID</c>. A host that inherited a
/// valid-but-stale id would silently target another instance's pipe, and
/// <see cref="DwellClient"/> swallows every connection failure — so the host names the pipe
/// explicitly or gets the production default.
/// </para>
/// <para>
/// <b>Latching:</b> the value is captured at the first <c>DwellHeadCore.EnsureStarted</c>. A later
/// set throws rather than being ignored, because "set before first use" is a contract a host cannot
/// reliably satisfy: <c>Dwell.Enabled="True"</c> in XAML latches during
/// <c>InitializeComponent</c>. The latch flag is static rather than per-client so a process using
/// both the WPF and WinUI heads — which own separate clients — cannot bind two different names.
/// </para>
/// </summary>
public static class DwellHead
{
    private static readonly object Gate = new();
    private static string _pipeName = DwellPipe.Name;
    private static bool _latched;

    /// <summary>
    /// Pipe this process's dwell client connects to. Defaults to <see cref="DwellPipe.Name"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The value is null, empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">
    /// The name has already been latched by a started client and the new value differs.
    /// </exception>
    public static string PipeName
    {
        get { lock (Gate) return _pipeName; }
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Pipe name must be non-blank.", nameof(value));

            lock (Gate)
            {
                if (_latched && !string.Equals(_pipeName, value, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"The dwell pipe name is already latched to '{_pipeName}'; a dwell client has "
                        + "started. Set DwellHead.PipeName before the first dwell-enabled control is "
                        + "realized — note that Dwell.Enabled=\"True\" in XAML latches during "
                        + "InitializeComponent.");

                _pipeName = value;
            }
        }
    }

    /// <summary>Whether a client has started and fixed the name.</summary>
    public static bool IsLatched { get { lock (Gate) return _latched; } }

    /// <summary>
    /// Captures the name for a starting client. Idempotent: a second head latching returns the same
    /// value the first one got.
    /// </summary>
    public static string Latch()
    {
        lock (Gate)
        {
            _latched = true;
            return _pipeName;
        }
    }

    /// <summary>Test-only reset. Not part of the supported surface.</summary>
    internal static void ResetForTests()
    {
        lock (Gate) { _pipeName = DwellPipe.Name; _latched = false; }
    }
}
