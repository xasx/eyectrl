namespace EyeCtrl.App.Tests;

using System.Globalization;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// The exclusive collection for the F5 click-sound diagnostics tests: every attempted click
/// funnels into AudioSessionDiagnostics' app-lifetime once-guard (a static), so the test classes
/// that click — the latch tests, the service counters and the audio-diagnostics tests — must
/// never interleave. DisableParallelization is also what the
/// WpfApplicationCreatorIsolationTests scan demands of any file that creates the WPF Application.
/// </summary>
[CollectionDefinition("KeyboardAudioDiagnostics", DisableParallelization = true)]
public class KeyboardAudioDiagnosticsCollection
{
}

/// <summary>
/// Review 04 T4: the COLLECTION-WIDE scheduler for every test in this collection that can queue
/// diagnostics work. The constructor parks the work (the real thread pool is never reached from
/// a test) and hands the test a queue it can inspect and run; Dispose completes whatever the
/// test left parked (so the schedule/complete counters stay balanced) and requires a clean drain
/// before the statics reset — loud, per review 02 R6. No queued item can ever land inside
/// another test's window (the fixup-01/03 flake family).
/// </summary>
public abstract class F5AudioDiagnosticsTestBase : IDisposable
{
    private readonly Queue<Action> _parked = new();

    protected F5AudioDiagnosticsTestBase()
    {
        AudioSessionDiagnostics.ConfigureForTesting(null, null); // drains (trivially at ctor time)
        AudioSessionDiagnostics.WorkScheduler = _parked.Enqueue; // park: NEVER the real pool
    }

    /// <summary>The work this test parked but has not run yet (inspectable per test).</summary>
    protected Queue<Action> Parked => _parked;

    /// <summary>Runs every parked diagnostics work item in FIFO order — the "background thread
    /// did its work now" step, without threads.</summary>
    protected void RunParkedWork()
    {
        while (_parked.Count > 0) _parked.Dequeue().Invoke();
    }

    /// <summary>Deterministic cleanup: run everything the test parked, then reset the statics.
    /// (Under the park scheduler this never waits — everything completes at Invoke.)</summary>
    protected void ResetStatics()
    {
        RunParkedWork();
        AudioSessionDiagnostics.WorkScheduler = null;
        AudioSessionDiagnostics.ConfigureForTesting(null, null);
    }

    public void Dispose()
    {
        // Complete whatever the test left parked (deterministic — the counters stay balanced),
        // then require a clean drain (loud, review 02 R6) before the statics reset.
        RunParkedWork();
        Check.That(AudioSessionDiagnostics.DrainQueuedWork(200)).IsTrue();
        AudioSessionDiagnostics.WorkScheduler = null;
        AudioSessionDiagnostics.ConfigureForTesting(null, null);
    }
}

/// <summary>
/// F5's diagnostics half, all behind the <see cref="AudioSessionDiagnostics"/> seams (no real
/// COM, no real audio — see <see cref="TheObserverAcquiresTheEnumeratorLazily"/> for the one
/// deliberate in-process exception): the once-per-app-lifetime first-click guard, the show/hide
/// baseline snapshots, the registration retry for a session that winmm has not created yet, and
/// — the owner-safety rule — that a Core Audio failure can never throw into the typing path and
/// never blocks a click. Source pins hold the coordinator forwarding and the LoadKeyboard wiring
/// in place. Every test inherits the collection-wide park scheduler
/// (<see cref="F5AudioDiagnosticsTestBase"/>).
/// </summary>
[Collection("KeyboardAudioDiagnostics")]
public class AudioSessionDiagnosticsTests : F5AudioDiagnosticsTestBase
{
    private sealed class FakeReader : AudioSessionDiagnostics.IAudioStateReader
    {
        public int Reads;
        public bool ThrowOnRead;

        public AudioSessionDiagnostics.AudioStateSnapshot? Read()
        {
            Reads++;
            if (ThrowOnRead) throw new InvalidOperationException("com gone");
            return Next;
        }

        public AudioSessionDiagnostics.AudioStateSnapshot? Next { get; set; } =
            new("Speakers", true, 0.8, false, "Active");
    }

    private sealed class FakeListener : AudioSessionDiagnostics.IAudioChangeListener
    {
        public int RegisterCalls, SessionRegisterCalls, ShutdownCalls;
        public bool RegisterResult = true;
        public Queue<bool> SessionResults = new([true]);
        public Action<string>? Handler;

