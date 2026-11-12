namespace EyeCtrl.App.Tests;

using System.Collections.Generic;
using System.Globalization;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

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

/// <summary>
/// F5's diagnostics half, all behind the <see cref="AudioSessionDiagnostics"/> seams (no real
/// COM, no real audio): the snapshot and change line formats (word-for-word, culture-invariant),
/// the once-per-app-lifetime first-click guard, the show/hide baseline snapshots, the
/// registration retry for a session that winmm has not created yet, and — the owner-safety
/// rule — that a Core Audio failure can never throw into the click path and never blocks a
/// click. A source pin holds the coordinator's forwarding of the real transitions in place.
/// <para>
/// Runs in the exclusive "KeyboardAudioDiagnostics" collection (see
/// <see cref="KeyboardAudioDiagnosticsCollection"/> for why).
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

    private static readonly List<string> Lines = new();

    private static (FakeReader Reader, FakeListener Listener) Configure()
    {
        Lines.Clear();
        var reader = new FakeReader();
        var listener = new FakeListener();
        AudioSessionDiagnostics.ConfigureForTesting(reader, listener);
        AudioSessionDiagnostics.LineSink = Lines.Add;
        return (reader, listener);
    }

    private static void ResetStatics()
    {
        AudioSessionDiagnostics.LineSink = null;
        AudioSessionDiagnostics.ConfigureForTesting(null, null);
        Lines.Clear();
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
        try
        {
            AudioSessionDiagnostics.OnClickAttempted();
            AudioSessionDiagnostics.OnClickAttempted();
            AudioSessionDiagnostics.OnClickAttempted();

            Check.That(reader.Reads).IsEqualTo(1);
            Check.That(listener.RegisterCalls).IsEqualTo(1);
            Check.That(listener.SessionRegisterCalls).IsEqualTo(1);
            Check.That(Lines).ContainsExactly(
                "Keyboard audio: endpoint=\"Speakers\" session: volume=0.80 mute=False state=Active");
        }
        finally { ResetStatics(); }
    }

    [Fact]
    public void Transitions_BeforeAnyClick_DoNothing()
    {
        var (reader, listener) = Configure();
        try
        {
            AudioSessionDiagnostics.OnKeyboardTransition();
            AudioSessionDiagnostics.OnKeyboardTransition();

            Check.That(reader.Reads).IsEqualTo(0);
            Check.That(listener.RegisterCalls).IsEqualTo(0);
            Check.That(Lines).IsEmpty();
        }
        finally { ResetStatics(); }
    }

    [Fact]
    public void Transitions_AfterFirstClick_Snapshot_ButNeverReRegisterCompleteAttachments()
    {
        var (reader, listener) = Configure();
        try
        {
            AudioSessionDiagnostics.OnClickAttempted();
            AudioSessionDiagnostics.OnKeyboardTransition();
            AudioSessionDiagnostics.OnKeyboardTransition();

            Check.That(reader.Reads).IsEqualTo(3); // first click + two baselines
            Check.That(listener.RegisterCalls).IsEqualTo(1);
            Check.That(listener.SessionRegisterCalls).IsEqualTo(1);
            Check.That(Lines).HasSize(3);
            Check.That(Lines[1]).Contains("Keyboard audio: endpoint=");
            Check.That(Lines[2]).Contains("Keyboard audio: endpoint=");
        }
        finally { ResetStatics(); }
    }

    [Fact]
    public void FailedEndpointRegistration_IsRetriedOnTheNextTransition()
    {
        var reader = new FakeReader();
        var listener = new FakeListener { RegisterResult = false };
        AudioSessionDiagnostics.ConfigureForTesting(reader, listener);
        Lines.Clear();
        AudioSessionDiagnostics.LineSink = Lines.Add;
        try
        {
            AudioSessionDiagnostics.OnClickAttempted();
            Check.That(listener.RegisterCalls).IsEqualTo(1);

            AudioSessionDiagnostics.OnKeyboardTransition();
            Check.WithCustomMessage(
                    "an endpoint listener that failed to attach must be retried at the next real "
                    + "transition — registration is the only way change events ever fire")
                .That(listener.RegisterCalls).IsEqualTo(2);
        }
        finally { ResetStatics(); }
    }

    [Fact]
    public void SessionEvents_RegistrationIsRetriedUntilItAttaches()
    {
        var (reader, listener) = Configure();
        listener.SessionResults = new Queue<bool>([false, false, true]);
        try
        {
            AudioSessionDiagnostics.OnClickAttempted(); // attach attempt 1: false (no session yet)
            AudioSessionDiagnostics.OnKeyboardTransition(); // attempt 2: false (still none)
            AudioSessionDiagnostics.OnKeyboardTransition(); // attempt 3: true (session appeared)

            Check.That(listener.SessionRegisterCalls).IsEqualTo(3);
        }
        finally { ResetStatics(); }
    }

    // ---- the owner-safety rule: a COM failure can never throw into the click path ----

    [Fact]
    public void ThrowingCoreAudio_NeverThrows_NeverBlocksAClick_AndFailsOnceOnly()
    {
        Lines.Clear();
        var reader = new FakeReader { ThrowOnRead = true };
        var listener = new ThrowingListener();
        AudioSessionDiagnostics.ConfigureForTesting(reader, listener);
        AudioSessionDiagnostics.LineSink = Lines.Add;
        try
        {
            var lines2 = new List<string>();
            var player = new AlwaysTruePlayer();
            var svc = new KeyboardSoundService(new KeyboardSettings(), "irrelevant.wav", _ => player)
            {
                DiagnosticsLineSink = lines2.Add,
            };

            Check.ThatCode(() => svc.PlayKeyClick()).DoesNotThrow();
            Check.ThatCode(() => svc.PlayKeyClick()).DoesNotThrow();
            Check.That(player.Calls).IsEqualTo(2); // the click path never learned about the COM mess
            svc.OnKeyboardHidden();
            Check.That(lines2).ContainsExactly("Keyboard click: requested=2 played=2 failed=0 skipped=0");

            // One failure line for the app's lifetime, then silence about further failures.
            Check.That(Lines).HasSize(1);
            Check.That(Lines[0]).Contains("Keyboard audio diagnostics:");
            AudioSessionDiagnostics.OnKeyboardTransition();
            Check.That(Lines).HasSize(1);
        }
        finally { ResetStatics(); }
    }

    [Fact]
    public void Unconfigured_DoesNothing()
    {
        // Production's first seconds: nothing wired yet — a click must not blow up on a null reader.
        AudioSessionDiagnostics.ConfigureForTesting(null, null);
        Lines.Clear();
        AudioSessionDiagnostics.LineSink = Lines.Add;
        try
        {
            Check.ThatCode(() => AudioSessionDiagnostics.OnClickAttempted()).DoesNotThrow();
            // The reader was absent → the state-unavailable line still reported honestly.
            Check.That(Lines).ContainsExactly("Keyboard audio: state unavailable (Core Audio read failed)");
        }
        finally { ResetStatics(); }
    }

    [Fact]
    public void Shutdown_ForwardsToTheListener()
    {
        var (reader, listener) = Configure();
        try
        {
            Check.ThatCode(() => AudioSessionDiagnostics.Shutdown()).DoesNotThrow();
            Check.That(listener.ShutdownCalls).IsEqualTo(1);
        }
        finally { ResetStatics(); }
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
