namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System.Windows.Threading;
using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;

/// <summary>
/// W1/W2/W3's recording <see cref="IProbeHost"/> (build plan C14). Every call is logged with
/// <see cref="Dispatcher.CheckAccess"/>; the host can throw or re-enter any named call exactly once,
/// and fixation suppression is forwarded to a real headless <c>GazeEngine</c> so "no probe
/// suppression is held" is asserted on the engine itself.
///
/// <para>
/// The keyboard models C13's real semantics: <see cref="HideKeyboard"/> hides it and leaves the
/// claim alone, and <see cref="RestoreKeyboard"/> with <c>false</c> is a NO-OP (review 05 F2). The
/// summon is an internal flag unless the test injects a real <c>QuickActionsCoordinator</c> as its
/// sink (W2/W3's "real objects" setup).
/// </para>
/// </summary>
internal sealed class RecordingProbeHost : IProbeHost
{
    internal sealed record HostCall(string Name, bool CheckAccess);

    private readonly ModalInteractionState _modal;
    private readonly Dispatcher _dispatcher;
    private readonly GazeEngine? _engine;
    private readonly Func<bool>? _cursorVisible;
    private readonly List<HostCall> _log = new();
    private readonly Dictionary<string, Action> _reenterAt = new();

    internal RecordingProbeHost(
        ModalInteractionState modal,
        Dispatcher dispatcher,
        GazeEngine? engine = null,
        Func<bool>? cursorVisible = null)
    {
        _modal = modal;
        _dispatcher = dispatcher;
        _engine = engine;
        _cursorVisible = cursorVisible;
    }

    /// <summary>Every call, in order, with the UI-thread check the moment it was made.</summary>
    internal IReadOnlyList<HostCall> Log => _log;

    /// <summary>Call names only, for order assertions.</summary>
    internal IReadOnlyList<string> Names => _log.Select(c => c.Name).ToList();

    /// <summary>Drops every recorded call, so a test can assert on the restore segment alone.</summary>
    internal void ClearLog() => _log.Clear();

    /// <summary>When set, the named call throws once (before doing anything).</summary>
    internal string? ThrowOnceAt { get; set; }

    /// <summary>
    /// When true, <see cref="HideKeyboard"/> hides first and then throws — the W1-26 model of a
    /// probe that touched the keyboard but never got to record that it did.
    /// </summary>
    internal bool ThrowAfterHideKeyboard { get; set; }

    /// <summary>When set for a call name, that call re-enters once, right after it is recorded.</summary>
    internal void ReenterOnceAt(string name, Action action) => _reenterAt[name] = action;

    /// <summary>Test hooks for the windows this host builds, applied at construction.</summary>
    internal Action<FakeProbeWindow>? WindowSetup { get; set; }

    /// <summary>Builds C5; a test supplies the factory (usually a real C5 on the test's clock).</summary>
    internal Func<SubPixelAbSchedule, TimeProvider, Func<bool>,
        SubPixelProbeStartMode, SubPixelAbSession1Verdict?, SubPixelProbeViewModel>? ViewModelFactory { get; set; }

    /// <summary>Forwards summon suspend/resume to the real coordinator when W2/W3 inject one.</summary>
    internal Action<bool>? SummonSink { get; set; }

    /// <summary>The menus' visibility, as the pre-show re-check sees it.</summary>
    internal bool MenusVisible { get; set; }

    /// <summary>Whether the real cursor VM says the ring should be visible.</summary>
    internal bool CursorVisible { get; set; }

    internal bool KeyboardVisible { get; set; } = true;
    internal bool RingVisible { get; set; } = true;
    internal bool SummonSuspended { get; set; }

    /// <summary>Every window the host built, in construction order.</summary>
    internal List<FakeProbeWindow> Windows { get; } = new();

    /// <summary>Every view model the host built, in construction order.</summary>
    internal List<SubPixelProbeViewModel> VmsSeen { get; } = new();

    private bool Before(string name)
    {
        _log.Add(new HostCall(name, _dispatcher.CheckAccess()));

        if (ThrowOnceAt == name)
        {
            ThrowOnceAt = null;
            throw new InvalidOperationException($"injected throw at {name}");
        }

        if (_reenterAt.TryGetValue(name, out var action))
        {
            _reenterAt.Remove(name);
            action();
            return true;
        }

        return false;
    }

    public void SetProbeOpen(bool open)
    {
        Before(nameof(SetProbeOpen));
        _modal.ProbeOpen = open;
    }

    public bool IsRingVisible => RingVisible;

    public void HideRing()
    {
        Before(nameof(HideRing));
        RingVisible = false;
    }

    public void ShowRingIfVisible()
    {
        Before(nameof(ShowRingIfVisible));
        if ((_cursorVisible?.Invoke()) ?? CursorVisible) RingVisible = true;
    }

    public void AcquireProbeSuppression()
    {
        Before(nameof(AcquireProbeSuppression));
        _engine?.AcquireFixationSuppression(FixationSuppressor.SubPixelProbe);
    }

    public void ReleaseProbeSuppression()
    {
        Before(nameof(ReleaseProbeSuppression));
        _engine?.ReleaseFixationSuppression(FixationSuppressor.SubPixelProbe);
    }

    public void DeactivateToolbar() => Before(nameof(DeactivateToolbar));

    public void AssertToolbarInactive() => Before(nameof(AssertToolbarInactive));

    public void ReassertToolbarVisibility() => Before(nameof(ReassertToolbarVisibility));

    bool IProbeHost.KeyboardVisible => KeyboardVisible;

