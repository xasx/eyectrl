namespace EyeCtrl.App.Tests;

using System.Globalization;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// F5's diagnostics half, all behind the <see cref="AudioSessionDiagnostics"/> seams (no real
/// COM, no real audio): the snapshot and change line formats (word-for-word, culture-invariant),
/// the once-per-app-lifetime first-click guard, the show/hide baseline snapshots, the
/// registration retry for a session that winmm has not created yet, and — the owner-safety
/// rule — that a Core Audio failure can never throw into the typing path and never blocks a
/// click. A source pin holds the coordinator's forwarding of the real transitions in place.
/// <para>
/// Runs in the exclusive "KeyboardAudioDiagnostics" collection (see
/// <see cref="KeyboardAudioDiagnosticsCollection"/> for why).
/// </para>
/// <para>
/// Fixup 03: every test uses the PARK scheduler seam — the diagnostics work never runs by
/// itself; the test runs it explicitly (<c>RunParkedWork</c>). That makes timing races
/// impossible (the fixup-01/02 flake family came from pool-scheduled items landing inside other
/// tests' windows), and line-content assertions stay on the dedicated format tests instead of a
/// shared capture.
/// </para>
/// </summary>
[Collection("KeyboardAudioDiagnostics")]
public class AudioSessionDiagnosticsTests
{
    private sealed class FakeReader : AudioSessionDiagnostics.IAudioStateReader
    {
        public int Reads;
        public bool ThrowOnRead;
        public AudioSessionDiagnostics.AudioStateSnapshot? Next { get; set; } =
            new("Speakers", true, 0.8, false, "Active");

        public AudioSessionDiagnostics.AudioStateSnapshot? Read()
        {
            Reads++;
            if (ThrowOnRead) throw new InvalidOperationException("com gone");
            return Next;
        }
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

    private sealed class BlockingReader : AudioSessionDiagnostics.IAudioStateReader
    {
        private readonly ManualResetEventSlim _release;
        public BlockingReader(ManualResetEventSlim release) => _release = release;
        public AudioSessionDiagnostics.AudioStateSnapshot? Read()
        {
            _release.Wait(30_000); // a hung AudioSrv RPC, simulated (released by the test)
            return null;
        }
    }

    private sealed class BlockingListener : AudioSessionDiagnostics.IAudioChangeListener
    {
        private readonly ManualResetEventSlim _release;
        public BlockingListener(ManualResetEventSlim release) => _release = release;
        public bool TryRegister(Action<string> onChangeLine)
        {
            _release.Wait(30_000);
            return true;
        }

        public bool TryRegisterSessionEvents()
        {
            _release.Wait(30_000);
            return true;
        }

        public void Shutdown() => _release.Wait(30_000);
    }

    private static (FakeReader Reader, FakeListener Listener, Queue<Action> Parked) Configure()
    {
        var reader = new FakeReader();
        var listener = new FakeListener();
        AudioSessionDiagnostics.ConfigureForTesting(reader, listener);
        // Park scheduler: the diagnostics work never runs by itself. Tests run it explicitly via
        // RunParkedWork — deterministic, no threads, no stragglers (review 03 fixup).
        var parked = new Queue<Action>();
        AudioSessionDiagnostics.WorkScheduler = parked.Enqueue;
        return (reader, listener, parked);
    }

    /// <summary>Runs every parked diagnostics work item in FIFO order (the "background thread
    /// did its work" step of each test, without threads).</summary>
    private static void RunParkedWork(Queue<Action> parked)
    {
        while (parked.Count > 0) parked.Dequeue().Invoke();
    }

