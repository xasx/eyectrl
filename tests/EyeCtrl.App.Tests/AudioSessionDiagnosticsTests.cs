namespace EyeCtrl.App.Tests;

using System.Globalization;
using System.Runtime.InteropServices;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// The exclusive collection for the F5/F10 click-sound diagnostics tests: every attempted click
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
/// F5's diagnostics half plus F10's device-level instruments, all behind the
/// <see cref="AudioSessionDiagnostics"/> seams (no real COM, no real audio): the
/// once-per-app-lifetime first-click guard, the show/hide baseline snapshots, the registration
/// retry for a session that winmm has not created yet, the endpoint master volume/mute in the
/// snapshot, the per-show endpoint-meter verdicts (reached/silent/pending) with the
/// first-silent line, the click→Active late-start count, the duck lines, and — the owner-safety
/// rule — that a Core Audio failure can never throw into the typing path and never blocks a
/// click. Source pins hold the coordinator forwarding and the LoadKeyboard wiring in place.
/// Every test inherits the collection-wide park scheduler
/// (<see cref="F5AudioDiagnosticsTestBase"/>).
/// </summary>
[Collection("KeyboardAudioDiagnostics")]
public class AudioSessionDiagnosticsTests : F5AudioDiagnosticsTestBase
{
    [Fact]
    public void BothRegistrationBodiesAcquireTheEnumeratorInsideTheComLock()
    {
        // Review 05 V3 (L2's pin, restored and adapted to F10's TryRegister body): the
        // session-events acquisition is a CACHE HIT after TryRegister, so behaviour can never
        // observe its removal — this source pin holds BOTH bodies' acquisition lines (L1 and L2
        // each remove one → RED), and the production factory fallback stays NewEnumerator.
        var source = System.Text.RegularExpressions.Regex.Replace(
            SourceScanner.StripComments(System.IO.File.ReadAllText(System.IO.Path.Combine(
                SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "CoreAudioInterop.cs"))),
            @"\s+", string.Empty);

        Check.WithCustomMessage("TryRegister no longer acquires the shared enumerator inside its _comLock")
            .That(source.Contains(
                "lock(_comLock){if(_shutDown)returnfalse;_enumerator??=AcquireSharedEnumerator();if(!_endpointAttached)"))
            .IsTrue();
        Check.WithCustomMessage("TryRegisterSessionEvents no longer acquires the shared enumerator inside its _comLock")
            .That(source.Contains(
                "lock(_comLock){if(_shutDown||_sessionAttached)return_sessionAttached;_enumerator??=AcquireSharedEnumerator();"))
            .IsTrue();
        Check.WithCustomMessage(
                "the production enumerator factory no longer falls back to the real MMDeviceEnumerator "
                + "coclass — the test seam must never replace the production path")
            .That(source.Contains("_sharedEnumerator??=EnumeratorFactoryForTesting?.Invoke()??NewEnumerator()"))
            .IsTrue();
    }

    private sealed class FakeReader : AudioSessionDiagnostics.IAudioStateReader
    {
        public int Reads;
        public bool ThrowOnRead;
        public AudioSessionDiagnostics.IAudioChangeListener? ListenerToCheck;
        public bool SawListenerRegisteredWhenRead;

        public AudioSessionDiagnostics.AudioStateSnapshot? Read()
        {
            Reads++;
            if (ListenerToCheck is FakeListener { RegisterCalls: > 0 }) SawListenerRegisteredWhenRead = true;
            if (ThrowOnRead) throw new InvalidOperationException("com gone");
            return Next;
        }

        public AudioSessionDiagnostics.AudioStateSnapshot? Next { get; set; } =
            new("Speakers", 0.65, false, true, 0.8, false, "Active");
    }

    private sealed class FakeListener : AudioSessionDiagnostics.IAudioChangeListener
    {
        public int RegisterCalls, SessionRegisterCalls, ShutdownCalls;
        public bool RegisterResult = true;
        public Queue<bool> SessionResults = new([true]);
        public Action<AudioSessionDiagnostics.AudioChange>? Handler;

        public bool TryRegister(Action<AudioSessionDiagnostics.AudioChange> onChange)
        {
            RegisterCalls++;
            Handler = onChange;
            return RegisterResult;
        }

        public bool TryRegisterSessionEvents()
        {
            SessionRegisterCalls++;
            return SessionResults.Count > 1 ? SessionResults.Dequeue() : SessionResults.Peek();
        }

        public void Shutdown() => ShutdownCalls++;