        public bool TryRegister(Action<string> onChangeLine)
        {
            RegisterCalls++;
            Handler = onChangeLine;
            return RegisterResult;
        }

        public bool TryRegisterSessionEvents()
        {
            SessionRegisterCalls++;
            return SessionResults.Count > 1 ? SessionResults.Dequeue() : SessionResults.Peek();
        }

        public void Shutdown() => ShutdownCalls++;
    }

    private static (FakeReader Reader, FakeListener Listener) Configure()
    {
        var reader = new FakeReader();
        var listener = new FakeListener();
        AudioSessionDiagnostics.ConfigureForTesting(reader, listener); // drains (trivially under park)
        return (reader, listener);
    }

    // ---- formats ----

    [Fact]
    public void SnapshotLine_PresentSession_IsPinnedWordForWord()
    {
        var line = new AudioSessionDiagnostics.AudioStateSnapshot("Speakers", true, 0.8, false, "Active").FormatLine();
        Check.That(line).IsEqualTo("Keyboard audio: endpoint=\"Speakers\" session: volume=0.80 mute=False state=Active");
    }

    [Fact]
    public void SnapshotLine_SessionNotCreatedYet_IsPinnedWordForWord()
    {
        var line = new AudioSessionDiagnostics.AudioStateSnapshot("Speakers", false, null, null, null).FormatLine();
        Check.That(line).IsEqualTo("Keyboard audio: endpoint=\"Speakers\" session: not created yet");
    }

    [Fact]
    public void SnapshotLine_UnreadableEndpoint_IsMarkedUnavailable()
    {
        var line = new AudioSessionDiagnostics.AudioStateSnapshot(null, true, 0.8, false, "Active").FormatLine();
        Check.That(line).Contains("<unavailable>");
    }

    [Fact]
    public void ChangeLine_Formats_ArePinnedWordForWord()
    {
        Check.That(AudioSessionDiagnostics.FormatVolumeChange(0.5f, true))
            .IsEqualTo("Keyboard audio change: volume=0.50 mute=True");
        Check.That(AudioSessionDiagnostics.FormatSessionStateChange("Expired"))
            .IsEqualTo("Keyboard audio change: session state=Expired");
        Check.That(AudioSessionDiagnostics.FormatSessionDisconnected("ServiceShutdown"))
            .IsEqualTo("Keyboard audio change: session disconnected: ServiceShutdown");
        Check.That(AudioSessionDiagnostics.FormatDefaultDeviceChanged("Headphones"))
            .IsEqualTo("Keyboard audio change: default device -> \"Headphones\"");
    }

    [Fact]
    public void Formats_AreCultureInvariant_UnderGermanCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Check.That(new AudioSessionDiagnostics.AudioStateSnapshot("Lautsprecher", true, 0.8, false, "Active").FormatLine())
                .IsEqualTo("Keyboard audio: endpoint=\"Lautsprecher\" session: volume=0.80 mute=False state=Active");
            Check.That(AudioSessionDiagnostics.FormatVolumeChange(0.5f, true))
                .IsEqualTo("Keyboard audio change: volume=0.50 mute=True");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    // ---- the once-guard and the transition baselines ----

    [Fact]
    public void FirstClick_SnapshotsOnce_AndRegistersOnce_RegardlessOfHowManyClicksFollow()
    {
        var (reader, listener) = Configure();
        AudioSessionDiagnostics.OnClickAttempted();
        RunParkedWork();
        AudioSessionDiagnostics.OnClickAttempted();
        AudioSessionDiagnostics.OnClickAttempted();
        RunParkedWork();

        Check.That(reader.Reads).IsEqualTo(1);
        Check.That(listener.RegisterCalls).IsEqualTo(1);
        Check.That(listener.SessionRegisterCalls).IsEqualTo(1);
    }

    [Fact]
    public void Transitions_BeforeAnyClick_DoNothing()
    {
        var (reader, listener) = Configure();
        AudioSessionDiagnostics.OnKeyboardTransition();
        AudioSessionDiagnostics.OnKeyboardTransition();
        RunParkedWork(); // nothing was queued — this must be a no-op

        // Counter-based (review 03 fixup: parallel production emitters made a shared log-line
        // capture racy): a transition before the first click must touch NO reader and NO listener.
        Check.That(reader.Reads).IsEqualTo(0);
        Check.That(listener.RegisterCalls).IsEqualTo(0);
        Check.That(AudioSessionDiagnostics.FailureLogged).IsFalse();
    }