    private static void ResetStatics()
    {
        // No drain needed under the park scheduler (nothing runs on the pool), but keep the
        // counters consistent for the next test.
        AudioSessionDiagnostics.WorkScheduler = null;
        AudioSessionDiagnostics.ConfigureForTesting(null, null);
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
        var (reader, listener, parked) = Configure();
        AudioSessionDiagnostics.OnClickAttempted();
        RunParkedWork(parked);
        AudioSessionDiagnostics.OnClickAttempted();
        AudioSessionDiagnostics.OnClickAttempted();
        RunParkedWork(parked);

        Check.That(reader.Reads).IsEqualTo(1);
        Check.That(listener.RegisterCalls).IsEqualTo(1);
        Check.That(listener.SessionRegisterCalls).IsEqualTo(1);
        ResetStatics();
    }

    [Fact]
    public void Transitions_BeforeAnyClick_DoNothing()
    {
        var (reader, listener, parked) = Configure();
        AudioSessionDiagnostics.OnKeyboardTransition();
        AudioSessionDiagnostics.OnKeyboardTransition();
        RunParkedWork(parked);

        // Counter-based (review 03 fixup: parallel production emitters made a shared log-line
        // capture racy): a transition before the first click must touch NO reader and NO listener.
        Check.That(reader.Reads).IsEqualTo(0);
        Check.That(listener.RegisterCalls).IsEqualTo(0);
        Check.That(AudioSessionDiagnostics.FailureLogged).IsFalse();
        ResetStatics();
    }

    [Fact]
    public void Transitions_AfterFirstClick_Snapshot_ButNeverReRegisterCompleteAttachments()
    {
        var (reader, listener, parked) = Configure();
        AudioSessionDiagnostics.OnClickAttempted();
        RunParkedWork(parked);
        var readsAfterFirstClick = reader.Reads;

        AudioSessionDiagnostics.OnKeyboardTransition();
        RunParkedWork(parked);
        AudioSessionDiagnostics.OnKeyboardTransition();
        RunParkedWork(parked);

        Check.That(reader.Reads).IsEqualTo(readsAfterFirstClick + 2); // two baselines
        Check.That(listener.RegisterCalls).IsEqualTo(1);
        Check.That(listener.SessionRegisterCalls).IsEqualTo(1);
        ResetStatics();
    }

    [Fact]
    public void FailedEndpointRegistration_IsRetriedOnTheNextTransition()
    {
        var reader = new FakeReader();
        var listener = new FakeListener { RegisterResult = false };
        AudioSessionDiagnostics.ConfigureForTesting(reader, listener);
        var parked = new Queue<Action>();
        AudioSessionDiagnostics.WorkScheduler = parked.Enqueue;
        AudioSessionDiagnostics.OnClickAttempted();
        RunParkedWork(parked);
        Check.That(listener.RegisterCalls).IsEqualTo(1);

        AudioSessionDiagnostics.OnKeyboardTransition();
        RunParkedWork(parked);
        Check.WithCustomMessage(
                "an endpoint listener that failed to attach must be retried at the next real "
                + "transition — registration is the only way change events ever fire")
            .That(listener.RegisterCalls).IsEqualTo(2);
        ResetStatics();
    }

    [Fact]
    public void SessionEvents_RegistrationIsRetriedUntilItAttaches()
    {
        var (reader, listener, parked) = Configure();
        listener.SessionResults = new Queue<bool>([false, false, true]);
        AudioSessionDiagnostics.OnClickAttempted(); // attach attempt 1: false (no session yet)
        RunParkedWork(parked);
        AudioSessionDiagnostics.OnKeyboardTransition(); // attempt 2: false (still none)
        RunParkedWork(parked);
        AudioSessionDiagnostics.OnKeyboardTransition(); // attempt 3: true (session appeared)
        RunParkedWork(parked);

        Check.That(listener.SessionRegisterCalls).IsEqualTo(3);
        ResetStatics();
    }

    // ---- the owner-safety rule: a COM failure can never throw into the click path ----

