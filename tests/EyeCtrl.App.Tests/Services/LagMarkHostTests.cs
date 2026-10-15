namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
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
    }

    private sealed class Rig
    {
        public WeakReferenceMessenger Messenger { get; } = new();
        public FakePresenter Presenter { get; } = new();
        public DispatchLatencyProbe Probe { get; } = new();
        public bool InstrumentOn { get; set; } = true;
        public bool SettingsOpen { get; set; }
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
    /// moment. The 15 s window is quoted from the probe: three seeded executes, with the deepest
    /// queue reading of 1 and a 1000 ms gap.
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
        // The completing sample: enqueued, then executed 200 ms later — the lag being marked.
        Sample(rig.Probe, Stopwatch.GetTimestamp(), 200);
        rig.Now = new DateTime(2026, 10, 16, 10, 20, 30, DateTimeKind.Local);

        rig.Presenter.RaiseMarked(4_000);

        Check.That(rig.Lines).HasSize(1);
        Check.WithCustomMessage($"the mark line was: {rig.Lines[0]}")
            .That(Regex.IsMatch(
                rig.Lines[0],
                @"^LAG MARK #1 at 2026-10-16 10:20:29\.800 — last 15 s: n=3 max=100ms p95=100ms maxQueueDepth=1 gapMs=1000 coverageMs=\d+$"))
            .IsTrue();
        Check.That(rig.Host.MarkCount).IsEqualTo(1);
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
        Check.That(Regex.IsMatch(
            arguments,
            @"isInstrumentOn\s*:\s*\(\)\s*=>\s*settings\s*\.\s*Diagnostics\s*\.\s*MeasureUiInstruments")).IsTrue();
        Check.That(Regex.IsMatch(
            arguments,
            @"isSettingsOpen\s*:\s*\(\)\s*=>\s*_eventCoordinator\s*\.\s*IsSettingsOpen")).IsTrue();
        Check.WithCustomMessage("the button is no longer given the chrome zones, so it could cover the keys or the toolbar")
            .That(Regex.IsMatch(
                arguments,
                @"keepClear\s*:\s*\(\)\s*=>\s*RestartNoticePlacement\s*\.\s*ToDip\s*\(\s*(?:EyeCtrl\s*\.\s*App\s*\.\s*Services\s*\.\s*Coordinators\s*\.\s*)?ActivationChromeZones\s*\.\s*CollectFromProduction\s*\(\s*coordinationContext\s*\)\s*\)")).IsTrue();
        Check.WithCustomMessage("the host construction reads the paused state; the launcher starts the app --paused")
            .That(Regex.IsMatch(arguments, @"paus", RegexOptions.IgnoreCase)).IsFalse();

        Check.WithCustomMessage("the construction is no longer gated on the dispatch probe existing")
            .That(Regex.IsMatch(
                text,
                @"if\s*\(\s*_serviceProvider\s*\.\s*GetService<\s*DispatchLatencyProbe\s*>\s*\(\s*\)\s+is\s*\{[^}]*\}\s*\w*\s*\)")).IsTrue();
        Check.WithCustomMessage("App.xaml.cs no longer starts the lag-mark host")
            .That(Regex.IsMatch(text, @"_lagMarkHost\s*\.\s*Start\s*\(\s*\)")).IsTrue();
        Check.WithCustomMessage("App.xaml.cs no longer takes the button down when Settings opens")
            .That(Regex.IsMatch(
                text,
                @"_eventCoordinator\s*\.\s*SettingsOpenChanged\s*\+=\s*_lagMarkHost\s*\.\s*OnSettingsOpenChanged")).IsTrue();
    }
}
