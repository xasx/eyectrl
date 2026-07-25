namespace EyeCtrl.Platform.Services;

using System.Windows.Automation;
using EyeCtrl.Core.Interfaces;
using Microsoft.Extensions.Logging;

/// <summary>
/// UI-Automation-backed <see cref="ISensitiveContextDetector"/> (design §13.1). Subscribes to system-wide
/// focus-changed events; on each change it reads the focused element's <c>IsPassword</c> and caches a single
/// boolean the feedback gate reads in O(1). The callback fires on a UI-Automation (RPC) thread, so the cached
/// bool is published with <see cref="System.Threading.Volatile"/>. Every UIA call is wrapped in
/// <c>try</c>/<c>catch</c> and degrades to <em>not sensitive</em> (best-effort — a false negative only shows a
/// subtle cue, never exposing the credential). The constructor is side-effect-free (UIA is touched only in
/// <see cref="Start"/>, mirroring the Tobii provider), so the DI container can construct it on any thread.
/// </summary>
public sealed class UiaSensitiveContextDetector : ISensitiveContextDetector
{
    private static readonly int OwnProcessId = Environment.ProcessId;

    private readonly ILogger<UiaSensitiveContextDetector> _logger;
    // Held in a field so the exact same delegate instance can be removed in Stop() (and so it is never GC'd
    // while the native UIA hook holds it — the same discipline the Tobii gaze callback needs).
    private readonly AutomationFocusChangedEventHandler _focusHandler;
    // Touched only on the UI/dispatcher thread (Start/Stop/Dispose run from App.OnStartup + container
    // disposal), so it needs no synchronization.
    private bool _started;
    // Written on the UIA/RPC callback thread, read on the dispatcher — published with Volatile for
    // cross-thread ordering. A bool is a single aligned byte (atomic read/write on every arch, so no tearing
    // even on x86); the Volatile here supplies the release/acquire fence, not atomicity.
    private bool _isSensitive;

    /// <summary>Initializes the detector. Assigns fields only — no UI Automation call happens until <see cref="Start"/>.</summary>
    /// <param name="logger">Logger for UIA subscription / read failures (which are otherwise swallowed).</param>
    public UiaSensitiveContextDetector(ILogger<UiaSensitiveContextDetector> logger)
    {
        _logger = logger;
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
        if (_started)
            return;
        _started = true;
        try
        {
            Automation.AddAutomationFocusChangedEventHandler(_focusHandler);
        }
        catch (Exception ex)
        {
            // Best-effort: detection stays inactive (never sensitive), so feedback is simply not suppressed.
            _logger.LogWarning(ex, "Failed to subscribe to UIA focus-changed events; sensitive-context detection is inactive.");
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (!_started)
            return;
        _started = false;
        try
        {
            Automation.RemoveAutomationFocusChangedEventHandler(_focusHandler);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to unsubscribe the UIA focus-changed handler during shutdown.");
        }
        // Reset to the safe default. RemoveAutomationFocusChangedEventHandler stops *new* invocations but does
        // not cancel one already running, so a callback in flight on the UIA thread could re-write this after
        // the removal above — but that only happens at Stop/Dispose (shutdown), when IsSensitiveActive is no
        // longer read (the coordinator's timer is disposed by the same container disposal), so it is harmless.
        System.Threading.Volatile.Write(ref _isSensitive, false);
    }

    /// <summary>Stops detection (removes the focus hook — it otherwise leaks) via <see cref="Stop"/>.</summary>
    public void Dispose() => Stop();

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

            bool? decision = EvaluateFocusedElement(element.Current.ProcessId, OwnProcessId, element.Current.IsPassword);
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