    [Fact]
    public void ThrowingCoreAudio_NeverThrows_NeverBlocksAClick_AndFailsOnceOnly()
    {
        var reader = new FakeReader { ThrowOnRead = true };
        var listener = new ThrowingListener();
        AudioSessionDiagnostics.ConfigureForTesting(reader, listener);
        var parked = new Queue<Action>();
        AudioSessionDiagnostics.WorkScheduler = parked.Enqueue;
        try
        {
            var player = new AlwaysTruePlayer();
            var svc = new KeyboardSoundService(new KeyboardSettings(), "irrelevant.wav", _ => player);

            Check.ThatCode(() => svc.PlayKeyClick()).DoesNotThrow();
            Check.ThatCode(() => svc.PlayKeyClick()).DoesNotThrow();
            Check.That(player.Calls).IsEqualTo(2); // the click path never learned about the COM mess
            Check.That(parked).HasSize(1); // the once-guard parks exactly ONE diagnostics work item

            // Run the parked diagnostics work NOW: the failure report must be latched to one.
            RunParkedWork(parked);
            Check.That(AudioSessionDiagnostics.FailureLogged).IsTrue();

            AudioSessionDiagnostics.OnKeyboardTransition();
            RunParkedWork(parked);
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
        AudioSessionDiagnostics.ConfigureForTesting(null, null);
        var parked = new Queue<Action>();
        AudioSessionDiagnostics.WorkScheduler = parked.Enqueue;
        try
        {
            var player = new AlwaysTruePlayer();
            var svc = new KeyboardSoundService(new KeyboardSettings(), "irrelevant.wav", _ => player);

            Check.ThatCode(() => svc.PlayKeyClick()).DoesNotThrow();
            Check.That(player.Calls).IsEqualTo(1); // the unwired state never blocks a click
            RunParkedWork(parked); // the diagnostics work runs honestly against "not configured"

            AudioSessionDiagnostics.EnsureProductionWiring();
            Check.WithCustomMessage(
                    "EnsureProductionWiring must install the REAL Core Audio reader and observer — "
                    + "production without them would never read state or register listeners")
                .That(AudioSessionDiagnostics.ProductionWired).IsTrue();
        }
        finally { ResetStatics(); }
    }

    [Fact]
    public void PlayKeyClick_DoesNotInvokeTheReaderSynchronously()
    {
        // Review 01 F4: cold COM measured ~105 ms on the first read; the first click may only
        // flip the once-flag and HAND the work to the scheduler. The parked scheduler makes the
        // assertion deterministic: if the click ever ran the work on its own thread, the reader
        // would fire without the park being released.
        var reader = new FakeReader();
        var listener = new FakeListener();
        AudioSessionDiagnostics.ConfigureForTesting(reader, listener);
        var parked = new Queue<Action>();
        AudioSessionDiagnostics.WorkScheduler = parked.Enqueue;
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
                .That(parked).HasSize(1);

            // Releasing the park IS the background thread doing its work later.
            RunParkedWork(parked);
            Check.That(reader.Reads).IsEqualTo(1);
        }
        finally { ResetStatics(); }
    }

