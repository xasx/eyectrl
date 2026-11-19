namespace EyeCtrl.App.Tests.Keyboard;

using System.Collections.Generic;
using System.Globalization;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// F5's service half: the per-show counters (requested/played/failed/skipped) behind the hide
/// line, the winmm false return as a counted — never latched — failure with one log line per
/// show, the counter window that opens on a real show, and the window's forwarding of the real
/// transitions. The player is always a fake here: no audio device is ever touched.
/// <para>
/// Shares the "KeyboardAudioDiagnostics" collection with
/// <see cref="KeyboardSoundServiceLatchTests"/> and <see cref="AudioSessionDiagnosticsTests"/>:
/// every attempted click funnels into AudioSessionDiagnostics' app-lifetime once-guard, so
/// these tests must not interleave (and no other test in the suite constructs a click that
/// reaches the guard — the prediction-click double overrides the method entirely).
/// </para>
/// </summary>
[Collection("KeyboardAudioDiagnostics")]
public class KeyboardClickSoundDiagnosticsTests : F5AudioDiagnosticsTestBase
{
    private sealed class FakePlayer(bool result) : IClickSoundPlayer
    {
        public int Calls;
        public bool Play() { Calls++; return result; }
    }

    private sealed class ScriptedMeter(params float?[] peaks) : AudioSessionDiagnostics.IAudioMeterProbe
    {
        private readonly Queue<float?> _peaks = new(peaks);
        public float? SamplePeak(Func<bool> stop) => _peaks.Count > 0 ? _peaks.Dequeue() : null;
    }

    private static KeyboardSoundService Service(IClickSoundPlayer player, List<string> lines) =>
        new(new KeyboardSettings(), "irrelevant.wav", _ => player) { DiagnosticsLineSink = lines.Add };

    [Fact]
    public void PlayedClicks_CountedAndReportedOnTheHideLine()
    {
        var lines = new List<string>();
        var player = new FakePlayer(true);
        var svc = Service(player, lines);

        svc.PlayKeyClick();
        svc.PlayKeyClick();
        Check.That(lines).IsEmpty(); // nothing between keys — the hide line is the report
        svc.OnKeyboardHidden();

        Check.That(lines).ContainsExactly("Keyboard click: requested=2 played=2 failed=0 skipped=0 reached=0 silent=0 pending=0 late=0 maxlatems=0");
    }

    [Fact]
    public void FalseReturn_CountsAsFailed_DoesNotLatch_LogsOnlyTheFirstFailurePerShow()
    {
        var lines = new List<string>();
        var player = new FakePlayer(false);
        var svc = Service(player, lines);

        svc.PlayKeyClick();
        svc.PlayKeyClick();
        svc.PlayKeyClick();

        // No latch: the sound came back for the owner, so every later key must attempt again.
        Check.WithCustomMessage(
                "a false PlaySound return must never latch — the next key must re-attempt it")
            .That(player.Calls).IsEqualTo(3);

        svc.OnKeyboardHidden();
        Check.That(lines).HasSize(2);
        Check.That(lines[0]).IsEqualTo(
            "Key click: PlaySound returned false (first failure this show; later ones count only).");
        Check.That(lines[1]).IsEqualTo("Keyboard click: requested=3 played=0 failed=3 skipped=0 reached=0 silent=0 pending=0 late=0 maxlatems=0");

        // A new show opens a new window: the first failure of THAT show logs again.
        svc.OnKeyboardShown();
        svc.PlayKeyClick();
        svc.OnKeyboardHidden();
        Check.That(lines).HasSize(4);
        Check.That(lines[2]).Contains("PlaySound returned false");
        Check.That(lines[3]).IsEqualTo("Keyboard click: requested=1 played=0 failed=1 skipped=0 reached=0 silent=0 pending=0 late=0 maxlatems=0");
    }

