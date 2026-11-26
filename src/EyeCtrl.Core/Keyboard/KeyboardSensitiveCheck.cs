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
/// no new one starts — the next check logs <c>busy</c> instead of piling up a thread per show. The in-flight
/// slot is owner-attributed (a generation per check), so a late completion from an older read can never free
/// a newer read's slot.
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

    /// <summary>The check that owns the read slot, or 0 when the slot is free (generations start at 1).</summary>
    private int _readInFlight;

    /// <summary>Source of the owner generations; incremented for every check that tries to claim.</summary>
    private int _generation;

    /// <summary>
    /// Test seam (<c>InternalsVisibleTo</c> → EyeCtrl.Core.Tests): wraps the read-owned, owner-checked
    /// release. Production runs it at once; a test can hold it to make a late release land after a
    /// later check has claimed the slot.
    /// </summary>
    internal Action<Action>? ReleaseReadSlotForTest { get; set; }

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

            // The slot is OWNER-ATTRIBUTED: each check claims it with a fresh generation and every
            // release only frees its own generation, so an old read's late release can never free a
            // newer read's slot and let two UIA reads overlap.
            int generation = Interlocked.Increment(ref _generation);
            if (Interlocked.CompareExchange(ref _readInFlight, generation, 0) != 0)
                return Report(at, evt, Busy, process, counts);

            return Task.Run(() => CheckAsync(generation, at, evt, process, counts));
        }
        catch (Exception)
        {
            return Task.CompletedTask;
        }
    }

    private async Task CheckAsync(int generation, string at, bool evt, string process,
        FocusEventCounts counts)
    {
        string polled;
        Task<bool?>? read = null;
        try
        {
            read = Task.Run(() => _poll!.ReadIsPassword());
            // An abandoned read keeps the slot until the read itself ends, not until the time box does.
            _ = read.ContinueWith(_ => ReleaseFromRead(generation), TaskScheduler.Default);
            bool? result = await read.WaitAsync(_timeBox).ConfigureAwait(false);

            // The read has completed: free our own slot BEFORE the line is logged, so the next
            // show/hide sees it free. The read-owned continuation repeats the same owner-checked
            // release; whichever runs first wins and the other is a no-op.
            ReleaseSlot(generation);
            polled = result is null ? None : result.Value ? True : False;
        }
        catch (TimeoutException)
        {
            // The time box fired. A read that is still running holds the slot until it ends (the
            // continuation releases it); only a read that already ended may free it here.
            polled = Timeout;
            if (read is null || read.IsCompleted)
                ReleaseSlot(generation);
        }
        catch (Exception ex)
        {
            // Only the TYPE: a provider's message could carry content. A provider fault surfaces
            // here only after its read completed, so the slot can be freed inline.
            polled = ErrorPrefix + ex.GetType().Name;
            if (read is null || read.IsCompleted)
                ReleaseSlot(generation);
        }

        Report(at, evt, polled, process, counts);
    }

    /// <summary>Frees the slot only if <paramref name="generation"/> still owns it.</summary>
    private void ReleaseSlot(int generation) =>
        Interlocked.CompareExchange(ref _readInFlight, 0, generation);

    /// <summary>The read's own release path (also the timeout path), owner-checked.</summary>
    private void ReleaseFromRead(int generation)
    {
        Action<Action>? seam = ReleaseReadSlotForTest;
        if (seam is null)
            ReleaseSlot(generation);
        else
            seam(() => ReleaseSlot(generation));
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