    public void HideKeyboard()
    {
        Before(nameof(HideKeyboard));
        KeyboardVisible = false;
        if (ThrowAfterHideKeyboard)
            throw new InvalidOperationException("injected throw after hiding the keyboard");
    }

    public void RestoreKeyboard(bool wasVisible)
    {
        Before(nameof(RestoreKeyboard));
        if (wasVisible) KeyboardVisible = true;
    }

    public void SuspendSummon()
    {
        Before(nameof(SuspendSummon));
        SummonSuspended = true;
        SummonSink?.Invoke(true);
    }

    public void ResumeSummon()
    {
        Before(nameof(ResumeSummon));
        SummonSuspended = false;
        SummonSink?.Invoke(false);
    }

    public bool AnyMenuVisible
    {
        get
        {
            Before(nameof(AnyMenuVisible));
            return MenusVisible;
        }
    }

    public System.Windows.Media.Imaging.BitmapSource? CaptureBackdrop()
    {
        Before(nameof(CaptureBackdrop));
        return null;
    }

    public SubPixelProbeViewModel CreateViewModel(
        SubPixelAbSchedule schedule,
        TimeProvider timeProvider,
        Func<bool> isValidSample,
        SubPixelProbeStartMode startMode,
        SubPixelAbSession1Verdict? resumedSession1Verdict)
    {
        Before(nameof(CreateViewModel));
        if (ViewModelFactory is null)
            throw new InvalidOperationException("RecordingProbeHost has no view-model factory.");
        var vm = ViewModelFactory(schedule, timeProvider, isValidSample, startMode, resumedSession1Verdict);
        VmsSeen.Add(vm);
        return vm;
    }

    public void DisposeViewModel(SubPixelProbeViewModel vm)
    {
        Before(nameof(DisposeViewModel));
        vm.Dispose();
    }

    public IProbeWindow CreateWindow(
        System.Windows.Media.Imaging.BitmapSource? backdrop,
        SubPixelProbeViewModel vm,
        EyeCtrl.Core.Settings.CursorSettings cursorSettings,
        Action sessionEnded)
    {
        Before(nameof(CreateWindow));
        var window = new FakeProbeWindow(sessionEnded)
        {
            // The close is a step of Restore; recording it in the same log is what makes W1 (b)'s
            // "close → teardown → suppression → …" order assertable.
            OnClosedObserved = () => _log.Add(new HostCall("WindowClosed", _dispatcher.CheckAccess())),
        };
        WindowSetup?.Invoke(window);
        Windows.Add(window);
        return window;
    }

    internal int CountOf(string name) => _log.Count(c => c.Name == name);
}

/// <summary>
/// WPF's close semantics as measured in review 05 E4, plus the one unmeasured case W1 (c3) models:
/// <c>Show()</c> can be told to swallow a <c>Close()</c> issued during it, leaving the window open
/// with no <c>Closed</c>; the after-Show close path must repair that.
/// </summary>
internal sealed class FakeProbeWindow : IProbeWindow
{
    private readonly Action _sessionEnded;
    private bool _inShow;

    internal FakeProbeWindow(Action sessionEnded) => _sessionEnded = sessionEnded;

    /// <summary>Invoked inside <see cref="Show"/>, so a test can re-enter <c>EndTest</c> there.</summary>
    internal Action? OnShow { get; set; }

    /// <summary>Invoked when <c>Closed</c> is observed, before the session-end callback runs.</summary>
    internal Action? OnClosedObserved { get; set; }

    /// <summary>When true, a <see cref="Close"/> issued during <see cref="Show"/> is swallowed.</summary>
    internal bool SwallowCloseDuringShow { get; set; }

    /// <summary>When true, every <see cref="Close"/> is swallowed (models a late <c>Closed</c>).</summary>
    internal bool SwallowAllCloses { get; set; }

    internal PhysicalPoint? LastGaze { get; private set; }

    public bool ClosedObserved { get; private set; }

    public bool IsShown { get; private set; }

    /// <summary>Fires the close path late, as a real C6 <c>Closed</c> would on a stray Close.</summary>
    internal void FireClosed()
    {
        if (ClosedObserved) return;
        ClosedObserved = true;
        OnClosedObserved?.Invoke();
        _sessionEnded();
    }

    public void UpdateGaze(PhysicalPoint positionPhys) => LastGaze = positionPhys;

    public void Show()
    {
        _inShow = true;
        try
        {
            OnShow?.Invoke();
        }
        finally
        {
            _inShow = false;
        }

        IsShown = true;
    }

    public void Close()
    {
        if (SwallowAllCloses) return;
        if (_inShow && SwallowCloseDuringShow) return;
        FireClosed();
    }
}

/// <summary>
/// The recording watchdog timer factory. One timer per run; the test advances the run's clock and
/// calls <c>Check</c> directly, so the priority and the enable/disable lifecycle are the assertions.
/// </summary>
internal sealed class FakeProbeTimerFactory
{
    internal sealed class FakeTimer : IDisposable
    {
        internal bool Disposed { get; private set; }
        internal bool Enabled { get; set; } = true;
        public void Dispose()
        {
            Disposed = true;
            Enabled = false;
        }
    }

    internal sealed record Creation(DispatcherPriority Priority, TimeSpan Interval, FakeTimer Timer);

    private readonly List<Creation> _created = new();

    internal IReadOnlyList<Creation> Created => _created;

    internal FakeTimer? Last => _created.Count == 0 ? null : _created[^1].Timer;

    public IDisposable Create(DispatcherPriority priority, TimeSpan interval, Action tick)
    {
        var timer = new FakeTimer();
        _created.Add(new Creation(priority, interval, timer));
        return timer;
    }
}