        /// <summary>Simulates a change the OS delivered on a WASAPI thread.</summary>
        public void Emit(AudioSessionDiagnostics.AudioChange change) => Handler?.Invoke(change);
    }

    private sealed class FakeMeter : AudioSessionDiagnostics.IAudioMeterProbe
    {
        public const float Silent = 0f;
        public const float Caught = 0.5f;

        public Queue<float?> Results = new();
        public float? Fallback;
        public int Calls;
        public Func<bool>? LastStop;

        public float? SamplePeak(Func<bool> stop)
        {
            Calls++;
            LastStop = stop;
            return Results.Count > 0 ? Results.Dequeue() : Fallback;
        }
    }

    private static (FakeReader Reader, FakeListener Listener) Configure()
    {
        var reader = new FakeReader();
        var listener = new FakeListener();
        AudioSessionDiagnostics.ConfigureForTesting(reader, listener); // drains (trivially under park)
        return (reader, listener);
    }

    private static (FakeReader Reader, FakeListener Listener, FakeMeter Meter) ConfigureWithMeter(
        params float?[] peaks)
    {
        var reader = new FakeReader();
        var listener = new FakeListener();
        var meter = new FakeMeter { Results = new Queue<float?>(peaks) };
        AudioSessionDiagnostics.ConfigureForTesting(reader, listener, meter);
        return (reader, listener, meter);
    }

    // ---- formats ----

    [Fact]
    public void SnapshotLine_PresentSession_IsPinnedWordForWord()
    {
        var line = new AudioSessionDiagnostics.AudioStateSnapshot(
            "Speakers", 0.65, false, true, 0.8, false, "Active").FormatLine();
        Check.That(line).IsEqualTo(
            "Keyboard audio: endpoint=\"Speakers\" endpoint: volume=0.65 mute=False "
            + "session: volume=0.80 mute=False state=Active");
    }

    [Fact]
    public void SnapshotLine_SessionNotCreatedYet_IsPinnedWordForWord()
    {
        var line = new AudioSessionDiagnostics.AudioStateSnapshot(
            "Speakers", 0.65, false, false, null, null, null).FormatLine();
        Check.That(line).IsEqualTo(
            "Keyboard audio: endpoint=\"Speakers\" endpoint: volume=0.65 mute=False session: not created yet");
    }

    [Fact]
    public void SnapshotLine_UnreadableEndpoint_IsMarkedUnavailable()
    {
        var line = new AudioSessionDiagnostics.AudioStateSnapshot(
            null, 0.65, false, true, 0.8, false, "Active").FormatLine();
        Check.That(line).Contains("<unavailable>");
    }

    [Fact]
    public void SnapshotLine_UnreadableEndpointVolume_IsQuestionMarked()
    {
        // F10: a failed master-volume read must never render as 0 — "?" is not "muted".
        var line = new AudioSessionDiagnostics.AudioStateSnapshot(
            "Speakers", null, null, true, 0.8, false, "Active").FormatLine();
        Check.That(line).IsEqualTo(
            "Keyboard audio: endpoint=\"Speakers\" endpoint: volume=? mute=? "
            + "session: volume=0.80 mute=False state=Active");
    }

    [Fact]
    public void SilentClickLine_IsPinnedWordForWord_AndCarriesTheState()
    {
        var line = AudioSessionDiagnostics.FormatSilentClickLine(
            new AudioSessionDiagnostics.AudioStateSnapshot("Speakers", 0.65, false, true, 0.8, false, "Active"));
        Check.That(line).IsEqualTo(
            "Keyboard click silent: no output peak during the play window; "
            + "endpoint=\"Speakers\" endpoint: volume=0.65 mute=False "
            + "session: volume=0.80 mute=False state=Active");
        Check.That(AudioSessionDiagnostics.FormatSilentClickLine(null))
            .IsEqualTo("Keyboard click silent: no output peak during the play window; audio state unavailable");
    }