    [Fact]
    public void Transitions_AfterFirstClick_Snapshot_ButNeverReRegisterCompleteAttachments()
    {
        var (reader, listener) = Configure();
        AudioSessionDiagnostics.OnClickAttempted();
        RunParkedWork();
        var readsAfterFirstClick = reader.Reads;

        AudioSessionDiagnostics.OnKeyboardTransition();
        RunParkedWork();
        AudioSessionDiagnostics.OnKeyboardTransition();
        RunParkedWork();

        Check.That(reader.Reads).IsEqualTo(readsAfterFirstClick + 2); // two baselines
        Check.That(listener.RegisterCalls).IsEqualTo(1);
        Check.That(listener.SessionRegisterCalls).IsEqualTo(1);
    }

    [Fact]
    public void FailedEndpointRegistration_IsRetriedOnTheNextTransition()
    {
        var reader = new FakeReader();
        var listener = new FakeListener { RegisterResult = false };
        AudioSessionDiagnostics.ConfigureForTesting(reader, listener);
        AudioSessionDiagnostics.OnClickAttempted();
        RunParkedWork();
        Check.That(listener.RegisterCalls).IsEqualTo(1);

        AudioSessionDiagnostics.OnKeyboardTransition();
        RunParkedWork();
        Check.WithCustomMessage(
                "an endpoint listener that failed to attach must be retried at the next real "
                + "transition — registration is the only way change events ever fire")
            .That(listener.RegisterCalls).IsEqualTo(2);
    }

    [Fact]
    public void SessionEvents_RegistrationIsRetriedUntilItAttaches()
    {
        var (reader, listener) = Configure();
        listener.SessionResults = new Queue<bool>([false, false, true]);
        AudioSessionDiagnostics.OnClickAttempted(); // attach attempt 1: false (no session yet)
        RunParkedWork();
        AudioSessionDiagnostics.OnKeyboardTransition(); // attempt 2: false (still none)
        RunParkedWork();
        AudioSessionDiagnostics.OnKeyboardTransition(); // attempt 3: true (session appeared)
        RunParkedWork();

        Check.That(listener.SessionRegisterCalls).IsEqualTo(3);
    }

    // ---- the owner-safety rule: a COM failure can never throw into the click path ----

    [Fact]
    public void ThrowingCoreAudio_NeverThrows_NeverBlocksAClick_AndFailsOnceOnly()
    {
        var reader = new FakeReader { ThrowOnRead = true };
        var listener = new ThrowingListener();
        AudioSessionDiagnostics.ConfigureForTesting(reader, listener);
        try
        {
            var player = new AlwaysTruePlayer();
            var svc = new KeyboardSoundService(new KeyboardSettings(), "irrelevant.wav", _ => player);

            Check.ThatCode(() => svc.PlayKeyClick()).DoesNotThrow();
            Check.ThatCode(() => svc.PlayKeyClick()).DoesNotThrow();
            Check.That(player.Calls).IsEqualTo(2); // the click path never learned about the COM mess

            // Run the parked diagnostics work: the failure report must be latched to one, and a
            // late transition after the latch must not report again.
            RunParkedWork();
            Check.That(AudioSessionDiagnostics.FailureLogged).IsTrue();
            AudioSessionDiagnostics.OnKeyboardTransition();
            RunParkedWork();
            Check.That(AudioSessionDiagnostics.FailureLogged).IsTrue(); // latch held
        }
        finally { ResetStatics(); }
    }

