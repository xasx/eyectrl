namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Messages;
using NFluent;

/// <summary>
/// The T0(a) lag-mark host: the button is shown (and the shown line logged) whenever the dispatch
/// probe exists, a completed dwell logs the exact LAG MARK line, and the button yields to Settings
/// and to the live instrument switch. The presenter is a fake, so the host's when-and-what
/// decisions are exercised without a window.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class LagMarkHostTests
{
    private sealed class FakePresenter : ILagMarkPresenter
    {
        public int ShowCalls { get; private set; }
        public int HideCalls { get; private set; }
        public bool IsShowing { get; private set; }
        public List<(PhysicalPoint Gaze, long Timestamp)> Gaze { get; } = [];

        public event Action<long>? Marked;

        public event Action? Moved;

        public PhysicalRect HitBounds { get; set; } = new(3280, 1872, 512, 384);

        public Rect ShownRectDip { get; set; } = new(1640, 936, 256, 192);

        public void ShowButton()
        {
            ShowCalls++;
            IsShowing = true;
        }

        public void HideButton()
        {
            HideCalls++;
            IsShowing = false;
        }

        public void OnGaze(PhysicalPoint gaze, long timestampMicroseconds) => Gaze.Add((gaze, timestampMicroseconds));

        public void RaiseMarked(long timestampMicroseconds) => Marked?.Invoke(timestampMicroseconds);

        public void RaiseMoved() => Moved?.Invoke();
    }

    private sealed class Rig
    {
        public WeakReferenceMessenger Messenger { get; } = new();
        public FakePresenter Presenter { get; } = new();
        public DispatchLatencyProbe Probe { get; } = new();
        public bool InstrumentOn { get; set; } = true;
        public bool SettingsOpen { get; set; }
        public bool ProbeOpen { get; set; }
        public DateTime Now { get; set; } = new(2026, 10, 16, 10, 20, 30, DateTimeKind.Local);
        public List<string> Lines { get; } = [];
        public List<PhysicalRect?> Zones { get; } = [];
        public LagMarkHost Host { get; }

        public Rig()
        {
            Host = new LagMarkHost(
                Messenger, Presenter, Probe,
                isInstrumentOn: () => InstrumentOn,
                isSettingsOpen: () => SettingsOpen,
                isProbeOpen: () => ProbeOpen,
                now: () => Now,
                postToUi: work => work(),
                log: line => Lines.Add(line));
            Messenger.Register<LagMarkZoneMessage>(this, (_, message) => Zones.Add(message.Zone));
        }
    }

    /// <summary>One executed sample: enqueued latencyMs before it ran at <paramref name="atTicks"/>.</summary>
    private static void Sample(DispatchLatencyProbe probe, long atTicks, double latencyMs)
    {
        probe.Enqueue();
        probe.Execute(atTicks - (long)(latencyMs * Stopwatch.Frequency / 1000.0), atTicks);
    }

    private static void WithDpi(double scale, Action action)
    {
        var (x, y) = (DpiHelper.ScaleX, DpiHelper.ScaleY);
        try
        {
            DpiHelper.Update(scale, scale);
            action();
        }
        finally
        {
            DpiHelper.Update(x, y);
        }
    }

    [StaFact]
    public void Start_ShowsTheButton_LogsTheShownLine_AndPublishesTheZone()
    {
        var rig = new Rig();
        WithDpi(2.0, () => rig.Host.Start());

        Check.That(rig.Presenter.ShowCalls).IsEqualTo(1);
        Check.That(rig.Lines).ContainsExactly("Lag mark button shown at 1640,936 256×192 DIP (scale 2)");
        Check.That(rig.Zones).HasSize(1);
        Check.That(rig.Zones[0]!.Value).IsEqualTo(rig.Presenter.HitBounds);
    }

    [Fact]
    public void Start_WithTheInstrumentOff_ConstructsNothingOnScreen()
    {
        var rig = new Rig { InstrumentOn = false };

        rig.Host.Start();

        Check.That(rig.Presenter.ShowCalls).IsEqualTo(0);
        Check.That(rig.Lines).IsEmpty();
        Check.That(rig.Zones).IsEmpty();
    }

    [Fact]
    public void Hiding_TakesTheZoneWithIt()
    {
        var rig = new Rig();
        rig.Host.Start();

        rig.InstrumentOn = false;
        rig.Messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 200), 1_000));

        Check.That(rig.Presenter.HideCalls).IsEqualTo(1);
        Check.That(rig.Zones).HasSize(2);
        Check.That(rig.Zones[0]!.Value).IsEqualTo(rig.Presenter.HitBounds);
        Check.That(rig.Zones[1]).IsNull();
    }

    [Fact]
    public void TheInstrumentSwitchedOff_MidSession_StopsLoggingMarks()
    {
        var rig = new Rig();
        rig.Host.Start();
        rig.Lines.Clear();

        rig.Now = new DateTime(2026, 10, 16, 10, 20, 30, DateTimeKind.Local);
        rig.Presenter.RaiseMarked(1_000);
        Check.That(rig.Host.MarkCount).IsEqualTo(1);

        rig.InstrumentOn = false;
        rig.Presenter.RaiseMarked(2_000);

        Check.That(rig.Host.MarkCount).IsEqualTo(1);
        Check.That(rig.Lines).HasSize(1);   // only the mark logged before the switch-off
    }

    [Fact]
    public void Gaze_IsForwardedOnlyWhileShowing_AndNeverFromAHeldSample()
    {
        var rig = new Rig();
        var point = new PhysicalPoint(100, 200);

        rig.Messenger.Send(new GazeCursorMovedMessage(point, 1_000));
        Check.WithCustomMessage("gaze was fed to a button that is not showing")
            .That(rig.Presenter.Gaze).IsEmpty();

        rig.Host.Start();
        rig.Messenger.Send(new GazeCursorMovedMessage(point, 2_000));
        rig.Messenger.Send(new GazeCursorMovedMessage(new GazeFrame(point, 3_000, IsInGap: true)));

        Check.That(rig.Presenter.Gaze).HasSize(1);
        Check.That(rig.Presenter.Gaze[0].Timestamp).IsEqualTo(2_000);
    }

    [Fact]
    public void WhileSettingsIsOpen_NoGazeReachesTheButton()
    {
        var rig = new Rig();
        rig.Host.Start();
        rig.SettingsOpen = true;

        rig.Messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 200), 2_000));

        Check.WithCustomMessage("gaze reached the button under an open Settings window")
            .That(rig.Presenter.Gaze).IsEmpty();
    }

    // ────── Task 6: the sub-pixel probe is the second screen-owning surface ──────

    [Fact]
    public void Start_WithTheProbeOpen_ShowsNothing()
    {
        var rig = new Rig { ProbeOpen = true };

        rig.Host.Start();

        Check.That(rig.Presenter.ShowCalls).IsEqualTo(0);
        Check.That(rig.Lines).IsEmpty();
        Check.That(rig.Zones).IsEmpty();
    }

    /// <summary>
    /// Unlike the restart card, the button is a session fixture: closing the probe brings it back —
    /// but only once Settings is closed too (both surfaces own the screen while up).
    /// </summary>
    [Fact]
    public void OpeningTheProbe_HidesTheButton_AndClosingBringsItBack_WhenBothAreClosed()
    {
        var rig = new Rig();
        rig.Host.Start();

        rig.ProbeOpen = true;
        rig.Host.OnProbeOpenChanged(true);
        Check.That(rig.Presenter.IsShowing).IsFalse();
        Check.That(rig.Zones.Last()).IsNull();

        // The probe closes while Settings is open: still nothing.
        rig.SettingsOpen = true;
        rig.ProbeOpen = false;
        rig.Host.OnProbeOpenChanged(false);
        Check.That(rig.Presenter.IsShowing).IsFalse();

        // Both closed now: the button returns.
        rig.SettingsOpen = false;
        rig.Host.OnSettingsOpenChanged(false);
        Check.That(rig.Presenter.IsShowing).IsTrue();
        Check.That(rig.Presenter.ShowCalls).IsEqualTo(2);
        Check.That(rig.Zones.Last()!.Value).IsEqualTo(rig.Presenter.HitBounds);
    }

    [Fact]
    public void WhileTheProbeIsOpen_NoGazeReachesTheButton()
    {
        var rig = new Rig();
        rig.Host.Start();
        rig.ProbeOpen = true;

        rig.Messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 200), 2_000));

        Check.WithCustomMessage("gaze reached the button under the probe")
            .That(rig.Presenter.Gaze).IsEmpty();
    }

    /// <summary>F1 at the fake level: a move re-sends the zone, and only while the button is up.</summary>
    [Fact]
    public void AMove_ReSendsTheZone_OnlyWhileShowing()
    {
        var rig = new Rig();
        rig.Host.Start();

        var moved = new PhysicalRect(3280, 1032, 512, 384);
        rig.Presenter.HitBounds = moved;
        rig.Presenter.RaiseMoved();
        Check.That(rig.Zones).HasSize(2);
        Check.That(rig.Zones[1]!.Value).IsEqualTo(moved);

        // Hidden (Settings opened): a move sends nothing.
        rig.SettingsOpen = true;
        rig.Host.OnSettingsOpenChanged(true);
        Check.That(rig.Zones).HasSize(3);
        Check.That(rig.Zones[2]).IsNull();

        rig.Presenter.RaiseMoved();
        Check.That(rig.Zones).HasSize(3);
    }

    /// <summary>
    /// Review 01 F1: the offer zone must follow the button. The button re-places itself when the
    /// chrome it keeps clear of changes; without the host re-sending, the zone the offer engine
    /// avoids goes stale at the old place (measured at y 1872 px while the button sat at 1032), so a
    /// fixation on the button's new place could raise an offer — and a taken offer clicks whatever
    /// lies under the click-through button. The real window and the real host, through the present
    /// seam; the chrome is the button's own resting rect, so the move is host-independent.
    /// </summary>
    [StaFact]
    public void TheOfferZone_FollowsTheButton_WhenChromeMovesIt()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var messenger = new WeakReferenceMessenger();
        var zones = new List<PhysicalRect?>();
        messenger.Register<LagMarkZoneMessage>(this, (_, message) => zones.Add(message.Zone));

        IReadOnlyList<Rect> chrome = [];
        var window = new LagMarkWindow(
            dwellMs: 25, messenger, () => chrome,
            present: w =>
            {
                w.Measure(new Size(w.Width, w.Height));
                w.Arrange(new Rect(0, 0, w.Width, w.Height));
                w.UpdateLayout();
            });
        using var host = new LagMarkHost(
            messenger, window, new DispatchLatencyProbe(),
            isInstrumentOn: () => true,
            isSettingsOpen: () => false,
            isProbeOpen: () => false,
            now: () => DateTime.Now,
            postToUi: work => work(),
            log: _ => { });
        try
        {
            host.Start();
            Check.That(zones).HasSize(1);
            var atRest = window.HitBounds;
            Check.That(zones[0]!.Value).IsEqualTo(atRest);

            // The chrome appears over the button's resting place; the next gaze sample re-places
            // the button, and the zone must follow it.
            chrome = [new Rect(window.Left, window.Top, window.Width, window.Height)];
            messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(10, 10), 1_000));

            Check.WithCustomMessage("the button did not move off the chrome that appeared over it")
                .That(window.HitBounds).IsNotEqualTo(atRest);
            Check.That(zones).HasSize(2);
            Check.WithCustomMessage("the zone stayed at the button's OLD place")
                .That(zones[1]!.Value).IsEqualTo(window.HitBounds);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void OpeningSettings_HidesTheButton_AndClosingBringsItBack()
    {
        var rig = new Rig();
        rig.Host.Start();

        rig.SettingsOpen = true;
        rig.Host.OnSettingsOpenChanged(true);
        Check.That(rig.Presenter.IsShowing).IsFalse();
        Check.That(rig.Zones.Last()).IsNull();

        rig.SettingsOpen = false;
        rig.Host.OnSettingsOpenChanged(false);
        Check.That(rig.Presenter.IsShowing).IsTrue();
        Check.That(rig.Presenter.ShowCalls).IsEqualTo(2);
        Check.That(rig.Zones.Last()!.Value).IsEqualTo(rig.Presenter.HitBounds);

        // With the instrument off, closing Settings brings nothing back.
        rig.InstrumentOn = false;
        rig.SettingsOpen = true;
        rig.Host.OnSettingsOpenChanged(true);
        rig.SettingsOpen = false;
        rig.Host.OnSettingsOpenChanged(false);
        Check.That(rig.Presenter.ShowCalls).IsEqualTo(2);
    }

    /// <summary>
    /// The exact line, and the mark's time is the completing SAMPLE's moment: its dispatch wait is
    /// the most recent execute's (LastLatencyMs), so the wall clock is now − 200 ms, not the write
    /// moment. The window ends at the execute instant and spans 15 s + the completing sample's
    /// wait (review 01, lens 2), so the completing sample's own 200 ms wait IS included: four
    /// seeded executes, deepest queue reading 1, a 1000 ms gap.
    /// </summary>
    [Fact]
    public void TheMarkLine_IsExact_AndTimesTheMarkAtTheCompletingSample()
    {
        var rig = new Rig();
        rig.Host.Start();
        rig.Lines.Clear();

        long ms = Stopwatch.Frequency / 1000;
        long t = Stopwatch.GetTimestamp();
        Sample(rig.Probe, t - 3_000 * ms, 5);
        Sample(rig.Probe, t - 2_000 * ms, 12.5);
        Sample(rig.Probe, t - 1_000 * ms, 100);
        // The completing sample: enqueued, then executed 200 ms later — the lag being marked; it
        // sits inside the window because the window reaches the execute instant.
        Sample(rig.Probe, Stopwatch.GetTimestamp(), 200);
        rig.Now = new DateTime(2026, 10, 16, 10, 20, 30, DateTimeKind.Local);

        rig.Presenter.RaiseMarked(4_000);

        Check.That(rig.Lines).HasSize(1);
        Check.WithCustomMessage($"the mark line was: {rig.Lines[0]}")
            .That(Regex.IsMatch(
                rig.Lines[0],
                @"^LAG MARK #1 at 2026-10-16 10:20:29\.800 — last 15 s: n=4 max=200ms p95=200ms maxQueueDepth=1 gapMs=1000(?:\.\d+)? coverageMs=\d+$"))
            .IsTrue();
        Check.That(rig.Host.MarkCount).IsEqualTo(1);
    }

    /// <summary>
    /// Review 01, lens 2: the case the enqueue-ended window was blind to. The owner sees the ring
    /// stop, looks at the button 200 ms into a 1 s freeze, and the dwell completes in the drained
    /// backlog — the completing sample was enqueued inside the freeze, so an enqueue-ended window
    /// cut the freeze's own long waits off (measured: max 2.0 ms). The execute-ended window covers
    /// (at − 15 s, now], so the freeze's wait is the mark's max. Synthetic ring: 10 s of ~90 Hz
    /// normal use (2 ms waits), then the freeze sample, which waited 1000 ms and executed as the
    /// mark completed.
    /// </summary>
    [Fact]
    public void AFreezeTheOwnerDwellsThrough_IsVisibleToTheMarksWindow()
    {
        var rig = new Rig();
        rig.Host.Start();
        rig.Lines.Clear();

        long ms = Stopwatch.Frequency / 1000;
        long t = Stopwatch.GetTimestamp();
        for (var k = 0; k < 900; k++)
            Sample(rig.Probe, t - 10_000 * ms + k * 11 * ms, 2);
        // The freeze: enqueued 1000 ms before it ran; this execute completes the dwell.
        Sample(rig.Probe, t, 1000);
        rig.Now = new DateTime(2026, 10, 16, 10, 20, 30, DateTimeKind.Local);

        rig.Presenter.RaiseMarked(0);

        Check.That(rig.Lines).HasSize(1);
        Check.WithCustomMessage($"the mark line was: {rig.Lines[0]}")
            .That(Regex.IsMatch(
                rig.Lines[0],
                @"^LAG MARK #1 at 2026-10-16 10:20:29\.000 — last 15 s: n=901 max=1000ms p95=2ms maxQueueDepth=1 gapMs=111 coverageMs=\d+$"))
            .IsTrue();
    }

    [Fact]
    public void Marks_CountFromOne_PerSession()
    {
        var rig = new Rig();
        rig.Host.Start();
        rig.Lines.Clear();

        rig.Presenter.RaiseMarked(1_000);
        rig.Presenter.RaiseMarked(2_000);

        Check.That(rig.Host.MarkCount).IsEqualTo(2);
        Check.That(rig.Lines[0].StartsWith("LAG MARK #1 ", StringComparison.Ordinal)).IsTrue();
        Check.That(rig.Lines[1].StartsWith("LAG MARK #2 ", StringComparison.Ordinal)).IsTrue();
    }

    [Fact]
    public void Dispose_UnregistersTheHost_AndTakesTheButtonAndZoneDown()
    {
        var rig = new Rig();
        rig.Host.Start();

        rig.Host.Dispose();

        Check.That(rig.Presenter.HideCalls).IsEqualTo(1);
        Check.That(rig.Zones.Last()).IsNull();
        rig.Messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 200), 2_000));
        Check.That(rig.Presenter.Gaze).IsEmpty();
    }

    /// <summary>
    /// App.xaml.cs is integration wiring that cannot be composed headlessly; pin the construction's
    /// conditions. It must exist ONLY when the dispatch probe exists (an ordinary session must
    /// construct nothing), read the live instrument switch and the Settings seam, take the chrome
    /// zones, and carry NO pause condition — the T0(a) launcher starts the app <c>--paused</c> and
    /// requires the shown line within 60 s, so the button must be shown and logged then.
    /// </summary>
    [Fact]
    public void TheHost_IsWiredOnlyWhenTheProbeExists_WithNoPauseCondition()
    {
        var text = LocalizationTestData.SourceFiles()
            .Single(file => file.Relative == "src/EyeCtrl.App/App.xaml.cs").Text;

        var construction = Regex.Match(text, @"new\s+LagMarkHost\s*\(([^;]*)\)\s*;");
        Check.WithCustomMessage("App.xaml.cs no longer constructs the LagMarkHost.")
            .That(construction.Success).IsTrue();
        var arguments = construction.Groups[1].Value;
        Check.That(Regex.IsMatch(
            arguments, @"new\s+LagMarkWindow\s*\(")).IsTrue();
        // A6 (the orchestrator's pinned check): the window gets the spec's dwell — the toolbar
        // button's 700 ms default — not a literal or any other setting.
        Check.WithCustomMessage("the window is no longer built with settings.Dwell.ToolbarButtonDwellMs")
            .That(Regex.IsMatch(
                arguments,
                @"new\s+LagMarkWindow\s*\(\s*settings\s*\.\s*Dwell\s*\.\s*ToolbarButtonDwellMs")).IsTrue();
        Check.That(Regex.IsMatch(
            arguments,
            @"isInstrumentOn\s*:\s*\(\)\s*=>\s*settings\s*\.\s*Diagnostics\s*\.\s*MeasureUiInstruments")).IsTrue();
        Check.That(Regex.IsMatch(
            arguments,
            @"isSettingsOpen\s*:\s*\(\)\s*=>\s*_eventCoordinator\s*\.\s*IsSettingsOpen")).IsTrue();
        Check.That(Regex.IsMatch(
            arguments,
            @"isProbeOpen\s*:\s*\(\)\s*=>\s*_eventCoordinator\s*\.\s*IsProbeOpen")).IsTrue();
        Check.WithCustomMessage("the button is no longer given the chrome zones (incl. the edge bars), so it could cover the keys or the toolbar")
            .That(Regex.IsMatch(
                arguments,
                @"keepClear\s*:\s*\(\)\s*=>\s*LagMarkChrome\s*\.\s*CollectFromProduction\s*\(\s*coordinationContext\s*\)")).IsTrue();
        Check.WithCustomMessage("the host construction reads the paused state; the launcher starts the app --paused")
            .That(Regex.IsMatch(arguments, @"paus", RegexOptions.IgnoreCase)).IsFalse();

        Check.WithCustomMessage("the construction is no longer gated on the dispatch probe existing")
            .That(Regex.IsMatch(
                text,
                @"if\s*\(\s*_serviceProvider\s*\.\s*GetService<\s*DispatchLatencyProbe\s*>\s*\(\s*\)\s+is\s*\{[^}]*\}\s*\w*\s*\)")).IsTrue();

        // Review 01 F3: the Start() statement must be UNCONDITIONAL. Review mutant M5
        // (`if (!IsPausedLaunch) _lagMarkHost.Start();`) survived the earlier pin: a --paused
        // launch would then never show the button or write the shown line the launcher requires.
        var startStatement = Regex.Match(text, @"(?m)^\s*(?<statement>_lagMarkHost\s*\.\s*Start\s*\(\s*\)\s*;)\s*$");
        Check.WithCustomMessage("App.xaml.cs no longer calls _lagMarkHost.Start(); as a plain statement")
            .That(startStatement.Success).IsTrue();
        var above = text[..startStatement.Index].Replace("\r\n", "\n").TrimEnd('\n', ' ', '\t');
        var lastLine = above[(above.LastIndexOf('\n') + 1)..].Trim();
        Check.WithCustomMessage(
                $"the Start() statement is guarded by '{lastLine}': a --paused launch must still show the button")
            .That(lastLine.StartsWith("if", StringComparison.Ordinal)
                  || lastLine.StartsWith("else", StringComparison.Ordinal)
                  || lastLine.EndsWith("{", StringComparison.Ordinal)).IsFalse();

        Check.WithCustomMessage("App.xaml.cs no longer takes the button down when Settings opens")
            .That(Regex.IsMatch(
                text,
                @"_eventCoordinator\s*\.\s*SettingsOpenChanged\s*\+=\s*_lagMarkHost\s*\.\s*OnSettingsOpenChanged")).IsTrue();
        // The probe opening over the button takes it down too (Task 6).
        Check.WithCustomMessage("App.xaml.cs no longer takes the button down when the probe opens")
            .That(Regex.IsMatch(
                text,
                @"_eventCoordinator\s*\.\s*ProbeOpenChanged\s*\+=\s*_lagMarkHost\s*\.\s*OnProbeOpenChanged")).IsTrue();
        // A7 (the orchestrator's pinned check): OnExit disposes the host — it owns the window and
        // sends the final null zone.
        Check.WithCustomMessage("OnExit no longer disposes the lag-mark host")
            .That(Regex.IsMatch(
                text,
                @"Step\s*\(\s*""dispose lag mark host""\s*,\s*\(\)\s*=>\s*_lagMarkHost\s*\?\s*\.\s*Dispose\s*\(\s*\)\s*\)")).IsTrue();
    }
}