    [Fact]
    public void ChangeLine_Formats_ArePinnedWordForWord()
    {
        Check.That(AudioSessionDiagnostics.FormatVolumeChange(0.5f, true))
            .IsEqualTo("Keyboard audio change: volume=0.50 mute=True");
        Check.That(AudioSessionDiagnostics.FormatEndpointVolumeChange(0.65f, false))
            .IsEqualTo("Keyboard audio change: endpoint volume=0.65 mute=False");
        Check.That(AudioSessionDiagnostics.FormatSessionStateChange("Expired"))
            .IsEqualTo("Keyboard audio change: session state=Expired");
        Check.That(AudioSessionDiagnostics.FormatSessionDisconnected("ServiceShutdown"))
            .IsEqualTo("Keyboard audio change: session disconnected: ServiceShutdown");
        Check.That(AudioSessionDiagnostics.FormatDefaultDeviceChanged("Headphones"))
            .IsEqualTo("Keyboard audio change: default device -> \"Headphones\"");
        Check.That(AudioSessionDiagnostics.FormatDuckStart(2))
            .IsEqualTo("Keyboard duck: communications ducking started (sessions=2)");
        Check.That(AudioSessionDiagnostics.FormatDuckEnd())
            .IsEqualTo("Keyboard duck: communications ducking ended");
    }