    [Fact]
    public void EnsureProductionWiring_RestoresTheCoreAudioPair_AfterTestsUnsetIt()
    {
        // Review 01 F1 (inverted — the old Unconfigured_DoesNothing pinned the broken state):
        // production must NEVER run unwired. KeyboardCoordinator.LoadKeyboard calls
        // EnsureProductionWiring before it builds the sound service; this is that call's contract.
        // The unwired first click must not throw (the observer-side line is emitted honestly).
        // The parked work is completed only AFTER the pair is reset again, so no real COM runs
        // in the testhost (review 04 T5/T4).
        AudioSessionDiagnostics.ConfigureForTesting(null, null);
        try
        {
            var player = new AlwaysTruePlayer();
            var svc = new KeyboardSoundService(new KeyboardSettings(), "irrelevant.wav", _ => player);

            Check.ThatCode(() => svc.PlayKeyClick()).DoesNotThrow();
            Check.That(player.Calls).IsEqualTo(1); // the unwired state never blocks a click
            RunParkedWork(); // the parked work runs against "unwired" — honest, COM-free

            AudioSessionDiagnostics.EnsureProductionWiring();
            Check.WithCustomMessage(
                    "EnsureProductionWiring must install the REAL Core Audio reader and observer — "
                    + "production without them would never read state or register listeners")
                .That(AudioSessionDiagnostics.ProductionWired).IsTrue();

            // Reset to unwired BEFORE the parked work runs: then the work emits the honest
            // "not configured" line via Serilog instead of touching real Core Audio.
            AudioSessionDiagnostics.ConfigureForTesting(null, null);
        }
        finally { ResetStatics(); }
    }

    [Fact]
    public void PlayKeyClick_DoesNotInvokeTheReaderSynchronously()
    {
        // Review 01 F4: cold COM measured ~105 ms on the first read; the first click may only
        // flip the once-flag and HAND the work to the scheduler. The park scheduler makes the
        // assertion deterministic: if the click ever ran the work on its own thread, the reader
        // would fire without the park being run.
        var reader = new FakeReader();
        AudioSessionDiagnostics.ConfigureForTesting(reader, listener: null);
        try
        {
            var player = new AlwaysTruePlayer();
            var svc = new KeyboardSoundService(new KeyboardSettings(), "irrelevant.wav", _ => player);

            svc.PlayKeyClick();
            Check.WithCustomMessage(
                    "the first click ran Core Audio work synchronously — a cold read costs ~100 ms "
                    + "and must never sit in front of a key; the click only flips the once-flag")
                .That(reader.Reads).IsEqualTo(0);
            Check.WithCustomMessage(
                    "the click neither ran the work synchronously nor scheduled it — the diagnostics "
                    + "would never fire at all")
                .That(Parked).HasSize(1);

            RunParkedWork(); // the background thread doing its work, deterministically
            Check.That(reader.Reads).IsEqualTo(1);
        }
        finally { ResetStatics(); }
    }

    [Fact]
    public void TheObserverAcquiresTheEnumeratorLazily_AndTheWiringNeverDoes()
    {
        // Review 04 T3: the S1 pin is PER INSTANCE — a fresh observer holds NO enumerator after
        // construction and after wiring; the S1-eager mutant (field initializer) makes this true
        // immediately, regardless of test order or what other tests created. No COM: the
        // true-after-worker-run side is probe-proven on this PC (fixup 03's probe line).
        var observer = new CoreAudioInterop.CoreAudioChangeObserver();
        Check.WithCustomMessage(
                "the observer acquired the shared MMDeviceEnumerator in its CONSTRUCTOR — that "
                + "CoCreate (~44 ms cold) would run on the UI thread at startup (review 03 S1)")
            .That(observer.HasEnumerator).IsFalse();

        // Wiring with the pair already configured must not give this INSTANCE an enumerator.
        AudioSessionDiagnostics.ConfigureForTesting(new FakeReader(), new FakeListener());
        AudioSessionDiagnostics.EnsureProductionWiring();
        Check.That(observer.HasEnumerator).IsFalse();
    }

    [Fact]
    public void QueuedTransition_AfterShutdown_RegistersNothing_AndLogsNothing()
    {
        // Review 03 S2 (mutant X2 survived): park a TRANSITION, shutdown, then release it — the
        // latch inside TransitionWork must suppress the late snapshot AND the registration retry.
        var (reader, listener) = Configure();
        AudioSessionDiagnostics.OnClickAttempted();
        RunParkedWork();
        var readsAfterClick = reader.Reads;
        AudioSessionDiagnostics.OnKeyboardTransition(); // parked transition
        AudioSessionDiagnostics.Shutdown(); // latch

        RunParkedWork(); // the late transition, running after the latch
        Check.WithCustomMessage(
                "a transition work item that runs after Shutdown logged or registered — the "
                + "shutdown latch must silence late work (review 03 S2, mutant X2)")
            .That(listener.RegisterCalls).IsEqualTo(1);
        Check.That(reader.Reads).IsEqualTo(readsAfterClick);
    }

