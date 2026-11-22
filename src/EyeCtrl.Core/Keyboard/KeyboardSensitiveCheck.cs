namespace EyeCtrl.Core.Keyboard;

using System.Globalization;
using EyeCtrl.Core.Interfaces;
using Microsoft.Extensions.Logging;

/// <summary>
/// D4 of the password-detector diagnostics: one read-only, side-by-side poll of the focused element's
/// <c>IsPassword</c> at the keyboard's show/hide moment, logged next to the event-driven detector's cached
/// verdict so a missed password field is visible in one line.
///
/// <para>
/// <b>It never changes the verdict.</b> The poll has no access to <see cref="ISensitiveContextDetector"/> and
/// its answer only reaches the log — the detector stays the sole authority, so no gate, mute or learning
/// decision can move because of this. <see cref="Begin"/> returns at once; the UI-Automation read runs on the
/// thread pool under <see cref="DefaultTimeBox"/> (300 ms) and a read that has not answered is logged
/// <c>timeout</c> and abandoned (UI Automation cannot be cancelled). While an abandoned read is still running
/// no new one starts — the next check logs <c>busy</c> instead of piling up a thread per show.
/// </para>
///
/// <para>
/// <b>Content-free by construction.</b> A line carries the moment (<c>show</c>/<c>hide</c>), two booleans (as
/// words), the poll outcome, and the foreground PROCESS name. The poll returns a boolean or throws, so a field's
/// text, name, value or automation id has no path into a line.
/// </para>
/// </summary>
public sealed class KeyboardSensitiveCheck
{
    /// <summary>The hard time box for the poll; D4 requires at most 300 ms.</summary>
    public static readonly TimeSpan DefaultTimeBox = TimeSpan.FromMilliseconds(300);

    /// <summary>The poll answered <c>true</c>.</summary>
    public const string True = "true";
    /// <summary>The poll answered <c>false</c>.</summary>
    public const string False = "false";
    /// <summary>The poll had no readable foreign focused element (or saw EyeCtrl's own UI).</summary>
    public const string None = "none";
    /// <summary>The poll did not answer within the time box and was abandoned.</summary>
    public const string Timeout = "timeout";
    /// <summary>The previous abandoned read is still running, so no new read was started.</summary>
    public const string Busy = "busy";
    /// <summary>The poll threw; the value is <c>error:&lt;ExceptionType&gt;</c>.</summary>
    public const string ErrorPrefix = "error:";

    private readonly ILogger? _logger;
    private readonly IFocusedPasswordPoll? _poll;
    private readonly TimeSpan _timeBox;
    private int _readInFlight;

    /// <param name="logger">Where the line goes; null makes every call a no-op.</param>
    /// <param name="poll">The read-only poll; null disables the check (diagnostics not wired).</param>
    /// <param name="timeBox">The hard time box; defaults to <see cref="DefaultTimeBox"/>.</param>
    public KeyboardSensitiveCheck(ILogger? logger, IFocusedPasswordPoll? poll, TimeSpan? timeBox = null)
    {
        _logger = logger;
        _poll = poll;
        _timeBox = timeBox ?? DefaultTimeBox;
    }

    /// <summary>
    /// Starts one check and returns immediately. <paramref name="evt"/> is the detector's verdict in the same
    /// moment — evidence to compare, never an input. <paramref name="counts"/> is the detector's live
    /// focus-event tally (F3a: a sitting's log is read while EyeCtrl runs, before the exit line exists). The
    /// returned task completes after the line is logged and never faults; a caller on a path that must not wait
    /// (the keyboard's show/hide) must not await it.
    /// </summary>
    /// <param name="at"><c>show</c> or <c>hide</c>.</param>
    /// <param name="evt">The event-driven verdict at this moment.</param>
    /// <param name="process">The foreground PROCESS name (never a title), or <c>unknown</c>.</param>
    /// <param name="counts">The live focus-event counters, or <see cref="FocusEventCounts.Unknown"/>.</param>
    public Task Begin(string at, bool evt, string process, FocusEventCounts counts)
    {
        try
        {
            if (_logger is null || _poll is null)
                return Task.CompletedTask;
            if (Interlocked.CompareExchange(ref _readInFlight, 1, 0) != 0)
                return Report(at, evt, Busy, process, counts);

            return Task.Run(() => CheckAsync(at, evt, process, counts));
        }
        catch (Exception)
        {
            return Task.CompletedTask;
        }
    }

    private async Task CheckAsync(string at, bool evt, string process, FocusEventCounts counts)
    {
        string polled;
        Task<bool?>? read = null;
        try
        {
            read = Task.Run(() => _poll!.ReadIsPassword());
            // Free the slot only when the read itself ends, not when the time box does.
            _ = read.ContinueWith(_ => Interlocked.Exchange(ref _readInFlight, 0),
                TaskScheduler.Default);
            bool? result = await read.WaitAsync(_timeBox).ConfigureAwait(false);
            polled = result is null ? None : result.Value ? True : False;
        }
        catch (TimeoutException)
        {
            polled = Timeout;
            if (read is null)
                Interlocked.Exchange(ref _readInFlight, 0);
        }
        catch (Exception ex)
        {
            // Only the TYPE: a provider's message could carry content.
            polled = ErrorPrefix + ex.GetType().Name;
            if (read is null)
                Interlocked.Exchange(ref _readInFlight, 0);
        }

        Report(at, evt, polled, process, counts);
    }

    private Task Report(string at, bool evt, string polled, string process, FocusEventCounts counts)
    {
        try
        {
            _logger?.LogInformation(
                "Sensitive check at {At}: event={Event} poll={Poll} proc={Proc}"
                + " events={Events} password={Password} failures={Failures} own={Own}",
                at, evt ? True : False, polled, process,
                Count(counts.Total), Count(counts.Password), Count(counts.Failures), Count(counts.Own));
        }
        catch (Exception)
        {
            // Logging must never throw into the keyboard.
        }
        return Task.CompletedTask;
    }

    /// <summary>An unknown counter (-1) renders <c>-</c>, never a real zero that would read as "no events".</summary>
    private static string Count(int value) =>
        value < 0 ? "-" : value.ToString(CultureInfo.InvariantCulture);
}