    [Fact]
    public void SettingOff_ClicksCountAsSkips_PlayerNeverTouched()
    {
        var lines = new List<string>();
        var player = new FakePlayer(true);
        var settings = new KeyboardSettings();
        settings.ClickSoundEnabled = false;
        var svc = new KeyboardSoundService(settings, "irrelevant.wav", _ => player)
        {
            DiagnosticsLineSink = lines.Add,
        };

        svc.PlayKeyClick();
        svc.PlayKeyClick();
        svc.OnKeyboardHidden();

        Check.That(player.Calls).IsEqualTo(0);
        Check.That(lines).ContainsExactly("Keyboard click: requested=2 played=0 failed=0 skipped=2 reached=0 silent=0 pending=0 late=0 maxlatems=0");
    }

    [Fact]
    public void Shown_OpensANewCounterWindow()
    {
        var lines = new List<string>();
        var player = new FakePlayer(true);
        var svc = Service(player, lines);

        svc.PlayKeyClick(); // pre-show click — must vanish from this show's line
        svc.OnKeyboardShown();
        svc.PlayKeyClick();
        svc.OnKeyboardHidden();

        Check.WithCustomMessage(
                "OnKeyboardShown must reset the counters: a show that did not reset would report "
                + "clicks that happened before the keyboard was up")
            .That(lines).ContainsExactly("Keyboard click: requested=1 played=1 failed=0 skipped=0 reached=0 silent=0 pending=0 late=0 maxlatems=0");
    }

    [Fact]
    public void TheHideLine_CarriesTheMeterVerdicts_AndTheFirstSilentClickReportsOnce()
    {
        // F10 item 2 end to end through the service: the hide line counts what the endpoint
        // meter saw, and the first silent click reports a single state line for the show.
        var lines = new List<string>();
        var player = new FakePlayer(true);
        AudioSessionDiagnostics.ConfigureForTesting(
            null, null, new ScriptedMeter(0.5f, 0f)); // reached, then silent
        var svc = Service(player, lines);
        svc.OnKeyboardShown();
        svc.PlayKeyClick();
        svc.PlayKeyClick();
        RunParkedWork();
        svc.OnKeyboardHidden();

        Check.That(lines).ContainsExactly(
            "Keyboard click: requested=2 played=2 failed=0 skipped=0 "
            + "reached=1 silent=1 pending=0 late=0 maxlatems=0");
        Check.That(AudioSessionDiagnostics.SilentClickReports).IsEqualTo(1);
    }

    [Fact]
    public void TheHideLine_ReportsAPendingSample_AndNeverBlocksTheDispatcher()
    {
        // Review-01 F2: the hide line is built on the dispatcher, so it must not wait for the
        // meter at all — a click still sampling shows as pending and its verdict lands after the
        // show. The measured path is warmed once first, then a hide with a parked sampler must
        // return in well under 20 ms.
        var lines = new List<string>();
        var player = new FakePlayer(true);
        AudioSessionDiagnostics.ConfigureForTesting(null, null, new ScriptedMeter(0.5f, 0.5f));
        var svc = Service(player, lines);

        svc.OnKeyboardShown();
        svc.PlayKeyClick();
        svc.OnKeyboardHidden(); // warm-up (also reports a pending sample)
        lines.Clear();

        svc.OnKeyboardShown();
        svc.PlayKeyClick(); // sampler parked: not run before the hide
        var sw = System.Diagnostics.Stopwatch.StartNew();
        svc.OnKeyboardHidden();
        sw.Stop();

        Check.That(lines).ContainsExactly(
            "Keyboard click: requested=1 played=1 failed=0 skipped=0 "
            + "reached=0 silent=0 pending=1 late=0 maxlatems=0");
        Check.WithCustomMessage(
                "the hide waited on the meter while running on the dispatcher (review-01 F2)")
            .That(sw.ElapsedMilliseconds).IsStrictlyLessThan(20);
    }

