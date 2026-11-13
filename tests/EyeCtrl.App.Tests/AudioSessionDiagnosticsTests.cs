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
        AudioSessionDiagnostics.WorkScheduler = a => a(); // deterministic: inline, like the old sync path
        return (reader, listener);
    }

    private static void ResetStatics()
    {
        // Drain the queue first: a pool-thread work item that lands after this reset would run
        // against the NEXT test's statics.
        AudioSessionDiagnostics.DrainQueuedWork();
        AudioSessionDiagnostics.LineSink = null;
        AudioSessionDiagnostics.WorkScheduler = null;
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
        AudioSessionDiagnostics.WorkScheduler = a => a(); // deterministic: inline
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
        AudioSessionDiagnostics.WorkScheduler = a => a(); // deterministic: inline
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
    public void EnsureProductionWiring_RestoresTheCoreAudioPair_AfterTestsUnsetIt()
    {
        // Review 01 F1 (inverted — the old Unconfigured_DoesNothing pinned the broken state):
        // production must NEVER run unwired. KeyboardCoordinator.LoadKeyboard calls
        // EnsureProductionWiring before it builds the sound service; this is that call's contract.
        AudioSessionDiagnostics.ConfigureForTesting(null, null);
        AudioSessionDiagnostics.WorkScheduler = a => a(); // deterministic: inline
        Lines.Clear();
        AudioSessionDiagnostics.LineSink = Lines.Add;
        try
        {
            // Unset and unwired: the state line must say so honestly.
            AudioSessionDiagnostics.OnClickAttempted();
            Check.That(Lines).ContainsExactly("Keyboard audio: diagnostics not configured (no reader wired)");

            AudioSessionDiagnostics.EnsureProductionWiring();
            Check.WithCustomMessage(
                    "EnsureProductionWiring must install the REAL Core Audio reader and observer — "
                    + "production without them logs 'not configured' forever and registers nothing")
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
        Lines.Clear();
        AudioSessionDiagnostics.LineSink = Lines.Add;
        Action? parked = null;
        AudioSessionDiagnostics.WorkScheduler = a => parked = a; // park: the work never runs by itself
        try
        {
            var player = new AlwaysTruePlayer();
            var svc = new KeyboardSoundService(new KeyboardSettings(), "irrelevant.wav", _ => player)
            {
                DiagnosticsLineSink = _ => { },
            };

            // BISECT A: direct diagnostics call, no service.
            AudioSessionDiagnostics.OnClickAttempted();
            Check.WithCustomMessage(
                    "the first click ran Core Audio work synchronously — a cold read costs ~100 ms "
                    + "and must never sit in front of a key; the click only flips the once-flag")
                .That(reader.Reads).IsEqualTo(0);
            Check.WithCustomMessage(
                    "the click neither ran the work synchronously nor scheduled it — the diagnostics "
                    + "would never fire at all")
                .That(parked).IsNotNull();

            // Releasing the park IS the background thread doing its work later.
            parked!.Invoke();
            Check.That(reader.Reads).IsEqualTo(1);
            Check.That(Lines).HasSize(1);
            Check.That(Lines[0]).Contains("Keyboard audio: endpoint=");
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

/// <summary>
/// Review 01 F2's pins: every COM interface's IID must equal the Windows SDK header value, and
/// every callback/vtable interface must declare its methods in exact header order. A wrong IID
/// makes the OS answer E_NOINTERFACE (the instrument silently never runs); a wrong order or a
/// dropped slot makes WASAPI call past the CCW's vtable — an access violation mid-typing.
/// Each expected value cites its SDK header and line (Windows Kits 10.0.26100.0).
/// No COM is touched: these are pure metadata assertions.
/// </summary>
public class CoreAudioInteropPinTests
{
    private static void PinIid<T>(string expected, string citation) =>
        Check.WithCustomMessage($"the IID of {typeof(T).Name} drifted from the SDK ({citation})")
            .That(typeof(T).GUID.ToString("D")).IsEqualTo(expected.ToLowerInvariant());

    [Fact]
    public void TheIids_AreTheSdkHeaderValues()
    {
        // mmdeviceapi.h
        PinIid<CoreAudioInterop.IMMDeviceEnumerator>("A95664D2-9614-4F35-A746-DE8DB63617E6", "mmdeviceapi.h:757");
        PinIid<CoreAudioInterop.IMMDevice>("D666063F-1587-4E43-81F1-B948E807363F", "mmdeviceapi.h:427");
        PinIid<CoreAudioInterop.IMMNotificationClient>("7991EEC9-7E89-4D85-8390-6C703CEC60C0", "mmdeviceapi.h:273");
        PinIid<CoreAudioInterop.IPropertyStore>("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99", "propsys.h:516");
        // audiopolicy.h
        PinIid<CoreAudioInterop.IAudioSessionManager2>("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F", "audiopolicy.h:1155");
        PinIid<CoreAudioInterop.IAudioSessionEnumerator>("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8", "audiopolicy.h:1058");
        PinIid<CoreAudioInterop.IAudioSessionControl>("F4B1A599-7266-4319-A8CA-E70ACB11E8CD", "audiopolicy.h:337");
        PinIid<CoreAudioInterop.IAudioSessionControl2>("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d", "audiopolicy.h:545");
        PinIid<CoreAudioInterop.IAudioSessionEvents>("24918ACC-64B3-37C1-8CA9-74A66E9957A8", "audiopolicy.h:151");
        // Audioclient.h
        PinIid<CoreAudioInterop.ISimpleAudioVolume>("87CE5498-68D6-44E5-9215-6DA47EF883D8", "Audioclient.h:1642");
        PinIid<CoreAudioInterop.MMDeviceEnumeratorComObject>("BCDE0395-E52F-467C-8E3D-C4579291692E", "mmdeviceapi.h:1541 (CLSID)");
    }

    private static void PinOrder<T>(string[] expected, string citation) =>
        Check.WithCustomMessage($"the vtable order of {typeof(T).Name} drifted from the SDK ({citation}) — "
            + "a shifted slot makes WASAPI call the wrong managed method or past the vtable entirely")
            .That(typeof(T).GetMethods().Select(m => m.Name).ToArray())
            .ContainsExactly(expected);

    [Fact]
    public void TheCallbackVtables_AreInSdkHeaderOrder()
    {
        // audiopolicy.h:155-190 — the full seven-slot order, OnChannelVolumeChanged and
        // OnGroupingParamChanged included (the two slots whose absence was review 01 F2).
        PinOrder<CoreAudioInterop.IAudioSessionEvents>(
            ["OnDisplayNameChanged", "OnIconPathChanged", "OnSimpleVolumeChanged",
             "OnChannelVolumeChanged", "OnGroupingParamChanged", "OnStateChanged", "OnSessionDisconnected"],
            "audiopolicy.h:155-190");
        // mmdeviceapi.h:277-299.
        PinOrder<CoreAudioInterop.IMMNotificationClient>(
            ["OnDeviceStateChanged", "OnDeviceAdded", "OnDeviceRemoved", "OnDefaultDeviceChanged", "OnPropertyValueChanged"],
            "mmdeviceapi.h:277-299");
        // audiopolicy.h:337+ (control) + :549-563 (control2 tail).
        PinOrder<CoreAudioInterop.IAudioSessionControl2>(
            ["GetState", "GetDisplayName", "SetDisplayName", "GetIconPath", "SetIconPath",
             "GetGroupingParam", "SetGroupingParam", "RegisterAudioSessionNotification",
             "UnregisterAudioSessionNotification", "GetSessionIdentifier", "GetSessionInstanceIdentifier",
             "GetProcessId", "IsSystemSoundsSession", "SetDuckingPreference"],
            "audiopolicy.h:337-563");
        // Audioclient.h:1646-1660.
        PinOrder<CoreAudioInterop.ISimpleAudioVolume>(
            ["SetMasterVolume", "GetMasterVolume", "SetMute", "GetMute"],
            "Audioclient.h:1646-1660");
        // mmdeviceapi.h:431-451.
        PinOrder<CoreAudioInterop.IMMDevice>(
            ["Activate", "OpenPropertyStore", "GetId", "GetState"],
            "mmdeviceapi.h:431-451");
        // mmdeviceapi.h:761-787.
        PinOrder<CoreAudioInterop.IMMDeviceEnumerator>(
            ["EnumAudioEndpoints", "GetDefaultAudioEndpoint", "GetDevice",
             "RegisterEndpointNotificationCallback", "UnregisterEndpointNotificationCallback"],
            "mmdeviceapi.h:761-787");
        // audiopolicy.h:1062-1065.
        PinOrder<CoreAudioInterop.IAudioSessionEnumerator>(
            ["GetCount", "GetSession"],
            "audiopolicy.h:1062-1065");
    }
}