    [Fact]
    public void Formats_AreCultureInvariant_UnderGermanCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Check.That(new AudioSessionDiagnostics.AudioStateSnapshot(
                    "Lautsprecher", 0.65, false, true, 0.8, false, "Active").FormatLine())
                .IsEqualTo("Keyboard audio: endpoint=\"Lautsprecher\" endpoint: volume=0.65 mute=False "
                    + "session: volume=0.80 mute=False state=Active");
            Check.That(AudioSessionDiagnostics.FormatVolumeChange(0.5f, true))
                .IsEqualTo("Keyboard audio change: volume=0.50 mute=True");
            Check.That(AudioSessionDiagnostics.FormatEndpointVolumeChange(0.65f, false))
                .IsEqualTo("Keyboard audio change: endpoint volume=0.65 mute=False");
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
    public void TheFirstClickRegistersItsInstruments_BeforeItReadsTheState()
    {
        // F10 item 4 ("no blind first show"): the endpoint-volume callback and the ducking
        // registration must exist BEFORE the first click's state is read — otherwise a change
        // during the first show would have no observer. The first-click work registers first.
        var reader = new FakeReader();
        var listener = new FakeListener();
        reader.ListenerToCheck = listener;
        AudioSessionDiagnostics.ConfigureForTesting(reader, listener);
        try
        {
            AudioSessionDiagnostics.OnClickAttempted();
            RunParkedWork();

            Check.WithCustomMessage(
                    "the first click read the audio state before it registered its endpoint "
                    + "instruments — a change during the first show would be blind (F10 item 4)")
                .That(reader.SawListenerRegisteredWhenRead).IsTrue();
        }
        finally { ResetStatics(); }
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

    // ---- F10: the endpoint meter's per-show window ----

    [Fact]
    public void Meter_CountsReachedAndSilent_AndAReachedClickWritesNoSilentLine()
    {
        var (_, _, meter) = ConfigureWithMeter(FakeMeter.Caught, FakeMeter.Silent);
        AudioSessionDiagnostics.OnShowStarted();
        AudioSessionDiagnostics.OnClickPlayed();
        RunParkedWork();
        AudioSessionDiagnostics.OnClickPlayed();
        RunParkedWork();

        var counts = AudioSessionDiagnostics.TakeMeterCounts(10);
        Check.That(counts.Reached).IsEqualTo(1);
        Check.That(counts.Silent).IsEqualTo(1);
        Check.That(counts.Pending).IsEqualTo(0);
        Check.That(meter.Calls).IsEqualTo(2);
    }

    [Fact]
    public void TheFirstSilentClickLine_IsEmittedOncePerShow()
    {
        // F10 item 2: ONE line at the first silent click of a show, never per click.
        var (reader, _, _) = ConfigureWithMeter(
            FakeMeter.Silent, FakeMeter.Silent, FakeMeter.Silent, FakeMeter.Silent);
        try
        {
            AudioSessionDiagnostics.OnShowStarted();
            for (var i = 0; i < 3; i++)
            {
                AudioSessionDiagnostics.OnClickPlayed();
                RunParkedWork();
            }
            Check.That(AudioSessionDiagnostics.SilentClickReports).IsEqualTo(1);
            Check.That(AudioSessionDiagnostics.TakeMeterCounts(10).Silent).IsEqualTo(3);
            Check.That(reader.Reads).IsEqualTo(1); // the silent line cost exactly one state read

            // A new show opens a new window: its first silent click reports again.
            AudioSessionDiagnostics.OnShowStarted();
            AudioSessionDiagnostics.OnClickPlayed();
            RunParkedWork();
            Check.That(AudioSessionDiagnostics.SilentClickReports).IsEqualTo(2);
        }
        finally { ResetStatics(); }
    }

    [Fact]
    public void AReachedClick_WritesNoSilentLine()
    {
        var (_, _, _) = ConfigureWithMeter(FakeMeter.Caught);
        AudioSessionDiagnostics.OnShowStarted();
        AudioSessionDiagnostics.OnClickPlayed();
        RunParkedWork();
        Check.That(AudioSessionDiagnostics.SilentClickReports).IsEqualTo(0);
    }

    [Fact]
    public void AnUnreadableMeter_CountsNeitherAndWritesNoSilentLine()
    {
        var (_, _, _) = ConfigureWithMeter((float?)null);
        AudioSessionDiagnostics.OnShowStarted();
        AudioSessionDiagnostics.OnClickPlayed();
        RunParkedWork();

        var counts = AudioSessionDiagnostics.TakeMeterCounts(10);
        Check.That(counts.Reached).IsEqualTo(0);
        Check.That(counts.Silent).IsEqualTo(0);
        Check.That(counts.Pending).IsEqualTo(0);
        Check.That(AudioSessionDiagnostics.SilentClickReports).IsEqualTo(0);
    }

    [Fact]
    public void TheShowWindowResets_SoAVerdictNeverLeaksIntoTheNextShow()
    {
        var (_, _, _) = ConfigureWithMeter(FakeMeter.Caught);
        AudioSessionDiagnostics.OnShowStarted();
        AudioSessionDiagnostics.OnClickPlayed();
        RunParkedWork();
        Check.That(AudioSessionDiagnostics.TakeMeterCounts(10).Reached).IsEqualTo(1);

        AudioSessionDiagnostics.OnShowStarted(); // show 2, no clicks
        var counts = AudioSessionDiagnostics.TakeMeterCounts(10);
        Check.WithCustomMessage("a reached click of the previous show leaked into the next show's line")
            .That(counts.Reached).IsEqualTo(0);
    }

    [Fact]
    public void APendingSample_IsReportedAsPending_NotAsReachedOrSilent()
    {
        var (_, _, _) = ConfigureWithMeter(FakeMeter.Caught);
        AudioSessionDiagnostics.OnShowStarted();
        AudioSessionDiagnostics.OnClickPlayed(); // parked: the sampler has not run yet

        var counts = AudioSessionDiagnostics.TakeMeterCounts(10);
        Check.That(counts.Pending).IsEqualTo(1);
        Check.That(counts.Reached).IsEqualTo(0);
        Check.That(counts.Silent).IsEqualTo(0);

        RunParkedWork(); // the verdict lands late — the PENDING count is what the hide line saw
        Check.That(AudioSessionDiagnostics.TakeMeterCounts(10).Reached).IsEqualTo(1);
    }

    [Fact]
    public void OnClickPlayed_DoesNotRunTheMeterSynchronously()
    {
        // F10's copy of review 01 F4: the sampler must be queued, never run on the key path.
        var (_, _, meter) = ConfigureWithMeter(FakeMeter.Caught);
        AudioSessionDiagnostics.OnShowStarted();
        AudioSessionDiagnostics.OnClickPlayed();
        Check.That(meter.Calls).IsEqualTo(0); // nothing yet
        Check.That(Parked).HasSize(1);
        RunParkedWork();
        Check.That(meter.Calls).IsEqualTo(1);
    }

    [Fact]
    public void ClickToActiveLatency_IsCountedAsLateAboveTheThreshold_AndBoundedByMax()
    {
        // The add-on: 13 clicks started ~0.4 s late after idle gaps. The delay comes from the
        // session state callback, not from the meter, so it is measurable with no meter too.
        var (_, listener) = Configure();
        AudioSessionDiagnostics.OnClickAttempted(); // attach the fake listener so Emit reaches it
        RunParkedWork();
        AudioSessionDiagnostics.OnShowStarted();
        AudioSessionDiagnostics.OnClickPlayed();
        Thread.Sleep(AudioSessionDiagnostics.LateStartThresholdMs + 30);
        listener.Emit(new AudioSessionDiagnostics.AudioChange(
            AudioSessionDiagnostics.AudioChangeKind.SessionState,
            AudioSessionDiagnostics.FormatSessionStateChange("Active"), "Active"));

        var counts = AudioSessionDiagnostics.TakeMeterCounts(10);
        Check.That(counts.Late).IsEqualTo(1);
        Check.That(counts.MaxLateMs).IsStrictlyGreaterThan(AudioSessionDiagnostics.LateStartThresholdMs - 1);
    }

    [Fact]
    public void AFastActiveTransition_IsMeasuredButNotLate()
    {
        var (_, listener) = Configure();
        AudioSessionDiagnostics.OnClickAttempted(); // attach the fake listener so Emit reaches it
        RunParkedWork();
        AudioSessionDiagnostics.OnShowStarted();
        AudioSessionDiagnostics.OnClickPlayed();
        listener.Emit(new AudioSessionDiagnostics.AudioChange(
            AudioSessionDiagnostics.AudioChangeKind.SessionState,
            AudioSessionDiagnostics.FormatSessionStateChange("Active"), "Active"));

        var counts = AudioSessionDiagnostics.TakeMeterCounts(10);
        Check.That(counts.Late).IsEqualTo(0);
        Check.That(counts.MaxLateMs).IsStrictlyLessThan(AudioSessionDiagnostics.LateStartThresholdMs);
    }

    [Fact]
    public void AnActiveTransitionWithoutAClickRequest_IsNotMeasured()
    {
        // Another of the app's sessions could go Active (or the callback could arrive twice);
        // a start without a pending click request must not invent a 0 ms measurement.
        var (_, listener) = Configure();
        AudioSessionDiagnostics.OnClickAttempted(); // attach the fake listener so Emit reaches it
        RunParkedWork();
        AudioSessionDiagnostics.OnShowStarted();
        listener.Emit(new AudioSessionDiagnostics.AudioChange(
            AudioSessionDiagnostics.AudioChangeKind.SessionState,
            AudioSessionDiagnostics.FormatSessionStateChange("Active"), "Active"));
        var counts = AudioSessionDiagnostics.TakeMeterCounts(10);
        Check.That(counts.Late).IsEqualTo(0);
        Check.That(counts.MaxLateMs).IsEqualTo(0);
    }

    [Fact]
    public void OnShowStarted_DropsThePreviousShowsMeasuredClicks()
    {
        var (_, listener) = Configure();
        AudioSessionDiagnostics.OnClickAttempted(); // attach the fake listener so Emit reaches it
        RunParkedWork();
        AudioSessionDiagnostics.OnShowStarted();
        AudioSessionDiagnostics.OnClickPlayed(); // show 1, never went Active
        AudioSessionDiagnostics.OnShowStarted(); // show 2 starts before that Active arrives
        listener.Emit(new AudioSessionDiagnostics.AudioChange(
            AudioSessionDiagnostics.AudioChangeKind.SessionState,
            AudioSessionDiagnostics.FormatSessionStateChange("Active"), "Active"));

        var counts = AudioSessionDiagnostics.TakeMeterCounts(10);
        Check.That(counts.Late).IsEqualTo(0);
        Check.That(counts.MaxLateMs).IsEqualTo(0);
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

            // Run the parked diagnostics work: the failure report must be latched to ONE LINE,
            // and a late transition after the latch must not report again (review 05 V1: the
            // COUNT, not a bool, is what detects a second line — mutant M10).
            RunParkedWork();
            Check.That(AudioSessionDiagnostics.FailureLogged).IsTrue();
            Check.That(AudioSessionDiagnostics.FailureReportCount).IsEqualTo(1);
            AudioSessionDiagnostics.OnKeyboardTransition();
            RunParkedWork();
            Check.That(AudioSessionDiagnostics.FailureReportCount).IsEqualTo(1); // exactly one line, ever
        }
        finally { ResetStatics(); }
    }

