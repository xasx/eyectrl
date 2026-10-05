namespace EyeCtrl.App.Views;

using System;
using System.Windows.Threading;

/// <summary>
/// Ticket 07 review F5: the settings window's supervision presence-refresh timer, extracted so its
/// wiring is testable headlessly (STA). The tick calls the refresh action (W1), and
/// <see cref="Stop"/> genuinely stops it (W2) — a still-running DispatcherTimer would root the
/// closed window and tick every interval for the rest of the session.
/// </summary>
internal sealed class SupervisionPresenceRefresher
{
    /// <summary>Two seconds: fast enough to show a mid-session Supervisor exit, slow enough to be
    /// invisible; the refresh itself is change-only, so no rebuild happens unless the truth moved.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _timer;

    /// <param name="refresh">The change-only refresh to run each tick
    /// (<see cref="Services.SupervisionToggleService.RefreshConnection"/> in production).</param>
    /// <param name="interval">Tick interval; tests pass a short one.</param>
    public SupervisionPresenceRefresher(Action refresh, TimeSpan? interval = null)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = interval ?? DefaultInterval
        };
        _timer.Tick += (_, _) => refresh();
    }

    public bool IsRunning => _timer.IsEnabled;

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();
}
