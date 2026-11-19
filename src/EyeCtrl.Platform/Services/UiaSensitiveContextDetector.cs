namespace EyeCtrl.Platform.Services;

using System.Collections.Concurrent;
using System.Windows.Automation;
using EyeCtrl.Core.Interfaces;
using Microsoft.Extensions.Logging;

/// <summary>
/// UI-Automation-backed <see cref="ISensitiveContextDetector"/> (design §13.1). Subscribes to system-wide
/// focus-changed events; on each change it reads the focused element's <c>IsPassword</c> and caches a single
/// boolean the feedback gate reads in O(1). The callback fires on a UI-Automation (RPC) thread, so the cached
/// bool is published with <see cref="System.Threading.Volatile"/>. Every UIA call is wrapped in
/// <c>try</c>/<c>catch</c> and degrades to <em>not sensitive</em> (best-effort — a false negative only shows a
/// subtle cue, never exposing the credential).
/// <para>
/// <b>Threading (the 18.11 exit hang, #340).</b> Microsoft's UI-Automation threading rule: a client that
/// subscribes to events that may originate from its own UI must call <c>AddAutomationEventHandler</c> (or a
/// related method) on a <b>non-UI thread</b>, and must remove the handler on that <b>same</b> thread —
/// otherwise the interaction can stop responding. EyeCtrl is such a client (its keyboard, overlays, settings
/// window and tray menu all raise focus events), and the removal used to run on the WPF UI thread at exit:
/// removing the hook was the first step after the last shutdown log line, so a blocked native
/// <c>UiaRemoveEvent</c> would strand the process with the single-instance mutex held.
/// </para>
/// <para>
/// So the detector owns one dedicated <b>MTA background</b> thread; <see cref="Start"/> and <see cref="Stop"/>
/// post the add and the remove to it, in order. <see cref="Stop"/> waits at most <see cref="StopBudget"/> for
/// the remove and then abandons the thread — it is background, so a hung native call cannot hold the process.
/// The constructor is side-effect-free (UIA is touched only on that thread), so the DI container can construct
/// it on any thread.
/// </para>
/// </summary>
public sealed class UiaSensitiveContextDetector : ISensitiveContextDetector
{
    /// <summary>How long <see cref="Stop"/> waits for the removal before abandoning its thread.</summary>
    internal static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(1);

    private static readonly int OwnProcessId = Environment.ProcessId;

    private readonly ILogger<UiaSensitiveContextDetector> _logger;
    private readonly IFocusHookApi _hooks;
    // Held in a field so the exact same delegate instance can be removed in Stop() (and so it is never GC'd
    // while the native UIA hook holds it — the same discipline the Tobii gaze callback needs).
    private readonly AutomationFocusChangedEventHandler _focusHandler;
    // Start/Stop both run on the one thread that owns the detector (the UI/dispatcher thread in the App),
    // so this gate only guards the hook-thread state against a concurrent Stop from that same discipline.
    private readonly object _gate = new();
    private BlockingCollection<Action>? _commands;
    private Thread? _hookThread;
    // Touched only under _gate (Start/Stop/Dispose run from App.OnStartup + container disposal).
    private bool _started;
    // Written on the UIA/RPC callback thread, read on the dispatcher — published with Volatile for
    // cross-thread ordering. A bool is a single aligned byte (atomic read/write on every arch, so no tearing
    // even on x86); the Volatile here supplies the release/acquire fence, not atomicity.
    private bool _isSensitive;

    /// <summary>Initializes the detector. Assigns fields only — no UI Automation call happens until <see cref="Start"/>.</summary>
    /// <param name="logger">Logger for UIA subscription / read failures (which are otherwise swallowed).</param>
    /// <param name="hooks">The focus-hook seam; the real UIA calls when omitted.</param>
    public UiaSensitiveContextDetector(
        ILogger<UiaSensitiveContextDetector> logger,
        IFocusHookApi? hooks = null)
    {
        _logger = logger;
        _hooks = hooks ?? new AutomationFocusHookApi();
        _focusHandler = OnFocusChanged;
    }

    /// <inheritdoc />
    public bool IsSensitiveActive => System.Threading.Volatile.Read(ref _isSensitive);

    /// <summary>
    /// Pure focus decision (headless-testable): returns the new sensitivity value, or <c>null</c> to
    /// <em>retain</em> the previous value. Own-process focus (an EyeCtrl window/overlay) retains — the user
    /// types passwords via the on-screen keyboard, so a momentary focus into EyeCtrl's own UI must not clear an
    /// active sensitive context. Otherwise the context is sensitive iff the focused element is a password field.
    /// </summary>
    /// <param name="focusedProcessId">Process id of the newly focused element.</param>
    /// <param name="ownProcessId">EyeCtrl's own process id.</param>
    /// <param name="isPassword">The focused element's <c>IsPassword</c> flag.</param>
    /// <returns><c>true</c>/<c>false</c> for the new value, or <c>null</c> to keep the current value.</returns>
    internal static bool? EvaluateFocusedElement(int focusedProcessId, int ownProcessId, bool isPassword)
    {
        if (focusedProcessId == ownProcessId)
            return null; // own-process focus: retain the previous cached value
        return isPassword;
    }

    /// <inheritdoc />
    public void Start()
    {
        BlockingCollection<Action> commands;
        lock (_gate)
        {
            if (_started)
                return;
            _started = true;
            commands = EnsureHookThreadLocked();
        }

        commands.Add(() =>
        {
            try
            {
                _hooks.Add(_focusHandler);
            }
            catch (Exception ex)
            {
                // Best-effort: detection stays inactive (never sensitive), so feedback is simply not suppressed.
                _logger.LogWarning(ex, "Failed to subscribe to UIA focus-changed events; sensitive-context detection is inactive.");
            }
        });
    }

