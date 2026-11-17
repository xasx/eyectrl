namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Input;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// C10's routing: blocked → banner + ring look, cleared → hide + restore, a dismissal lasts only
/// for the current episode, and the way-out button calls the window manager's NON-INJECTING path.
/// The coordinator is driven through its internal <c>Apply</c>, so no dispatcher pump and no window
/// are needed; the detector itself is pinned in EyeCtrl.Core.Tests.
/// </summary>
public class BlockedInputCoordinatorTests
{
    private static readonly BlockedInputState Blocked = new(
        IsBlocked: true,
        BlockedInputReason.HigherIntegrityForeground,
        "test",
        new PhysicalPoint(10, 10),
        new PhysicalPoint(300, 300),
        new PhysicalPoint(300, 300),
        new ProcessTokenFacts("Taskmgr.exe", 0x3000, true, false, false),
        ProcessTokenFacts.Unknown);

    private static readonly BlockedInputState Cleared = Blocked with
    {
        IsBlocked = false,
        Reason = BlockedInputReason.Unknown,
        Trigger = "verified action move",
    };

    [Fact]
    public void ABlockedTransition_ShowsTheBannerAndMarksTheRing()
    {
        var h = new Harness();

        h.Coordinator.Apply(Blocked);

        Check.That(h.Banner.Shown).ContainsExactly(Blocked);
        Check.That(h.Ring.BlockedStates).ContainsExactly(true);
    }

    [Fact]
    public void AClearedTransition_HidesTheBannerAndRestoresTheRing()
    {
        var h = new Harness();
        h.Coordinator.Apply(Blocked);

        h.Coordinator.Apply(Cleared);

        Check.That(h.Banner.HideCount).IsEqualTo(1);
        Check.That(h.Ring.BlockedStates).ContainsExactly(true, false);
    }

    [Fact]
    public void ADismissal_SuppressesOnlyTheCurrentEpisode()
    {
        var h = new Harness();
        h.Coordinator.Apply(Blocked);
        h.Banner.RaiseDismiss();

        h.Coordinator.Apply(Blocked); // same episode: no re-show
        Check.That(h.Banner.Shown).HasSize(1);

        h.Coordinator.Apply(Cleared); // the episode ends
        h.Coordinator.Apply(Blocked); // a new episode shows again
        Check.That(h.Banner.Shown).HasSize(2);
    }

    [Fact]
    public void TheEscapeButton_CallsTheNonInjectingWindowManagerAndPassesItsTarget()
    {
        var h = new Harness();
        h.Coordinator.Apply(Blocked);

        h.Banner.RaiseEscape();

        Check.That(h.Escape.TargetLookups).IsEqualTo(1);
        Check.That(h.Escape.Begun).ContainsExactly(h.Escape.Target);
        // The completion is asynchronous: it arrives later, from the scheduler's worker.
        Check.That(h.Escape.HasPending).IsTrue();
        Check.That(h.Escape.CompleteFromWorker(new EscapeWindowResult(true, false, true))).IsNull();
        Check.That(h.Banner.HideCount).IsEqualTo(0);
    }

    /// <summary>
    /// F-ESC: the escape is the owner's emergency exit. Raising it must return to the dispatcher
    /// at once — the attempt is still running on the scheduler's worker when the call returns.
    /// </summary>
    [Fact]
    public void TheEscapeButton_ReturnsToTheDispatcherAtOnce_WhileTheAttemptIsStillRunning()
    {
        var h = new Harness();
        h.Coordinator.Apply(Blocked);

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        h.Banner.RaiseEscape();
        elapsed.Stop();

        Check.WithCustomMessage(
                "the escape button blocked the dispatcher; a hung target would freeze gaze control.")
            .That(elapsed.ElapsedMilliseconds).IsStrictlyLessThan(50);
        Check.That(h.Escape.HasPending).IsTrue();
        Check.That(h.Banner.HideCount).IsEqualTo(0);
    }

