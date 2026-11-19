namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

/// <summary>
/// T3 — one content-free log line per switcher open and one per close, so the debate's
/// look-time ladder can be computed from the day log (an open with <c>lastOutcome=Picked</c>
/// and <c>sinceLastCloseMs &lt; 10000</c> is a quick reopen).
///
/// <para>
/// The fake clock drives every timing (<c>openMs</c>, <c>guardLiftMs</c>, <c>liftToEntryMs</c>,
/// <c>sinceLastCloseMs</c>) and the fake logger captures the rendered lines, so each assertion
/// is the exact string the recorder produces. The window list is fed with distinctive titles and
/// process names precisely so the privacy case can prove they never appear.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class TaskSwitcherSessionLogTests
{
    [Fact]
    public void OpenLine_WaitsForTheLayoutPass_AndCarriesTilesReachableAndTheFirstSessionMarkers()
    {
        using var h = new Harness(windowCount: 5);

        h.Sample(h.Away, 0);
        h.Open();

        // Before layout nobody knows how many tiles are reachable, so the open line must wait.
        Check.That(h.Entries).IsEmpty();

        h.Layout(reachable: 3);

        Check.That(h.Lines).ContainsExactly(
            "Task switcher opened: tiles=5 currentExcluded=false reachable=3 anchorValid=true sinceLastCloseMs=none lastOutcome=none tileDwellMs=800 previews=0 previewFailures=0 previewHr=none");
        Check.That(h.Entries[0].Level).IsEqualTo(LogLevel.Information);

        // A second layout pass (a re-registration) must not write the open line twice.
        h.Vm.UpdateCloseButtonPosition(h.Close);
        Check.That(h.CountLines("Task switcher opened:")).IsEqualTo(1);
    }

    [Fact]
    public void OpenLine_CarriesTheTileDwell_TheEffectiveMaxOfTileAndToolbar()
    {
        // S4-A: the line ends with the tile dwell in effect, captured at the open. With the
        // toolbar slower than the tile knob (900 > 800) the tiles follow the toolbar, so the
        // value logged is 900 — the default-settings 800 is pinned by every other open-line
        // case above, and the registered targets themselves by TaskSwitcherTileDwellTests.
        using var h = new Harness(windowCount: 2, settings: new EyeCtrlSettings
        {
            Dwell = { ToolbarButtonDwellMs = 900 }
        });

        h.Sample(h.Away, 0);
        h.Open();
        h.Layout(reachable: 2);

        Check.That(h.Lines).ContainsExactly(
            "Task switcher opened: tiles=2 currentExcluded=false reachable=2 anchorValid=true sinceLastCloseMs=none lastOutcome=none tileDwellMs=900 previews=0 previewFailures=0 previewHr=none");
    }

    [Fact]
    public void OpenLine_CarriesThePreviewCounts_AfterTheTileDwell()
    {
        // S4-C: the View reports the registration result before the close-disc call flushes the
        // open line, so the two counts describe the frames the owner is about to see.
        using var h = new Harness(windowCount: 4);

        h.Sample(h.Away, 0);
        h.Open();
        h.Vm.ReportPreviews(registered: 3, failed: 1);
        h.Layout(reachable: 4);

        Check.That(h.Lines).ContainsExactly(
            "Task switcher opened: tiles=4 currentExcluded=false reachable=4 anchorValid=true sinceLastCloseMs=none lastOutcome=none tileDwellMs=800 previews=3 previewFailures=1 previewHr=none");
    }

    [Fact]
    public void OpenLine_CarriesTheFirstFailingPreviewHresult_AsAHexToken()
    {
        // S4-C fixup-01: the installed UIAccess / High-integrity build is the untested case, so the
        // line must say WHY previews failed — a hex HRESULT, never a title or a process.
        using var h = new Harness(windowCount: 2);

        h.Sample(h.Away, 0);
        h.Open();
        h.Vm.ReportPreviews(registered: 0, failed: 2, firstFailureHresult: unchecked((int)0x80070005));
        h.Layout(columns: 2);

        Check.That(h.Lines).ContainsExactly(
            "Task switcher opened: tiles=2 currentExcluded=false reachable=2 anchorValid=true sinceLastCloseMs=none lastOutcome=none tileDwellMs=800 previews=0 previewFailures=2 previewHr=0x80070005");
    }

    [Fact]
    public void PreviewCounts_ArePerSession_AndNeverCarriedIntoTheNextOpen()
    {
        // The VM is a DI singleton: a count left over from a session whose previews all worked
        // would claim previews for a session that registered none.
        using var h = new Harness(windowCount: 2);

        h.Sample(h.Away, 0);
        h.Open();
        h.Vm.ReportPreviews(2, 0);
        h.Layout(columns: 2);
        h.Toggle();   // close this session

        h.At(20_000);
        h.Open();
        h.Layout(columns: 2);

        Check.That(h.Lines.Last())
            .Contains("tileDwellMs=800 previews=0 previewFailures=0 previewHr=none");
    }

    [Fact]
    public void OpenLine_CarriesARaisedTileKnob_AboveTheToolbar()
    {
        // S4-A fixup-01 (review finding 1): the knob's OWN value above its default — tile 1200,
        // toolbar 600 — is what the line must log (the 800 → 1000 ladder step works exactly
        // through this). Reviewer mutant (e), max(800, toolbar), ignores the setting and logs
        // 800 here — red. The targets themselves are pinned by TaskSwitcherTileDwellTests.
        using var h = new Harness(windowCount: 2, settings: new EyeCtrlSettings
        {
            Dwell = { ToolbarButtonDwellMs = 600, TaskSwitcherTileDwellMs = 1200 }
        });

        h.Sample(h.Away, 0);
        h.Open();
        h.Layout(reachable: 2);

        Check.That(h.Lines).ContainsExactly(
            "Task switcher opened: tiles=2 currentExcluded=false reachable=2 anchorValid=true sinceLastCloseMs=none lastOutcome=none tileDwellMs=1200 previews=0 previewFailures=0 previewHr=none");
    }

    [Fact]
    public void OpenLine_SaysWhetherTheForegroundWindowWasLeftOut()
    {
        // S2-A's field tells the day log whether the grid the owner saw was the full enumeration
        // or one window short. Both values are pinned: `true` for a foreground that was a tile,
        // `false` for the next session where nothing ordinary was in front (the fail-closed null).
        using var h = new Harness(windowCount: 3, foreground: new IntPtr(2));
        h.Sample(h.Away, 0);
        h.Open();
        h.Layout(columns: 3);

        Check.That(h.Lines[0]).IsEqualTo(
            "Task switcher opened: tiles=2 currentExcluded=true reachable=2 anchorValid=true sinceLastCloseMs=none lastOutcome=none tileDwellMs=800 previews=0 previewFailures=0 previewHr=none");

        h.At(500);
        h.Toggle();                              // close (Other) at 500 ms
        h.SetForeground(null);                   // next session: EyeCtrl itself / the desktop
        h.At(5500);
        h.Sample(h.Away, 5500);
        h.Open();
        h.Layout(columns: 3);

        Check.That(h.Lines.Last()).IsEqualTo(
            "Task switcher opened: tiles=3 currentExcluded=false reachable=3 anchorValid=true sinceLastCloseMs=5000 lastOutcome=Other tileDwellMs=800 previews=0 previewFailures=0 previewHr=none");
    }

    [Fact]
    public void PickedOutcome_LogsSlotRowColAndTheLiftToEntrySpan_FromTheRegisteredFaces()
    {
        using var h = new Harness(windowCount: 9);
        h.Sample(h.Away, 0);
        h.Open();
        h.Layout(columns: 3);

        // (A) lifts on the 200 ms sample; the gaze enters the fifth tile (slot 4) at 300 ms, so
        // the lift-to-entry span is exactly 100 ms.
        h.Sample(h.Away2, 200);
        var picked = 4;
        h.Sample(h.TileCentre(picked, columns: 3), 300);
        var closedAt = h.Hold(h.TileCentre(picked, columns: 3), 330, 3000);

        Check.That(closedAt).IsNotNull();
        Check.That(h.Selections).ContainsExactly(new IntPtr(picked + 1));
        Check.That(h.Lines).ContainsExactly(
            "Task switcher opened: tiles=9 currentExcluded=false reachable=9 anchorValid=true sinceLastCloseMs=none lastOutcome=none tileDwellMs=800 previews=0 previewFailures=0 previewHr=none",
            $"Task switcher closed: outcome=Picked openMs={h.SinceOpen(closedAt!.Value)} guardLiftMs=200 liftToEntryMs=100 slot=4 row=1 col=1");
    }

    [Fact]
    public void CloseDisc_IsGated_LikeEveryTarget_AndReportsTheLiftAndEntry()
    {
        // F-CORNER: the ✕ sits at (screen width − 53, 53) — the top-right corner a toolbar's Task
        // Switcher button may open from — so it is under the same (A)+(B) rule as the tiles. The
        // anchor is ON the ✕: a resting look must not dismiss, and the escape must still work
        // after the gaze moves (> 50 DIP) and enters the disc from outside.
        using var h = new Harness(windowCount: 2);
        h.Sample(h.Close, 0);
        h.Open();
        h.Layout(reachable: 2, closeCenter: h.Close);

        h.Hold(h.Close, 30, 500);
        Check.WithCustomMessage("the opening gaze on the ✕ dismissed the switcher without moving")
            .That(h.Vm.IsVisible).IsTrue();

        h.Sample(h.Away, 600);                 // 300 DIP from the anchor → (A) lifts at 600 ms
        var closedAt = h.Hold(h.Close, 630, 3000);   // fresh entry, then normal 600 ms dwell

        Check.That(closedAt).IsNotNull();
        Check.That(h.Selections).IsEmpty();
        Check.That(h.Lines).ContainsExactly(
            "Task switcher opened: tiles=2 currentExcluded=false reachable=2 anchorValid=true sinceLastCloseMs=none lastOutcome=none tileDwellMs=800 previews=0 previewFailures=0 previewHr=none",
            $"Task switcher closed: outcome=CloseDisc openMs={h.SinceOpen(closedAt!.Value)} guardLiftMs=600 liftToEntryMs=n/a slot=- row=- col=-");
    }

    [Fact]
    public void PausedTileLook_IsADismiss_NotAPick()
    {
        using var h = new Harness(windowCount: 4);
        h.Sample(h.Away, 0);
        h.Open();
        h.Layout(columns: 2);

        h.Sample(h.Away2, 200);
        h.Sample(h.TileCentre(0, columns: 2), 300, paused: true);
        var closedAt = h.Hold(h.TileCentre(0, columns: 2), 330, 3000, paused: true);

        Check.That(closedAt).IsNotNull();
        Check.That(h.Selections).IsEmpty();          // paused never switches a window
        Check.That(h.Vm.IsVisible).IsFalse();
        Check.That(h.Lines[1]).IsEqualTo(
            $"Task switcher closed: outcome=PausedDismiss openMs={h.SinceOpen(closedAt!.Value)} guardLiftMs=200 liftToEntryMs=n/a slot=0 row=0 col=0");
    }

    [Fact]
    public void SecondToggle_LogsOther_AndStillPairsTheOpenLineWrittenAtClose()
    {
        // No layout pass at all: the open line is flushed by the close, so the two lines stay
        // paired and in order even for a session that never drew a tile.
        using var h = new Harness(windowCount: 3);
        h.Sample(h.Away, 0);
        h.Open();
        h.At(500);
        h.Toggle();

        Check.That(h.Lines).ContainsExactly(
            "Task switcher opened: tiles=3 currentExcluded=false reachable=0 anchorValid=true sinceLastCloseMs=none lastOutcome=none tileDwellMs=800 previews=0 previewFailures=0 previewHr=none",
            "Task switcher closed: outcome=Other openMs=500 guardLiftMs=never liftToEntryMs=n/a slot=- row=- col=-");
    }

    [Fact]
    public void APickIsLoggedOnce_EvenWhenHideOverlayRunsAgain()
    {
        using var h = new Harness(windowCount: 3);
        h.Sample(h.Away, 0);
        h.Open();
        h.Layout(columns: 3);
        h.Sample(h.Away2, 200);
        h.Sample(h.TileCentre(1, columns: 3), 300);
        h.Hold(h.TileCentre(1, columns: 3), 330, 3000);

        Check.That(h.CountLines("Task switcher closed:")).IsEqualTo(1);

        // The VM's HideOverlay is public, so a second call is possible even though production
        // hides the VM once per pick (the dispatcher's post-pick dismissal goes to the window's
        // ITaskSwitcherOverlay.HideOverlay, a different object). The line must not double.
        h.Vm.HideOverlay();

        Check.That(h.CountLines("Task switcher closed:")).IsEqualTo(1);
        Check.That(h.CountLines("Task switcher opened:")).IsEqualTo(1);
    }

    [Fact]
    public void InvalidAnchor_ReportsAnchorValidFalse_AndNaForBothGuardTimings()
    {
        using var h = new Harness(windowCount: 4);
        h.Sample(h.Away, 0);
        h.Gap(h.Away, 10);              // the last sample before the show is a gap
        h.Open();
        h.Layout(columns: 2);

        // With an invalid anchor (A) is skipped, so the very first look at a tile is an entry.
        h.Sample(h.TileCentre(2, columns: 2), 100);
        var closedAt = h.Hold(h.TileCentre(2, columns: 2), 130, 3000);

        Check.That(closedAt).IsNotNull();
        Check.That(h.Lines[0]).IsEqualTo(
            "Task switcher opened: tiles=4 currentExcluded=false reachable=4 anchorValid=false sinceLastCloseMs=none lastOutcome=none tileDwellMs=800 previews=0 previewFailures=0 previewHr=none");
        Check.That(h.Lines[1]).IsEqualTo(
            $"Task switcher closed: outcome=Picked openMs={h.SinceOpen(closedAt!.Value)} guardLiftMs=n/a liftToEntryMs=n/a slot=2 row=1 col=0");
    }

    [Fact]
    public void TheNextOpen_CarriesThePreviousSessionsOutcomeAndTheTimeSinceItsClose()
    {
        using var h = new Harness(windowCount: 6);
        h.Sample(h.Away, 0);
        h.Open();
        h.Layout(columns: 3);
        h.Sample(h.Away2, 200);
        h.Sample(h.TileCentre(3, columns: 3), 300);
        var firstClose = h.Hold(h.TileCentre(3, columns: 3), 330, 3000);
        Check.That(firstClose).IsNotNull();

        var reopenedAt = firstClose!.Value + 8400;
        h.At(reopenedAt);
        h.Sample(h.Away, reopenedAt);
        h.Open();

        // The View's layout pass lands ~300 ms after the show (icon resolution + Show + the
        // Loaded-priority registration). The value is the previous close → the OPEN, so the
        // latency must not leak into it: 9800 ms between close and open is a quick reopen and
        // must stay one (T3 review finding 1 — a flush-time read logs 10100 here).
        h.At(reopenedAt + 300);
        h.Layout(columns: 3);

        Check.That(h.Lines.Any(line => string.Equals(
            line,
            "Task switcher opened: tiles=6 currentExcluded=false reachable=6 anchorValid=true sinceLastCloseMs=8400 lastOutcome=Picked tileDwellMs=800 previews=0 previewFailures=0 previewHr=none",
            StringComparison.Ordinal))).IsTrue();
    }

    [Fact]
    public void AnOpenLineFlushedAtTheClose_StillMeasuresSinceLastCloseFromTheOpen()
    {
        // The other flush path: a session that hides before its layout pass writes the open line
        // from EndSession. If the value were read at the flush it would include the whole session.
        using var h = new Harness(windowCount: 3);
        h.Sample(h.Away, 0);
        h.Open();
        h.Layout(columns: 3);
        h.Sample(h.Away2, 200);
        h.Sample(h.TileCentre(0, columns: 3), 300);
        var firstClose = h.Hold(h.TileCentre(0, columns: 3), 330, 3000);
        Check.That(firstClose).IsNotNull();

        var reopenedAt = firstClose!.Value + 8400;
        h.At(reopenedAt);
        h.Sample(h.Away, reopenedAt);
        h.Open();
        h.At(reopenedAt + 2500);      // the session lasts, and no layout ever runs
        h.Toggle();

        Check.That(h.Lines.Any(line => string.Equals(
            line,
            "Task switcher opened: tiles=3 currentExcluded=false reachable=0 anchorValid=true sinceLastCloseMs=8400 lastOutcome=Picked tileDwellMs=800 previews=0 previewFailures=0 previewHr=none",
            StringComparison.Ordinal))).IsTrue();
    }

    [Fact]
    public void NoLineEverCarriesATitleOrAProcessName()
    {
        // The windows are named so that any leak is a substring a reader can see. Every line of a
        // full pick session must be free of them (the ticket's privacy contract).
        using var h = new Harness(
            windows:
            [
                new AppWindow(new IntPtr(1), "Confidential Salary Review", 42, "secretproc"),
                new AppWindow(new IntPtr(2), "Steuererklaerung 2025", 43, "finanzamt"),
                new AppWindow(new IntPtr(3), "Notepad", 44, "notepad"),
            ]);
        h.Sample(h.Away, 0);
        h.Open();
        h.Layout(columns: 3);
        h.Sample(h.Away2, 200);
        h.Sample(h.TileCentre(0, columns: 3), 300);
        h.Hold(h.TileCentre(0, columns: 3), 330, 3000);

        Check.That(h.Lines).HasSize(2);
        foreach (var line in h.Lines)
        {
            foreach (var secret in new[] { "Confidential", "Salary", "Review", "Steuererklaerung", "finanzamt", "notepad", "Notepad" })
                Check.That(line.Contains(secret, StringComparison.Ordinal)).IsFalse();
        }
    }

    [Fact]
    public void AThrowingSink_NeverReachesTheGazePath()
    {
        using var h = new Harness(windowCount: 2, logger: new ThrowingLogger());

        Check.ThatCode(() =>
        {
            h.Sample(h.Away, 0);
            h.Open();
            h.Layout(columns: 2);
            h.Sample(h.Away2, 200);
            h.Sample(h.TileCentre(0, columns: 2), 300);
            h.Hold(h.TileCentre(0, columns: 2), 330, 3000);
        }).DoesNotThrow();

        // The pick still happened: logging is an observer, never a gate on the session.
        Check.That(h.Selections).ContainsExactly(new IntPtr(1));
        Check.That(h.Vm.IsVisible).IsFalse();
    }

    private sealed class Harness : IDisposable
    {
        private readonly FakeTimeProvider _clock = new();
        private readonly WeakReferenceMessenger _messenger = new();
        private readonly Capture _capture = new();
        private readonly FakeWindowManager _windowManager;
        private readonly long _startMs;
        /// <summary>The fake clock reading at the toggle that opened the current session.</summary>
        private long _openedAtMs;

        internal TaskSwitcherViewModel Vm { get; }
        internal List<IntPtr> Selections { get; } = new();
        internal List<(LogLevel Level, string Message)> Entries => _capture.Entries;
        internal IReadOnlyList<string> Lines => _capture.Entries.Select(e => e.Message).ToList();

        /// <summary>The gaze point at opening, and the second empty point 200 DIP to its right.</summary>
        internal PhysicalPoint Away { get; } = new(1000, 800);
        internal PhysicalPoint Away2 { get; } = new(1200, 800);
        internal PhysicalPoint Close { get; } = new(1000, 1100);

        internal Harness(
            int windowCount = 3,
            IReadOnlyList<AppWindow>? windows = null,
            ILogger<TaskSwitcherViewModel>? logger = null,
            IntPtr? foreground = null,
            EyeCtrlSettings? settings = null)
        {
            DpiHelper.Initialize(1.0, 1.0);
            _startMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
            _windowManager = new FakeWindowManager(
                windows ?? Enumerable.Range(1, windowCount)
                    .Select(i => new AppWindow(new IntPtr(i), $"Window {i}", (uint)(100 + i), "app")).ToArray(),
                foreground);
            Vm = new TaskSwitcherViewModel(
                _windowManager,
                settings ?? new EyeCtrlSettings(),
                _messenger,
                logger: logger ?? _capture,
                timeProvider: _clock);
            _messenger.Register<Harness, TaskSwitcherSelectionMessage>(this, (_, m) => Selections.Add(m.Hwnd));
        }

        /// <summary>What the next open sees in front (null = nothing eligible, e.g. EyeCtrl itself).</summary>
        internal void SetForeground(IntPtr? hwnd) => _windowManager.Foreground = hwnd;

        /// <summary>The fake clock, in whole milliseconds since the harness was created.</summary>
        internal long NowMs => _clock.GetUtcNow().ToUnixTimeMilliseconds() - _startMs;

        internal void At(long ms)
        {
            if (ms > NowMs)
                _clock.Advance(TimeSpan.FromMilliseconds(ms - NowMs));
        }

        internal void Open()
        {
            _openedAtMs = NowMs;
            _messenger.Send(new TaskSwitcherToggledMessage());
        }

        internal void Toggle() => _messenger.Send(new TaskSwitcherToggledMessage());

        /// <summary>Milliseconds from this session's open to <paramref name="atMs"/> on the clock.</summary>
        internal long SinceOpen(long atMs) => atMs - _openedAtMs;

        /// <summary>
        /// The View's post-layout pass: each reachable tile's drawn face in display order, then
        /// the close disc (which is what flushes the open line).
        /// </summary>
        internal void Layout(int reachable = -1, int columns = 3, PhysicalPoint? closeCenter = null)
        {
            var count = reachable < 0 ? Vm.Tiles.Count : Math.Min(reachable, Vm.Tiles.Count);
            for (var i = 0; i < count; i++)
                Vm.UpdateTilePosition(Vm.Tiles[i].Hwnd, Face(i, columns));

            Vm.UpdateCloseButtonPosition(closeCenter ?? Close);
        }

        internal PhysicalPoint TileCentre(int index, int columns) => Face(index, columns).Center;

        private static PhysicalRect Face(int index, int columns)
        {
            var x = index % columns * 200.0;
            var y = index / columns * 166.0;
            return new PhysicalRect(x, y, 184, 150);
        }

        internal void Sample(PhysicalPoint p, long atMs, bool paused = false)
        {
            At(atMs);
            _messenger.Send(new GazeCursorMovedMessage(p, atMs * 1000) { IsPaused = paused });
        }

        internal void Gap(PhysicalPoint p, long atMs)
        {
            At(atMs);
            _messenger.Send(new GazeCursorMovedMessage(new GazeFrame(p, atMs * 1000, IsInGap: true)));
        }

        /// <summary>Holds the gaze at 30 ms cadence; returns the time the overlay hid, if it did.</summary>
        internal long? Hold(PhysicalPoint p, long fromMs, long toMs, bool paused = false)
        {
            for (var t = fromMs; t <= toMs; t += 30)
            {
                Sample(p, t, paused);
                if (!Vm.IsVisible)
                    return t;
            }

            return null;
        }

        internal int CountLines(string prefix) =>
            Entries.Count(e => e.Message.StartsWith(prefix, StringComparison.Ordinal));

        public void Dispose()
        {
            Vm.Dispose();
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    private sealed class Capture : ILogger<TaskSwitcherViewModel>
    {
        internal List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class ThrowingLogger : ILogger<TaskSwitcherViewModel>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => throw new InvalidOperationException("sink is down");
    }

    private sealed class FakeWindowManager : IWindowManager
    {
        private readonly IReadOnlyList<AppWindow> _windows;

        internal FakeWindowManager(IReadOnlyList<AppWindow> windows, IntPtr? foreground = null)
        {
            _windows = windows;
            Foreground = foreground;
        }

        internal IntPtr? Foreground { get; set; }

        public IReadOnlyList<AppWindow> EnumerateOpenWindows() => _windows;
        public void BringToFront(IntPtr hwnd) { }
        public IntPtr? TryGetEligibleForegroundWindow() => Foreground;
        public void Minimize(IntPtr hwnd) { }
        public void MinimizeNonBlocking(IntPtr hwnd) { }
        public void MaximizeOrRestore(IntPtr hwnd) { }
        public bool CloseWindow(IntPtr hwnd) => false;
    }
}