    /// <inheritdoc />
    public void Stop()
    {
        BlockingCollection<Action>? commands;
        Thread? thread;
        lock (_gate)
        {
            if (!_started)
                return;
            _started = false;
            commands = _commands;
            thread = _hookThread;
            // Abandoned (or finished) either way: a later Start gets a fresh thread, and one that
            // timed out must never be reused — it is still inside the native removal.
            _commands = null;
            _hookThread = null;
        }

        if (commands is null || thread is null || !thread.IsAlive)
        {
            ResetSensitive();
            return;
        }

        // Deliberately not disposed when Stop returns: on the timeout path the removal is still in
        // flight on the abandoned thread, and this event is how it reports back. Disposing it here
        // would turn that callback into an ObjectDisposedException in the hook thread's queue.
        var removed = new ManualResetEventSlim(false);
        try
        {
            commands.Add(() =>
            {
                try
                {
                    _hooks.Remove(_focusHandler);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to unsubscribe the UIA focus-changed handler during shutdown.");
                }
                finally
                {
                    removed.Set();
                }
            });
        }
        catch (InvalidOperationException)
        {
            // The queue was completed by an earlier Stop; there is nothing left to post to.
            ResetSensitive();
            return;
        }

        if (!removed.Wait(StopBudget))
        {
            // Abandon, do not join: the thread is background, and waiting longer is exactly what
            // stranded the process on 18.11. Completing the queue still lets it end as soon as the
            // native removal returns.
            _logger.LogWarning(
                "UIA focus-hook removal did not finish within {BudgetMs} ms; abandoning its background thread.",
                (int)StopBudget.TotalMilliseconds);
        }

        commands.CompleteAdding();

        // Last, as before the hook thread existed: the removal has been posted (and waited for, up to
        // the budget), so the window in which a callback still in flight can re-write the verdict is as
        // narrow as it was. Harmless either way at Stop/Dispose (shutdown), when IsSensitiveActive is no
        // longer read — the coordinator's timer is disposed by the same container disposal.
        ResetSensitive();
    }

    /// <summary>
    /// Resets the cached verdict to the safe default. RemoveAutomationFocusChangedEventHandler stops
    /// *new* invocations but does not cancel one already running, so a callback in flight on the UIA
    /// thread can still re-write this after <see cref="Stop"/> returns.
    /// </summary>
    private void ResetSensitive() => System.Threading.Volatile.Write(ref _isSensitive, false);

    /// <summary>Stops detection (removes the focus hook — it otherwise leaks) via <see cref="Stop"/>.</summary>
    public void Dispose() => Stop();

    /// <summary>
    /// Starts the one hook thread if it is not running. Caller holds <see cref="_gate"/>.
    /// MTA because UI Automation clients must not run on an STA thread without a message pump;
    /// background so a stuck native call can never keep the process alive.
    /// </summary>
    private BlockingCollection<Action> EnsureHookThreadLocked()
    {
        if (_commands is not null && _hookThread is { IsAlive: true })
            return _commands;

        var commands = new BlockingCollection<Action>();
        var thread = new Thread(() =>
            {
                foreach (Action command in commands.GetConsumingEnumerable())
                {
                    try
                    {
                        command();
                    }
                    catch (Exception ex)
                    {
                        // The posted commands guard their own UIA calls; this is the last-resort guard so
                        // one failing command never kills the thread that the next Stop posts to.
                        _logger.LogDebug(ex, "UIA focus-hook thread command failed.");
                    }
                }
            })
        {
            IsBackground = true,
            Name = "EyeCtrl UIA focus hook",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        _commands = commands;
        _hookThread = thread;
        thread.Start();
        return commands;
    }

    private void OnFocusChanged(object? sender, AutomationFocusChangedEventArgs e)
    {
        // The event's `sender` IS the element that received focus when the event fired — the correct
        // per-event choice. (AutomationElement.FocusedElement, which §13.1 names as the example, returns
        // whatever has focus *now*, which for rapid focus flips could be a newer element; either way the
        // ~4 Hz dispatcher read converges to the latest cached value, and using `sender` avoids a round-trip.)
        // Reading it can still fault, so the whole read is guarded and degrades to not-sensitive.
        try
        {
            if (sender is not AutomationElement element)
            {
                // No readable focused element ⇒ treat as not sensitive (best-effort false negative, §13.1).
                System.Threading.Volatile.Write(ref _isSensitive, false);
                return;
            }

            // The process id first: own-process focus (EyeCtrl's keyboard, an overlay) retains WITHOUT reading
            // IsPassword — a cross-thread UIA read of our own UI from this callback is avoidable work, and a
            // fault there would wrongly clear an active sensitive context (review r1 F4).
            int processId = element.Current.ProcessId;
            bool? decision = processId == OwnProcessId
                ? null
                : EvaluateFocusedElement(processId, OwnProcessId, element.Current.IsPassword);
            if (decision.HasValue)
                System.Threading.Volatile.Write(ref _isSensitive, decision.Value);
        }
        catch (Exception ex)
        {
            // Any UIA read failure (element vanished mid-read, COM fault) ⇒ not sensitive.
            _logger.LogDebug(ex, "UIA focus-change read failed; treating context as not sensitive.");
            System.Threading.Volatile.Write(ref _isSensitive, false);
        }
    }
}