    /// <summary>
    /// The completion arrives on the scheduler's worker and must not touch a WPF object there. The
    /// banner records the thread of every call, so a completion that hides or re-shows it off the
    /// dispatcher fails this test.
    /// </summary>
    [Fact]
    public void TheEscapeCompletion_RunsOffTheWorkerButTouchesNoBanner()
    {
        var h = new Harness();
        h.Coordinator.Apply(Blocked);
        var dispatcherThread = Environment.CurrentManagedThreadId;
        h.Banner.RaiseEscape();

        Check.That(h.Escape.CompleteFromWorker(new EscapeWindowResult(true, true, false))).IsNull();

        Check.WithCustomMessage("the escape completion touched the banner off the dispatcher.")
            .That(h.Banner.CallThreads.Where(id => id != dispatcherThread)).IsEmpty();
        Check.That(h.Banner.HideCount).IsEqualTo(0);
        Check.That(h.Ring.BlockedStates).ContainsExactly(true);
    }

    /// <summary>
    /// A null result is the scheduler's "dropped or abandoned" answer: the banner must stay up and
    /// nothing may throw.
    /// </summary>
    [Fact]
    public void TheEscapeCompletion_WithNoResult_LeavesTheBannerUp()
    {
        var h = new Harness();
        h.Coordinator.Apply(Blocked);
        h.Banner.RaiseEscape();

        Check.That(h.Escape.CompleteFromWorker(null)).IsNull();

        Check.That(h.Banner.HideCount).IsEqualTo(0);
        Check.That(h.Ring.BlockedStates).ContainsExactly(true);
    }

    /// <summary>
    /// N-F-ESC: disposing EyeCtrl while an attempt is in flight must not throw, and the late
    /// completion must not call back into the disposed coordinator's surfaces.
    /// </summary>
    [Fact]
    public void DisposeWhileAnEscapeIsPending_IgnoresTheLateCompletion()
    {
        var h = new Harness();
        h.Coordinator.Apply(Blocked);
        h.Banner.RaiseEscape();

        h.Coordinator.Dispose();
        var bannerCallsAfterDispose = h.Banner.CallCount;
        var ringStatesAfterDispose = h.Ring.BlockedStates.Count();

        Check.That(h.Escape.CompleteFromWorker(new EscapeWindowResult(true, true, false))).IsNull();

        Check.That(h.Banner.CallCount).IsEqualTo(bannerCallsAfterDispose);
        Check.That(h.Ring.BlockedStates.Count()).IsEqualTo(ringStatesAfterDispose);
    }

    /// <summary>
    /// Two quick escape requests are queued (the scheduler serialises them); the coordinator does
    /// not coalesce them away, so a second press after nothing happened really does try again.
    /// </summary>
    [Fact]
    public void TwoQuickEscapeRequests_AreBothHandedToTheScheduler()
    {
        var h = new Harness();
        h.Coordinator.Apply(Blocked);

        h.Banner.RaiseEscape();
        h.Banner.RaiseEscape();

        Check.That(h.Escape.Begun).ContainsExactly(h.Escape.Target, h.Escape.Target);
    }