    [Fact]
    public void QueuedWork_AfterShutdown_RegistersNothing_AndLogsNothing()
    {
        // Review 04 T2/X4: the FIRST-CLICK variant of the after-shutdown latch. The first click
        // parks its work; the shutdown latch must silence that late work too — no snapshot read,
        // no registration.
        var (reader, listener) = Configure();
        AudioSessionDiagnostics.OnClickAttempted(); // parked first click: never run yet
        AudioSessionDiagnostics.Shutdown(); // latch

        RunParkedWork(); // the late first-click work, running after the latch
        Check.WithCustomMessage(
                "first-click work that runs after Shutdown read state or registered — the "
                + "shutdown latch must silence late work (review 04 X4)")
            .That(reader.Reads).IsEqualTo(0);
        Check.That(listener.RegisterCalls).IsEqualTo(0);
    }

    [Fact]
    public void Shutdown_ForwardsToTheListener()
    {
        // Review 02 R3/R4: Shutdown is SIGNAL-AND-RETURN — it latches, hands the teardown to the
        // pool, and returns immediately; this test waits for the pool-delivered call instead of
        // assuming synchronous delivery (review 04: restored after the fixup-03 rework lost it).
        var (reader, listener) = Configure();
        AudioSessionDiagnostics.Shutdown();
        var deadline = Environment.TickCount64 + 2000;
        while (listener.ShutdownCalls == 0 && Environment.TickCount64 < deadline) Thread.Sleep(10);
        Check.WithCustomMessage("Shutdown did not forward the teardown to the listener within 2 s")
            .That(listener.ShutdownCalls).IsEqualTo(1);
    }