    [Fact]
    public void EnsureProductionWiring_RestoresTheCoreAudioSet_AfterTestsUnsetIt()
    {
        // Review 01 F1 (inverted — the old Unconfigured_DoesNothing pinned the broken state):
        // production must NEVER run unwired. KeyboardCoordinator.LoadKeyboard calls
        // EnsureProductionWiring before it builds the sound service; this is that call's contract.
        // The unwired first click must not throw (the observer-side line is emitted honestly).
        // The parked work is completed only AFTER the set is reset again, so no real COM runs
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
                    "EnsureProductionWiring must install the REAL Core Audio reader, observer and "
                    + "meter — production without them would never read state, register listeners "
                    + "or meter a click")
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
    public void QueuedMeterSample_AfterShutdown_CountsNothing()
    {
        var (_, _, meter) = ConfigureWithMeter(FakeMeter.Caught);
        AudioSessionDiagnostics.OnShowStarted();
        AudioSessionDiagnostics.OnClickPlayed(); // parked sampler
        AudioSessionDiagnostics.Shutdown(); // latch
        RunParkedWork();

        Check.That(meter.Calls).IsEqualTo(0); // the sampler never touched COM after the latch
        Check.That(AudioSessionDiagnostics.TakeMeterCounts(10).Reached).IsEqualTo(0);
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
    public void Shutdown_Twice_ForwardsOnce()
    {
        // F10: a second app-level shutdown must be a no-op — nothing to unregister twice and no
        // second pool teardown in a dying process.
        var (_, listener) = Configure();
        AudioSessionDiagnostics.Shutdown();
        AudioSessionDiagnostics.Shutdown();
        var deadline = Environment.TickCount64 + 2000;
        while (listener.ShutdownCalls == 0 && Environment.TickCount64 < deadline) Thread.Sleep(10);
        Thread.Sleep(100); // give a wrongly queued second teardown time to land
        Check.That(listener.ShutdownCalls).IsEqualTo(1);
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
        // endpoint volume registration, the meter, the session control) simply go out of scope;
        // only native memory is cleared explicitly. Comments are stripped first.
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

    [Fact]
    public void AWorkerRegistrationAcquiresTheEnumerator_AndRegistersEveryInstrument()
    {
        // Review 05 V3 + F10: WITHOUT real COM — managed fakes through the factory seam prove
        // TryRegister ACQUIRES the shared enumerator and attaches all THREE endpoint-level
        // instruments (device notifications, master volume/mute, ducking). Mutant L1 (the
        // `_enumerator ??=` removed) throws here; dropping any registration turns this red.
        var world = new FakeComWorld();
        CoreAudioInterop.EnumeratorFactoryForTesting = () => world.Enumerator;
        CoreAudioInterop.ResetSharedEnumeratorForTesting();
        try
        {
            var observer = new CoreAudioInterop.CoreAudioChangeObserver();
            Check.That(observer.HasEnumerator).IsFalse(); // construction: still lazy

            Check.That(observer.TryRegister(_ => { })).IsTrue();
            Check.WithCustomMessage(
                    "a worker registration did not acquire the enumerator — the endpoint callbacks "
                    + "would never register (review 05 V3, mutant L1)")
                .That(observer.HasEnumerator).IsTrue();
            Check.That(world.Enumerator.Registrations).IsEqualTo(1); // idempotent: one
            Check.That(world.EndpointVolume.Registrations).IsEqualTo(1);
            Check.That(world.Manager.DuckRegistrations).IsEqualTo(1);

            Check.That(observer.TryRegister(_ => { })).IsTrue();
            Check.That(world.Enumerator.Registrations).IsEqualTo(1);
            Check.That(world.EndpointVolume.Registrations).IsEqualTo(1);
            Check.That(world.Manager.DuckRegistrations).IsEqualTo(1);
        }
        finally
        {
            CoreAudioInterop.EnumeratorFactoryForTesting = null;
            CoreAudioInterop.ResetSharedEnumeratorForTesting();
        }
    }

    [Fact]
    public void Shutdown_UnregistersEveryCallback_AndASecondShutdownIsANoOp()
    {
        // F10's hard COM rule: every registration is unregistered on shutdown, and a second
        // shutdown touches nothing.
        var world = new FakeComWorld();
        CoreAudioInterop.EnumeratorFactoryForTesting = () => world.Enumerator;
        CoreAudioInterop.ResetSharedEnumeratorForTesting();
        try
        {
            var observer = new CoreAudioInterop.CoreAudioChangeObserver();
            Check.That(observer.TryRegister(_ => { })).IsTrue();

            observer.Shutdown();
            Check.That(world.Enumerator.Unregistrations).IsEqualTo(1);
            Check.That(world.EndpointVolume.Unregistrations).IsEqualTo(1);
            Check.That(world.Manager.DuckUnregistrations).IsEqualTo(1);

            observer.Shutdown(); // second: no-op
            Check.That(world.Enumerator.Unregistrations).IsEqualTo(1);
            Check.That(world.EndpointVolume.Unregistrations).IsEqualTo(1);
            Check.That(world.Manager.DuckUnregistrations).IsEqualTo(1);
        }
        finally
        {
            CoreAudioInterop.EnumeratorFactoryForTesting = null;
            CoreAudioInterop.ResetSharedEnumeratorForTesting();
        }
    }

    [Fact]
    public void TheEndpointVolumeCallback_LogsOneLinePerActualChange()
    {
        // F10 item 1: a line whenever the master volume OR mute changes — and nothing for the
        // duplicate notifications Windows sends for the same state. Driven directly with a
        // native-shaped payload; no COM, no device.
        var lines = new List<string>();
        var callback = new CoreAudioInterop.EndpointVolumeCallback(
            change => lines.Add(change.Line));

        NotifyVolume(callback, 0.65f, mute: false);
        Check.That(lines).ContainsExactly("Keyboard audio change: endpoint volume=0.65 mute=False");

        NotifyVolume(callback, 0.65f, mute: false); // duplicate: swallowed
        Check.That(lines).HasSize(1);

        NotifyVolume(callback, 0.40f, mute: false);
        Check.That(lines).HasSize(2);
        Check.That(lines[1]).IsEqualTo("Keyboard audio change: endpoint volume=0.40 mute=False");

        NotifyVolume(callback, 0.40f, mute: true);
        Check.That(lines).HasSize(3);
        Check.That(lines[2]).IsEqualTo("Keyboard audio change: endpoint volume=0.40 mute=True");
    }

    [Fact]
    public void TheDuckNotification_LogsDuckAndUnduck()
    {
        // F10 item 3. Driven directly; in production Windows calls this on a WASAPI thread.
        var lines = new List<string>();
        var callback = new CoreAudioInterop.DuckNotification(change => lines.Add(change.Line));

        callback.OnVolumeDuckNotification("session-id", 2);
        callback.OnVolumeUnduckNotification("session-id");

        Check.That(lines).ContainsExactly(
            "Keyboard duck: communications ducking started (sessions=2)",
            "Keyboard duck: communications ducking ended");
    }

    /// <summary>Builds the native AUDIO_VOLUME_NOTIFICATION_DATA prefix, calls OnNotify, and frees
    /// it — the callback copies what it needs synchronously.</summary>
    private static void NotifyVolume(
        CoreAudioInterop.EndpointVolumeCallback callback, float volume, bool mute)
    {
        var data = new CoreAudioInterop.AudioVolumeNotificationData
        {
            EventContext = Guid.Empty,
            Muted = mute,
            MasterVolume = volume,
            ChannelCount = 1,
        };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<CoreAudioInterop.AudioVolumeNotificationData>());
        try
        {
            Marshal.StructureToPtr(data, ptr, false);
            callback.OnNotify(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    // ---- managed fakes for the COM graph the observer walks (no real COM) ----

    private sealed class FakeComWorld
    {
        public readonly FakeDeviceEnumerator Enumerator = new();
        public readonly FakeDevice Device;
        public readonly FakeEndpointVolume EndpointVolume = new();
        public readonly FakeSessionManager Manager = new();

        public FakeComWorld()
        {
            Device = new FakeDevice(EndpointVolume, Manager);
            Enumerator.Device = Device;
        }
    }

    private sealed class FakeDeviceEnumerator : CoreAudioInterop.IMMDeviceEnumerator
    {
        public FakeDevice? Device;
        public int Registrations;
        public int Unregistrations;

        public int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices)
        {
            devices = IntPtr.Zero;
            return unchecked((int)0x80004005);
        }

        public int GetDefaultAudioEndpoint(int dataFlow, int role, out CoreAudioInterop.IMMDevice device)
        {
            device = Device!;
            return Device is null ? unchecked((int)0x80004005) : 0;
        }

        public int GetDevice(string deviceId, out CoreAudioInterop.IMMDevice device)
        {
            device = Device!;
            return Device is null ? unchecked((int)0x80004005) : 0;
        }

        public int RegisterEndpointNotificationCallback(CoreAudioInterop.IMMNotificationClient client)
        {
            Registrations++;
            return 0;
        }

        public int UnregisterEndpointNotificationCallback(CoreAudioInterop.IMMNotificationClient client)
        {
            Unregistrations++;
            return 0;
        }
    }

    private sealed class FakeDevice(
        FakeEndpointVolume endpointVolume, FakeSessionManager manager) : CoreAudioInterop.IMMDevice
    {
        public int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out object iface)
        {
            if (iid == CoreAudioInterop.IidIAudioEndpointVolume) { iface = endpointVolume; return 0; }
            if (iid == CoreAudioInterop.IidIAudioSessionManager2) { iface = manager; return 0; }
            iface = null!;
            return unchecked((int)0x80004002); // E_NOINTERFACE
        }

        public int OpenPropertyStore(int stgmAccess, out CoreAudioInterop.IPropertyStore properties)
        {
            properties = null!;
            return unchecked((int)0x80004005);
        }

        public int GetId(out string deviceId)
        {
            deviceId = "fake";
            return 0;
        }

        public int GetState(out int state)
        {
            state = 1; // DEVICE_STATE_ACTIVE
            return 0;
        }
    }

    private sealed class FakeEndpointVolume : CoreAudioInterop.IAudioEndpointVolume
    {
        public int Registrations;
        public int Unregistrations;

        public int RegisterControlChangeNotify(CoreAudioInterop.IAudioEndpointVolumeCallback pNotify)
        {
            Registrations++;
            return 0;
        }

        public int UnregisterControlChangeNotify(CoreAudioInterop.IAudioEndpointVolumeCallback pNotify)
        {
            Unregistrations++;
            return 0;
        }

        public int GetMasterVolumeLevelScalar(out float level) { level = 0.65f; return 0; }
        public int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute) { mute = false; return 0; }
        public int GetChannelCount(out int channelCount) { channelCount = 2; return 0; }
        public int SetMasterVolumeLevel(float levelDb, ref Guid eventContext) => unchecked((int)0x80004005);
        public int SetMasterVolumeLevelScalar(float level, ref Guid eventContext) => unchecked((int)0x80004005);
        public int GetMasterVolumeLevel(out float levelDb) { levelDb = 0; return unchecked((int)0x80004005); }
        public int SetChannelVolumeLevel(int channel, float levelDb, ref Guid eventContext) => unchecked((int)0x80004005);
        public int SetChannelVolumeLevelScalar(int channel, float level, ref Guid eventContext) => unchecked((int)0x80004005);
        public int GetChannelVolumeLevel(int channel, out float levelDb) { levelDb = 0; return unchecked((int)0x80004005); }
        public int GetChannelVolumeLevelScalar(int channel, out float level) { level = 0; return unchecked((int)0x80004005); }
        public int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext) => unchecked((int)0x80004005);
        public int GetVolumeStepInfo(out int step, out int stepCount) { step = 0; stepCount = 0; return unchecked((int)0x80004005); }
        public int VolumeStepUp(ref Guid eventContext) => unchecked((int)0x80004005);
        public int VolumeStepDown(ref Guid eventContext) => unchecked((int)0x80004005);
        public int QueryHardwareSupport(out int hardwareSupportMask) { hardwareSupportMask = 0; return unchecked((int)0x80004005); }
        public int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb)
        { minDb = 0; maxDb = 0; incrementDb = 0; return unchecked((int)0x80004005); }
    }

    private sealed class FakeSessionManager : CoreAudioInterop.IAudioSessionManager2
    {
        public int DuckRegistrations;
        public int DuckUnregistrations;

        public int GetAudioSessionControl(IntPtr audioSessionGuid, int streamFlags, out IntPtr sessionControl)
        { sessionControl = IntPtr.Zero; return unchecked((int)0x80004005); }

        public int GetSimpleAudioVolume(ref Guid audioSessionGuid, int streamFlags,
            out CoreAudioInterop.ISimpleAudioVolume audioVolume)
        { audioVolume = null!; return unchecked((int)0x80004005); }

        public int GetSessionEnumerator(out CoreAudioInterop.IAudioSessionEnumerator sessionEnumerator)
        { sessionEnumerator = null!; return unchecked((int)0x80004005); }

        public int RegisterSessionNotification(IntPtr sessionNotification) => unchecked((int)0x80004005);
        public int UnregisterSessionNotification(IntPtr sessionNotification) => unchecked((int)0x80004005);

        public int RegisterDuckNotification(string? sessionId,
            CoreAudioInterop.IAudioVolumeDuckNotification duckNotification)
        { DuckRegistrations++; return 0; }

        public int UnregisterDuckNotification(CoreAudioInterop.IAudioVolumeDuckNotification duckNotification)
        { DuckUnregistrations++; return 0; }
    }

    private sealed class ThrowingListener : AudioSessionDiagnostics.IAudioChangeListener
    {
        public bool TryRegister(Action<AudioSessionDiagnostics.AudioChange> onChangeLine) => throw new InvalidOperationException("com gone");
        public bool TryRegisterSessionEvents() => throw new InvalidOperationException("com gone");
        public void Shutdown() { }
    }

    private sealed class BlockingListener(ManualResetEventSlim release) : AudioSessionDiagnostics.IAudioChangeListener
    {
        public bool TryRegister(Action<AudioSessionDiagnostics.AudioChange> onChangeLine)
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