    /// <summary>
    /// The completion callback runs on a worker thread: a source pin keeps every WPF surface out
    /// of it, independently of what the fakes happen to observe.
    /// </summary>
    [Fact]
    public void TheEscapeCompletionMethod_TouchesNoUiSurface()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "EyeCtrl.App", "Services", "BlockedInputCoordinator.cs"));
        var body = ExtractMethod(source, "private void OnEscapeCompleted(IntPtr hwnd, EscapeWindowResult? result)");

        foreach (var surface in new[]
                 {
                     "_banner", "_cursorCircle", "_dispatcher", "_gate", "_eyeCare", "_detector",
                 })
        {
            Check.WithCustomMessage(
                    $"the worker-thread escape completion touches {surface}; it must only log.")
                .That(body.Contains(surface, StringComparison.Ordinal)).IsFalse();
        }
    }

    [Fact]
    public void Dispose_RestoresTheRingAndHidesTheBanner()
    {
        var h = new Harness();
        h.Coordinator.Apply(Blocked);

        h.Coordinator.Dispose();

        Check.That(h.Banner.HideCount).IsEqualTo(1);
        Check.That(h.Ring.BlockedStates[^1]).IsFalse();
    }

    private sealed class Harness
    {
        public Harness()
        {
            var detector = new BlockedInputDetector(new FakeCursor(), new FakeIntegrity());
            Banner = new FakeBanner();
            Escape = new FakeEscape();
            Ring = new FakeRing();
            Gate = new BlockedInputGate();
            EyeCare = new FakeEyeCare { Gate = Gate };
            Messenger = new WeakReferenceMessenger();
            Coordinator = new BlockedInputCoordinator(
                detector, Banner, Escape, Ring, Dispatcher.CurrentDispatcher,
                Messenger, Gate, EyeCare);
        }

        public FakeBanner Banner { get; }
        public FakeEscape Escape { get; }
        public FakeRing Ring { get; }
        public BlockedInputGate Gate { get; }
        public FakeEyeCare EyeCare { get; }
        public WeakReferenceMessenger Messenger { get; }
        public BlockedInputCoordinator Coordinator { get; }
    }

    [Fact]
    public void AGapFrame_DoesNotFeedTheBannerButtons()
    {
        // F4: a held (blink / tracking-loss) sample must not advance the banner's dwell detectors.
        var h = new Harness();

        h.Messenger.Send(new GazeCursorMovedMessage(
            new GazeFrame(new PhysicalPoint(10, 10), 1, IsInGap: true)));
        Check.That(h.Banner.GazeCount).IsEqualTo(0);

        h.Messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(10, 10), 2));
        Check.That(h.Banner.GazeCount).IsEqualTo(1);
    }

    [Fact]
    public void ABlockedTransition_DefersEyeCareAndTakesItsBannerDown()
    {
        // F3: the blocked banner wins. The gate holds a break due while blocked; a live eye-care
        // banner is cancelled (only the banner, not a running exercise).
        var h = new Harness();

        h.Coordinator.Apply(Blocked);

        Check.That(h.Gate.IsBlocked).IsTrue();
        Check.That(h.EyeCare.CancelBannerCount).IsEqualTo(1);
        Check.That(h.EyeCare.CancelActiveCount).IsEqualTo(0);

        h.Coordinator.Apply(Cleared);

        Check.That(h.Gate.IsBlocked).IsFalse();
        Check.That(h.EyeCare.CancelBannerCount).IsEqualTo(1); // clears do not cancel anything
    }

    [Fact]
    public void Dispose_AbandonsADeferredEyeCarePrompt_BeforeReleasingTheGate()
    {
        // N2: if Dispose released the gate first, the deferred ShowBannerAsync would resume and
        // paint on a shutting-down dispatcher. CancelBanner must see the gate still blocked.
        var h = new Harness();
        h.Coordinator.Apply(Blocked); // one CancelBanner for the block transition

        h.Coordinator.Dispose();

        Check.That(h.EyeCare.CancelBannerCount).IsEqualTo(2);
        Check.WithCustomMessage("Dispose released the gate before cancelling the deferred prompt.")
            .That(h.EyeCare.CancelBannerSawBlocked).IsTrue();
        Check.That(h.Gate.IsBlocked).IsFalse();
    }

    /// <summary>No-op hooks; only the banner cancellation is observed.</summary>
    private sealed class FakeEyeCare : IEyeCareHooks
    {
        public int CancelBannerCount { get; private set; }
        public int CancelActiveCount { get; private set; }
        public bool CancelBannerSawBlocked { get; set; }

        /// <summary>Set by the harness so the order of Dispose's cancel and gate release is visible.</summary>
        public BlockedInputGate? Gate { get; set; }

        public Task<bool> ShowBannerAsync(string message) => Task.FromResult(true);

        public Task ShowExerciseAsync(
            string exerciseName, long durationMicroseconds,
            Func<long, PhysicalPoint> getTargetAt, Action<PhysicalPoint, long> recordGaze) =>
            Task.CompletedTask;

        public Task ShowSummaryAsync(EyeCareExerciseOutcome outcome) => Task.CompletedTask;

        public void CancelActive() => CancelActiveCount++;

        public void CancelBanner()
        {
            CancelBannerSawBlocked = Gate?.IsBlocked ?? false;
            CancelBannerCount++;
        }
    }

    private sealed class FakeBanner : IBlockedInputBanner
    {
        public List<BlockedInputState> Shown { get; } = new();
        public int HideCount { get; private set; }
        public int CallCount { get; private set; }
        public List<int> CallThreads { get; } = new();

        public event EventHandler? EscapeRequested;
        public event EventHandler? DismissRequested;
        public event EventHandler? Hidden;

        public void ShowBlocked(BlockedInputState state)
        {
            RecordCall();
            Shown.Add(state);
        }

        public void HideBlocked()
        {
            RecordCall();
            HideCount++;
            Hidden?.Invoke(this, EventArgs.Empty);
        }

        public void OnGaze(PhysicalPoint gaze, long timestampMicroseconds)
        {
            RecordCall();
            GazeCount++;
        }

        public int GazeCount { get; private set; }
        public void RaiseEscape() => EscapeRequested?.Invoke(this, EventArgs.Empty);
        public void RaiseDismiss() => DismissRequested?.Invoke(this, EventArgs.Empty);

        private void RecordCall()
        {
            CallCount++;
            CallThreads.Add(Environment.CurrentManagedThreadId);
        }
    }

    /// <summary>
    /// The asynchronous escape seam: records the hand-off and holds the completion so a test can
    /// invoke it from a real background thread (exactly where the scheduler calls it).
    /// </summary>
    private sealed class FakeEscape : IEscapeWindowService
    {
        private Action<EscapeWindowResult?>? _pending;

        public IntPtr Target { get; } = new(42);
        public int TargetLookups { get; private set; }
        public List<IntPtr> Begun { get; } = new();
        public bool HasPending => _pending is not null;

        public IntPtr? TryGetEscapeTarget()
        {
            TargetLookups++;
            return Target;
        }

        public void BeginBringToFront(IntPtr hwnd, Action<EscapeWindowResult?> onCompleted)
        {
            Begun.Add(hwnd);
            _pending = onCompleted;
        }

        /// <summary>
        /// Invokes the pending completion on a real worker thread and returns whatever it threw, so
        /// a coordinator fault surfaces as a failed assertion rather than an unhandled exception.
        /// </summary>
        public Exception? CompleteFromWorker(EscapeWindowResult? result)
        {
            var completion = _pending
                ?? throw new InvalidOperationException("no escape attempt is pending");

            Exception? thrown = null;
            var worker = new Thread(() =>
            {
                try
                {
                    completion(result);
                }
                catch (Exception ex)
                {
                    thrown = ex;
                }
            })
            {
                IsBackground = true,
                Name = "EyeCtrl test escape completion",
            };
            worker.Start();
            if (!worker.Join(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("the escape completion did not return");

            return thrown;
        }
    }

    private sealed class FakeRing : ICursorCircleOverlay
    {
        public List<bool> BlockedStates { get; } = new();

        public void Show() { }
        public void Hide() { }
        public void UpdatePosition(PhysicalPoint point) { }
        public void MoveTo(PhysicalPoint point, bool glide) { }
        public void UpdateAppearance(CursorSettings settings) { }
        public void SetPinned(bool pinned) { }
        public void SetBlocked(bool blocked) => BlockedStates.Add(blocked);
    }

    private sealed class FakeCursor : ICursorPositionProbe
    {
        public PhysicalPoint? TryGetCursorPosition() => new PhysicalPoint(300, 300);
    }

    private sealed class FakeIntegrity : IInputIntegrityProbe
    {
        public SelfTokenFacts ReadSelf() => new(0x2000, false, false);
        public ProcessTokenFacts InspectForeground() => ProcessTokenFacts.Unknown;
        public ProcessTokenFacts InspectPoint(PhysicalPoint point) => ProcessTokenFacts.Unknown;
        public ProcessTokenFacts InspectWindow(IntPtr hwnd) => ProcessTokenFacts.Unknown;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Could not find the repository root (EyeCtrl.slnx).");
    }

    /// <summary>Returns one method's source text by brace matching from its signature.</summary>
    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Check.WithCustomMessage($"The repository source no longer contains '{signature}'.")
            .That(start).IsStrictlyGreaterThan(-1);

        var open = source.IndexOf('{', start);
        Check.That(open).IsStrictlyGreaterThan(-1);

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[open..(i + 1)];
        }

        throw new InvalidOperationException($"Unbalanced braces after '{signature}'.");
    }
}