    [Fact]
    public void ASecondShow_OpensAFreshMeterWindow()
    {
        // Review-01 F4/R1: a service that never resets the meter window would pile show 1's
        // reached clicks into show 2's hide line.
        var lines = new List<string>();
        var player = new FakePlayer(true);
        AudioSessionDiagnostics.ConfigureForTesting(null, null, new ScriptedMeter(0.5f));
        var svc = Service(player, lines);

        svc.OnKeyboardShown();
        svc.PlayKeyClick();
        RunParkedWork();
        svc.OnKeyboardHidden();
        Check.That(lines[^1]).Contains("reached=1 silent=0 pending=0");

        lines.Clear();
        svc.OnKeyboardShown(); // show 2: no clicks at all
        svc.OnKeyboardHidden();
        Check.WithCustomMessage("show 2's hide line carried show 1's reached count")
            .That(lines[^1]).Contains("reached=0 silent=0 pending=0");
    }

    [Fact]
    public void TheHideLineFormat_IsPinnedWordForWord()
    {
        Check.That(KeyboardSoundService.FormatHideLine(41, 41, 0, 0, 0, 0, 0, 0, 0))
            .IsEqualTo("Keyboard click: requested=41 played=41 failed=0 skipped=0 reached=0 silent=0 pending=0 late=0 maxlatems=0");
        Check.That(KeyboardSoundService.FormatHideLine(0, 0, 0, 0, 0, 0, 0, 0, 0))
            .IsEqualTo("Keyboard click: requested=0 played=0 failed=0 skipped=0 reached=0 silent=0 pending=0 late=0 maxlatems=0");
    }

    [Fact]
    public void TheHideLineFormat_RendersIntegersInvariantly_UnderGermanCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Check.That(KeyboardSoundService.FormatHideLine(1, 2, 3, 4, 5, 6, 7, 8, 9))
                .IsEqualTo("Keyboard click: requested=1 played=2 failed=3 skipped=4 reached=5 silent=6 pending=7 late=8 maxlatems=9");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void TheShippedClickWav_PassesThePcm16Factory()
    {
        // Review 01 F6: DefaultPlayerFactory requires PCM-16 (WavHeader); the old SoundPlayer also
        // accepted float and 8-bit. If the shipped asset ever drifts to such a format, every click
        // would silently become "skipped" — this pin turns that into red. Plays nothing.
        var wav = System.IO.Path.Combine(
            SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Assets", "click.wav");
        Check.WithCustomMessage($"the shipped click.wav at {wav} no longer passes the PCM-16 factory: "
            + "every key click would be silently skipped")
            .That(KeyboardSoundService.DefaultPlayerFactory(wav)).IsNotNull();
    }

    [StaFact]
    public void TheWindow_ForwardsTheRealTransitionsToItsSoundService()
    {
        // The wiring half: SetSoundService + the Notify* forwards, driven headless — a real
        // KeyboardWindow that is never shown (its ctor only; no Show anywhere).
        WpfApplicationGate.EnsureApplicationWithTokens();
        var lines = new List<string>();
        var player = new FakePlayer(true);
        var svc = new KeyboardSoundService(new KeyboardSettings(), "irrelevant.wav", _ => player)
        {
            DiagnosticsLineSink = lines.Add,
        };
        var window = new EyeCtrl.App.Overlays.KeyboardWindow();
        try
        {
            window.SetSoundService(svc);

            svc.PlayKeyClick(); // pre-show click — the forwarded show must reset it away
            window.NotifyClickSoundShown();
            svc.PlayKeyClick();
            window.NotifyClickSoundHidden();

            Check.WithCustomMessage(
                    "the window must forward the real show/hide transitions to ITS sound service: "
                    + "a missing forward loses the reset (requested=2) or the hide line itself")
                .That(lines).ContainsExactly("Keyboard click: requested=1 played=1 failed=0 skipped=0 reached=0 silent=0 pending=0 late=0 maxlatems=0");
        }
        finally
        {
            // The click queued first-click diagnostics work: the collection-wide park scheduler
            // holds it — run it deterministically, then ASSERT the drain (review 03 S3).
            RunParkedWork();
            Check.That(AudioSessionDiagnostics.DrainQueuedWork()).IsTrue();
            window.Close();
        }
    }
}