    [Fact]
    public void TheObserverAcquiresTheEnumeratorLazily_AndTheWiringNeverDoes()
    {
        // Review 03 S1: the observer's MMDeviceEnumerator (CoCreate ~44 ms cold) must be acquired
        // lazily on the WORKER (inside _comLock, at the first registration) — never in the
        // constructor that EnsureProductionWiring touches on the UI thread, and never by the
        // wiring call itself. Uses the real observer (read-only COM + an in-process registration
        // that this test unregisters again); the creation counter is the seam.
        var before = CoreAudioInterop.EnumeratorCreations;
        var observer = new CoreAudioInterop.CoreAudioChangeObserver();
        Check.WithCustomMessage("constructing the observer acquired the shared enumerator — "
                + "that CoCreate would run on the UI thread at startup")
            .That(CoreAudioInterop.EnumeratorCreations).IsEqualTo(before);

        // EnsureProductionWiring with the pair already wired: no acquisition, no throwaway pair.
        AudioSessionDiagnostics.ConfigureForTesting(new FakeReader(), new FakeListener());
        AudioSessionDiagnostics.EnsureProductionWiring();
        Check.That(CoreAudioInterop.EnumeratorCreations).IsEqualTo(before);

        // The first worker registration CREATES the shared instance (unless an earlier worker
        // read already did) and later uses are cache hits: the count may move by AT MOST one —
        // never more, and never on the UI path (asserted above).
        try
        {
            Check.ThatCode(() => observer.TryRegister(_ => { })).DoesNotThrow();
            var afterTry = CoreAudioInterop.EnumeratorCreations;
            Check.WithCustomMessage(
                    "one worker registration moved the creation count by more than one — the shared "
                    + "enumerator must be created at most once, ever")
                .That(new[] { before, before + 1 }).Contains(afterTry);
            observer.TryRegisterSessionEvents(); // may return false (probe-like: no own session)
            Check.That(CoreAudioInterop.EnumeratorCreations).IsEqualTo(afterTry);
        }
        finally
        {
            observer.Shutdown(); // unregister the real in-process callback again
        }
    }

    [Fact]
    public void QueuedTransition_AfterShutdown_RegistersNothing_AndLogsNothing()
    {
        // Review 03 S2 (mutant X2 survived): park a TRANSITION, shutdown, then release it — the
        // latch inside TransitionWork must suppress the late snapshot AND the registration retry.
        var (reader, listener, parked) = Configure();
        try
        {
            AudioSessionDiagnostics.OnClickAttempted(); // first click: work runs, registered
            RunParkedWork(parked);
            var readsAfterClick = reader.Reads;
            AudioSessionDiagnostics.OnKeyboardTransition(); // parked transition
            AudioSessionDiagnostics.Shutdown(); // latch

            RunParkedWork(parked); // the late transition, running after the latch
            Check.WithCustomMessage(
                    "a transition work item that runs after Shutdown logged or registered — the "
                    + "shutdown latch must silence late work (review 03 S2, mutant X2)")
                .That(listener.RegisterCalls).IsEqualTo(1);
            Check.That(reader.Reads).IsEqualTo(readsAfterClick);
        }
        finally { ResetStatics(); }
    }

    [Fact]
    public void TheDrain_ReportsSuccess_AndTimesOutLoudly()
    {
        // Review 02 R6: the drain must say whether it actually drained. An item parked by the
        // scheduler is scheduled but never completed — exactly the timeout case.
        var (reader, listener, parked) = Configure();
        try
        {
            Check.That(AudioSessionDiagnostics.DrainQueuedWork(200)).IsTrue();

            AudioSessionDiagnostics.OnClickAttempted(); // parked: scheduled, never completed
            Check.WithCustomMessage("a drain that gave up silently would hide the fixup-01 flake again")
                .That(AudioSessionDiagnostics.DrainQueuedWork(200)).IsFalse();

            RunParkedWork(parked); // complete it: the drain reports success again
            Check.That(AudioSessionDiagnostics.DrainQueuedWork(200)).IsTrue();
        }
        finally { ResetStatics(); }
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

    private sealed class AlwaysTruePlayer : IClickSoundPlayer
    {
        public int Calls;
        public bool Play() { Calls++; return true; }
    }
}

/// <summary>
/// The exclusive collection for the F5 click-sound diagnostics tests: every attempted click
/// funnels into AudioSessionDiagnostics' app-lifetime once-guard (a static), so the three test
/// classes that click — the latch tests, the service counters and the audio-diagnostics tests —
/// must never interleave. DisableParallelization is also what the
/// WpfApplicationCreatorIsolationTests scan demands of any file that creates the WPF
/// Application (the window-forwarding test does).
/// </summary>
[CollectionDefinition("KeyboardAudioDiagnostics", DisableParallelization = true)]
public class KeyboardAudioDiagnosticsCollection
{
}