    [Fact]
    public void BlockedCom_NeverBlocksTheUiThreadEntries()
    {
        // Review 02 R3 / review 04 T2 (mutant X3): a hung AudioSrv RPC must not freeze the UI
        // thread. The listener blocks forever; EnsureProductionWiring is lock-free, the click
        // only parks, and Shutdown is signal-and-return (the blocking teardown runs on the pool).
        // Nothing on the test thread may wait behind them.
        var release = new ManualResetEventSlim(false);
        var blockingListener = new BlockingListener(release);
        AudioSessionDiagnostics.ConfigureForTesting(new FakeReader(), blockingListener);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            AudioSessionDiagnostics.EnsureProductionWiring();
            AudioSessionDiagnostics.OnClickAttempted(); // parked — never runs the blocked listener here
            AudioSessionDiagnostics.Shutdown(); // signal-and-return: the teardown blocks on the POOL
            sw.Stop();
            Check.WithCustomMessage(
                    "a blocked diagnostics listener delayed the UI-thread entry points — a hung "
                    + "AudioSrv RPC would freeze gaze control")
                .That(sw.ElapsedMilliseconds).IsStrictlyLessThan(100);

            sw.Restart();
            AudioSessionDiagnostics.EnsureProductionWiring();
            sw.Stop();
            Check.That(sw.ElapsedMilliseconds).IsStrictlyLessThan(100);
        }
        finally
        {
            // Unblock the listener, run the parked work (now fast — the block is released), and
            // only then reset the statics (the drain-throwing ConfigureForTesting stays loud).
            release.Set();
            RunParkedWork();
            AudioSessionDiagnostics.DrainQueuedWork();
            AudioSessionDiagnostics.WorkScheduler = null;
            AudioSessionDiagnostics.ConfigureForTesting(null, null);
        }
    }

    [Fact]
    public void TheDrain_ReportsSuccess_AndTimesOutLoudly()
    {
        // Review 02 R6: the drain must say whether it actually drained. An item parked by the
        // scheduler is scheduled but never completed — exactly the timeout case.
        var (reader, listener) = Configure();
        AudioSessionDiagnostics.OnClickAttempted(); // parked: scheduled, never completed
        Check.WithCustomMessage("a drain that gave up silently would hide the fixup-01 flake again")
            .That(AudioSessionDiagnostics.DrainQueuedWork(200)).IsFalse();

        RunParkedWork(); // complete it: the drain reports success again
        Check.That(AudioSessionDiagnostics.DrainQueuedWork(200)).IsTrue();
    }

    [Fact]
    public void LoadKeyboard_WiresTheProductionAudioPair()
    {
        // Review 02 R5 (+ its mutant O6): the composition test alone could not see production
        // drop the wiring. This pin holds the LoadKeyboard call — the only production site — in
        // place, immediately before the sound service it serves.
        var source = System.Text.RegularExpressions.Regex.Replace(
            SourceScanner.StripComments(System.IO.File.ReadAllText(System.IO.Path.Combine(
                SourceScanner.RepoRoot().FullName,
                "src", "EyeCtrl.App", "Services", "Coordinators", "KeyboardCoordinator.cs"))),
            @"\s+", string.Empty);

        Check.WithCustomMessage(
                "LoadKeyboard no longer calls EnsureProductionWiring before building the sound "
                + "service: production would run the Core Audio diagnostics unwired (review 01 F1)")
            .That(source.Contains(
                "AudioSessionDiagnostics.EnsureProductionWiring();"
                + "keyboard.SetSoundService(newKeyboardSoundService(settings.Keyboard,wavPath));"))
            .IsTrue();
    }

    [Fact]
    public void TheInteropUsesNoEagerComReleases()
    {
        // Review 02 R1/R2's discipline pin: the previous release bugs were FinalReleaseComObject
        // calls on RCWs shared between the reader, the observer and the device-change callback.
        // The file must contain NO eager COM release at all — shared RCWs (the enumerator, the
        // session control) simply go out of scope; only native memory is cleared explicitly.
        // Comments are stripped first (the release discipline is DOCUMENTED in them).
        var source = SourceScanner.StripComments(System.IO.File.ReadAllText(System.IO.Path.Combine(
            SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "CoreAudioInterop.cs")));

        Check.WithCustomMessage(
                "CoreAudioInterop.cs contains an eager COM release (FinalReleaseComObject or "
                + "Marshal.Release): it would separate an RCW another holder shares — the exact "
                + "review 02 R1/R2 defect class (dead mute reads, dead re-registration)")
            .That(source.Contains("FinalReleaseComObject") || source.Contains("Marshal.ReleaseComObject"))
            .IsFalse();
    }

    [Fact]
    public void SetKeyboardVisible_ForwardsTheRealTransitionsToTheWindow()
    {
        // Source pin over comment-stripped, whitespace-collapsed code (the KeyboardCoordinatorTests
        // discipline): the visibility path is unreachable headless (Context null-guards it), so the
        // pin is what sees a revert drop the forwarding or widen it to repeated show requests.
        var source = System.Text.RegularExpressions.Regex.Replace(
            SourceScanner.StripComments(System.IO.File.ReadAllText(System.IO.Path.Combine(
                SourceScanner.RepoRoot().FullName,
                "src", "EyeCtrl.App", "Services", "Coordinators", "KeyboardCoordinator.cs"))),
            @"\s+", string.Empty);

        Check.WithCustomMessage(
                "the click counters' window must follow the REAL transitions only: a repeated show "
                + "request while the keyboard is up would reset a running show's counters, and a "
                + "missing forward loses the hide line entirely. The show forward sits before Show() "
                + "(the window is unchanged until then), the hide forward before Hide().")
            .That(source.Contains(
                "if(visible){if(!wasVisible)Context.OverlayManager.Keyboard.NotifyClickSoundShown();"
                + "Context.OverlayManager.Keyboard.Show();")
                && source.Contains(
                "else{if(wasVisible)Context.OverlayManager.Keyboard.NotifyClickSoundHidden();"
                + "Context.OverlayManager.Keyboard.Hide();"))
            .IsTrue();
    }

    private sealed class ThrowingListener : AudioSessionDiagnostics.IAudioChangeListener
    {
        public bool TryRegister(Action<string> onChangeLine) => throw new InvalidOperationException("com gone");
        public bool TryRegisterSessionEvents() => throw new InvalidOperationException("com gone");
        public void Shutdown() { }
    }

    private sealed class BlockingListener(ManualResetEventSlim release) : AudioSessionDiagnostics.IAudioChangeListener
    {
        public bool TryRegister(Action<string> onChangeLine)
        {
            release.Wait(30_000);
            return true;
        }

        public bool TryRegisterSessionEvents()
        {
            release.Wait(30_000);
            return true;
        }

        public void Shutdown() => release.Wait(30_000);
    }

    private sealed class AlwaysTruePlayer : IClickSoundPlayer
    {
        public int Calls;
        public bool Play() { Calls++; return true; }
    }
}
