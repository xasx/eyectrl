namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging;

/// <summary>
/// S3-A — the Task Switcher's "Desktop anzeigen" / "Show desktop" tile.
///
/// <para>
/// The tile is the session's LAST one, drawn by <c>PanelItems</c> (window tiles + desktop), while
/// <see cref="TaskSwitcherViewModel.Tiles"/> keeps its S2-A/S2-B meaning: enumerated windows only.
/// That split is what the counts, the badges and the overflow chip are built on, so this file
/// asserts both sides: the desktop tile is present exactly when the session has something to
/// minimise, and it never inflates a window count.
/// </para>
///
/// <para>
/// Nothing here touches a real window: every path goes through the fake
/// <see cref="IWindowManager"/>, and the action itself is observed as the
/// <see cref="TaskSwitcherShowDesktopMessage"/> the VM sends (the App's dispatcher is what calls
/// <c>Minimize</c>, and its own test asserts that call — never this one).
/// </para>
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class TaskSwitcherDesktopTileTests
{
    private static readonly PhysicalPoint CloseCenter = new(1000, 1100);
    private static readonly PhysicalPoint Away = new(1700, 900);

    [Fact]
    public void DesktopTile_ComesAfterTheWindowTiles_AndNeverEntersTheWindowList()
    {
        using var h = new Harness(Windows(1, 2, 3));

        h.Open();

        Check.That(h.Vm.Tiles.Select(t => t.Hwnd))
            .ContainsExactly(new IntPtr(1), new IntPtr(2), new IntPtr(3));
        Check.That(h.Vm.HasDesktopTile).IsTrue();
        Check.That(h.Vm.PanelItems).HasSize(4);
        Check.That(h.Vm.PanelItems[h.Vm.PanelItems.Count - 1].IsDesktop).IsTrue();
        Check.That(h.Vm.PanelItems[h.Vm.PanelItems.Count - 1].Hwnd).IsEqualTo(IntPtr.Zero);
        Check.That(h.Vm.PanelItems.Take(3).Select(t => t.Hwnd))
            .ContainsExactly(new IntPtr(1), new IntPtr(2), new IntPtr(3));
        Check.That(h.Vm.IsEmpty).IsFalse();

        // The VM is a DI singleton: the next session must not inherit the tile.
        h.Toggle();
        Check.That(h.Vm.IsVisible).IsFalse();
        Check.That(h.Vm.HasDesktopTile).IsFalse();
        Check.That(h.Vm.PanelItems).IsEmpty();
    }

    [Fact]
    public void NoWindowAtAll_DrawsNoDesktopTile_AndSaysTheGridIsEmpty()
    {
        // The one session where "Show desktop" has nothing to do: S2-B's message stands alone.
        using var h = new Harness(Array.Empty<AppWindow>(), foreground: null);

        h.Open();

        Check.That(h.Vm.Tiles).IsEmpty();
        Check.That(h.Vm.HasDesktopTile).IsFalse();
        Check.That(h.Vm.PanelItems).IsEmpty();
        Check.That(h.Vm.IsEmpty).IsTrue();
    }

    [Fact]
    public void OnlyTheLeftOutCurrentWindow_StillDrawsTheDesktopTile()
    {
        // S2-A leaves the foreground window out of the tiles. That window is exactly what "show
        // desktop" can still minimise, so the tile is offered even though the grid has no window.
        using var h = new Harness(Windows(7), foreground: new IntPtr(7));

        h.Open();

        Check.That(h.Vm.Tiles).IsEmpty();
        Check.That(h.Vm.HasDesktopTile).IsTrue();
        Check.That(h.Vm.PanelItems).HasSize(1);
        Check.That(h.Vm.PanelItems[0].IsDesktop).IsTrue();
        Check.That(h.Vm.IsEmpty).IsFalse();
    }

    [Fact]
    public void DesktopTile_HoldsTheLastVisibleSlot_AndTheChipCountsTheWindowItDisplaced()
    {
        // 64 windows and a 63-cell grid (the owner's 9x7 at 1920x1200 with the top band). The
        // desktop tile takes the last slot, so 62 windows keep a tile and TWO go without: the one
        // that was already overflowing plus the one the tile displaced. The chip must say 2, and
        // the desktop tile must be the LAST panel entry — never the entry the viewport clips.
        using var h = new Harness(NumberedWindows(64));

        h.Open();
        h.Vm.ApplyTileCapacity(63);
        h.LayoutWindows(h.Vm.Tiles.Count);
        h.LayoutDesktop();

        Check.That(h.Vm.Tiles).HasSize(62);
        Check.That(h.Vm.PanelItems).HasSize(63);
        Check.That(h.Vm.PanelItems[h.Vm.PanelItems.Count - 1].IsDesktop).IsTrue();
        Check.That(h.Vm.OverflowCount).IsEqualTo(2);

        // Exactly fit: 62 windows + the desktop tile = 63 cells, nothing is hidden.
        using var fits = new Harness(NumberedWindows(62));
        fits.Open();
        fits.Vm.ApplyTileCapacity(63);
        fits.LayoutWindows(fits.Vm.Tiles.Count);
        fits.LayoutDesktop();
        Check.That(fits.Vm.Tiles).HasSize(62);
        Check.That(fits.Vm.OverflowCount).IsEqualTo(0);

        // A single-cell grid still keeps the desktop tile (and drops every window): the tile that
        // performs the action must never be the one cut off.
        using var tiny = new Harness(NumberedWindows(3));
        tiny.Open();
        tiny.Vm.ApplyTileCapacity(1);
        Check.That(tiny.Vm.Tiles).IsEmpty();
        Check.That(tiny.Vm.PanelItems).HasSize(1);
        Check.That(tiny.Vm.PanelItems[0].IsDesktop).IsTrue();
    }

    [Fact]
    public void WithoutADesktopTile_CapacityTrimmingIsNotApplied()
    {
        // The pre-S3-A layout (no window at all) keeps its viewport-clipping behaviour; a stale
        // trim from a previous session must not be able to rewrite this one either.
        using var h = new Harness(Array.Empty<AppWindow>());
        h.Open();

        h.Vm.ApplyTileCapacity(1);

        Check.That(h.Vm.PanelItems).IsEmpty();
        Check.That(h.Vm.OverflowCount).IsEqualTo(0);
    }

    [Fact]
    public void DwellingTheDesktopTile_MinimisesEveryEnumeratedWindow_IncludingTheLeftOutCurrent()
    {
        // The enumeration is z-order top-down: window 1 is in front, so it has no tile. "Show
        // desktop" means ALL of them — the left-out window is the one a naive implementation
        // (minimise the tiles) would leave on screen.
        using var h = new Harness(Windows(1, 2, 3), foreground: new IntPtr(1));

        h.Open();
        h.LayoutWindows(h.Vm.Tiles.Count);
        h.LayoutDesktop();

        h.Dwell(h.DesktopFace());

        Check.That(h.MinimiseRequests).HasSize(1);
        Check.That(h.MinimiseRequests[0]).ContainsExactly(
            new IntPtr(1), new IntPtr(2), new IntPtr(3));
        Check.That(h.Selections).IsEmpty();
        Check.That(h.Vm.IsVisible).IsFalse();
    }

    [Fact]
    public void DwellingTheDesktopTile_NeverMinimisesAForegroundTheEnumerationDidNotReturn()
    {
        // The action only touches windows the owner was shown or knowingly left out. A foreground
        // the enumeration filtered out (a title-less window, an owned dialog such as "Save as", an
        // odd shell) is not in the session's list and must not be minimised — the left-out current
        // window qualifies only when the enumeration actually returned it.
        using var h = new Harness(Windows(2, 3), foreground: new IntPtr(99));

        h.Open();
        h.LayoutWindows(h.Vm.Tiles.Count);
        h.LayoutDesktop();
        h.Dwell(h.DesktopFace());

        Check.That(h.MinimiseRequests).HasSize(1);
        Check.That(h.MinimiseRequests[0]).ContainsExactly(new IntPtr(2), new IntPtr(3));
        Check.That(h.MinimiseRequests[0].Contains(new IntPtr(99))).IsFalse();
    }

    [Fact]
    public void DwellingTheDesktopTile_NeverMinimisesAnEyeCtrlOwnedWindow()
    {
        // Defensive: the enumeration already excludes the overlays (WS_EX_TOOLWINDOW), but a
        // window of this process must be impossible to minimise — losing an EyeCtrl surface would
        // take away the owner's only control. The window still gets its tile (that is the
        // enumeration's call), it just never enters the action's set.
        var ownProcess = new AppWindow(new IntPtr(9), "EyeCtrl overlay", (uint)Environment.ProcessId, "eyectrl");
        using var h = new Harness([ownProcess, .. Windows(2, 3)], foreground: null);

        h.Open();
        h.LayoutWindows(h.Vm.Tiles.Count);
        h.LayoutDesktop();
        h.Dwell(h.DesktopFace());

        Check.That(h.MinimiseRequests).HasSize(1);
        Check.That(h.MinimiseRequests[0]).ContainsExactly(new IntPtr(2), new IntPtr(3));
        Check.That(h.MinimiseRequests[0].Contains(new IntPtr(9))).IsFalse();
    }

    [Fact]
    public void PausedDesktopLook_OnlyDismisses_NeverMinimises()
    {
        // Paused means no OS state change leaves EyeCtrl. The tile stays dwellable (the switcher
        // must remain escapable) but the action is suppressed.
        using var h = new Harness(Windows(1, 2));
        h.Open();
        h.LayoutWindows(h.Vm.Tiles.Count);
        h.LayoutDesktop();

        h.Sample(Away, 0);
        h.Hold(Away, 30_000, 300_000);                  // the landing guard's entry gate lifts
        h.Hold(h.DesktopFace().Center, 330_000, 3_000_000, paused: true);

        Check.That(h.MinimiseRequests).IsEmpty();
        Check.That(h.Selections).IsEmpty();
        Check.That(h.Vm.IsVisible).IsFalse();
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void DesktopTile_DoesNotFireFromTheOpeningLook(double scale)
    {
        // Slice 1's landing guard applies to every tile, the desktop one included: opening the
        // switcher from the toolbar leaves the gaze resting where the toolbar was, and this tile
        // sits at the end of the grid — a gaze already there must not minimise the desktop.
        using var h = new Harness(Windows(1, 2), scale: scale);
        // Two window tiles, so the desktop tile's cell is the third: sample THAT point before the
        // show — where the gaze would be if it rested on the tile the switcher is about to draw.
        h.Sample(h.FaceAt(2).Center, 0);
        h.Open();
        h.LayoutWindows(h.Vm.Tiles.Count);
        h.LayoutDesktop();

        h.Hold(h.DesktopFace().Center, 30_000, 3_000_000);

        Check.That(h.MinimiseRequests).IsEmpty();
        Check.That(h.Vm.IsVisible).IsTrue();

        // A fresh entry still works: leave past the release distance, then look at it.
        h.Hold(h.P(1700, 900), 3_030_000, 3_300_000);
        h.Hold(h.DesktopFace().Center, 3_330_000, 4_500_000);
        Check.That(h.MinimiseRequests).HasSize(1);
    }

    [Fact]
    public void PausedDesktopLook_LogsItsSlotRowAndColumn_LikeAPausedWindowTile()
    {
        // Fixup-01 F2: a paused window-tile look logs its slot; the paused desktop look must too,
        // or the log cannot tell "paused look at the desktop tile" from a no-slot dismissal.
        var capture = new Capture();
        using var h = new Harness(Windows(1, 2), logger: capture);
        h.Sample(Away, 0);
        h.Open();
        h.LayoutWindows(h.Vm.Tiles.Count);
        h.LayoutDesktop();
        h.Hold(Away, 30_000, 300_000);
        h.Hold(h.DesktopFace().Center, 330_000, 3_000_000, paused: true);

        Check.That(h.MinimiseRequests).IsEmpty();
        Check.That(h.Vm.IsVisible).IsFalse();

        var lines = capture.Lines;
        Check.That(lines).HasSize(2);
        Check.That(lines[1].StartsWith(
            "Task switcher closed: outcome=PausedDismiss ", StringComparison.Ordinal)).IsTrue();
        Check.That(lines[1].EndsWith(" slot=2 row=0 col=2", StringComparison.Ordinal)).IsTrue();
    }

    [Fact]
    public void DesktopOutcome_IsLoggedWithItsSlotRowAndColumn()
    {
        // T3: one new close outcome, nothing else. `tiles`/`reachable` keep counting WINDOWS;
        // the desktop tile's slot/row/col are logged like any pick's, from the faces the View
        // actually registered.
        var capture = new Capture();
        using var h = new Harness(Windows(1, 2), logger: capture);
        h.Sample(Away, 0);
        h.Open();
        h.LayoutWindows(h.Vm.Tiles.Count);
        h.LayoutDesktop();
        h.Hold(Away, 30_000, 300_000);
        h.Hold(h.DesktopFace().Center, 330_000, 3_000_000);

        var lines = capture.Lines;
        Check.That(lines).HasSize(2);
        Check.That(lines[0]).IsEqualTo(
            "Task switcher opened: tiles=2 currentExcluded=false reachable=2 anchorValid=true sinceLastCloseMs=none lastOutcome=none tileDwellMs=800");
        Check.That(lines[1].StartsWith("Task switcher closed: outcome=Desktop ", StringComparison.Ordinal)).IsTrue();
        Check.That(lines[1].EndsWith(" slot=2 row=0 col=2", StringComparison.Ordinal)).IsTrue();
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<AppWindow> Windows(params int[] hwnds) =>
        hwnds.Select(h => new AppWindow(new IntPtr(h), $"Window {h}", (uint)(100 + h), "app")).ToArray();

    /// <summary><paramref name="count"/> ordinary windows, hwnds 1..count — the many-window case.</summary>
    private static IReadOnlyList<AppWindow> NumberedWindows(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new AppWindow(new IntPtr(i), $"Window {i}", (uint)(100 + i), "app"))
            .ToArray();

    private sealed class Harness : IDisposable
    {
        private readonly WeakReferenceMessenger _messenger = new();
        private readonly FakeWindowManager _windowManager;
        private readonly double _scale;

        internal TaskSwitcherViewModel Vm { get; }
        internal List<IntPtr[]> MinimiseRequests { get; } = new();
        internal List<IntPtr> Selections { get; } = new();

        internal Harness(
            IReadOnlyList<AppWindow> windows, IntPtr? foreground = null, double scale = 1.0,
            ILogger<TaskSwitcherViewModel>? logger = null)
        {
            _scale = scale;
            DpiHelper.Initialize(scale, scale);
            _windowManager = new FakeWindowManager(windows, foreground);
            Vm = new TaskSwitcherViewModel(
                _windowManager, new EyeCtrlSettings(), _messenger, logger: logger);
            _messenger.Register<Harness, TaskSwitcherShowDesktopMessage>(
                this, (_, m) => MinimiseRequests.Add(m.Hwnds.ToArray()));
            _messenger.Register<Harness, TaskSwitcherSelectionMessage>(
                this, (_, m) => Selections.Add(m.Hwnd));
        }

        internal void Open() => _messenger.Send(new TaskSwitcherToggledMessage());

        internal void Toggle() => _messenger.Send(new TaskSwitcherToggledMessage());

        /// <summary>The View's post-layout pass for the first <paramref name="count"/> window tiles.</summary>
        internal void LayoutWindows(int count)
        {
            for (var i = 0; i < count; i++)
            {
                var hwnd = Vm.Tiles[i].Hwnd;
                Vm.UpdateTilePosition(hwnd, FaceAt(i));
            }
        }

        /// <summary>…then the desktop tile (last), then the close disc (flushes the counts).</summary>
        internal void LayoutDesktop()
        {
            if (Vm.HasDesktopTile)
                Vm.UpdateDesktopTilePosition(FaceAt(Vm.Tiles.Count));
            Vm.UpdateCloseButtonPosition(CloseCenter);
        }

        /// <summary>The desktop tile's drawn face: the cell right after the last window tile.</summary>
        internal PhysicalRect DesktopFace() => FaceAt(Vm.Tiles.Count);

        /// <summary>One tile cell, in physical pixels at this harness's scale.</summary>
        internal PhysicalRect FaceAt(int index) =>
            new(index * 200.0 * _scale, 200.0 * _scale, 184.0 * _scale, 104.0 * _scale);

        internal PhysicalPoint P(double xDip, double yDip) => new(xDip * _scale, yDip * _scale);

        internal void Sample(PhysicalPoint p, long t) =>
            _messenger.Send(new GazeCursorMovedMessage(p, t));

        internal void Hold(PhysicalPoint p, long fromUs, long toUs, bool paused = false)
        {
            DwellDrive.Hold(fromUs, toUs, t => _messenger.Send(
                new GazeCursorMovedMessage(p, t) { IsPaused = paused }));
        }

        /// <summary>Dwells the desktop tile with a valid anchor far from it.</summary>
        internal void Dwell(PhysicalRect face)
        {
            Sample(Away, 0);
            Hold(Away, 30_000, 300_000);
            Hold(face.Center, 330_000, 3_000_000);
        }

        public void Dispose()
        {
            Vm.Dispose();
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    private sealed class Capture : ILogger<TaskSwitcherViewModel>
    {
        private readonly List<string> _lines = new();

        internal IReadOnlyList<string> Lines => _lines;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => _lines.Add(formatter(state, exception));
    }

    private sealed class FakeWindowManager : IWindowManager
    {
        private readonly IReadOnlyList<AppWindow> _windows;

        internal FakeWindowManager(IReadOnlyList<AppWindow> windows, IntPtr? foreground)
        {
            _windows = windows;
            Foreground = foreground;
        }

        internal IntPtr? Foreground { get; }

        public IReadOnlyList<AppWindow> EnumerateOpenWindows() => _windows;
        public void BringToFront(IntPtr hwnd) { }
        public IntPtr? TryGetEligibleForegroundWindow() => Foreground;
        public void Minimize(IntPtr hwnd) { }
        public void MinimizeNonBlocking(IntPtr hwnd) { }
        public void MaximizeOrRestore(IntPtr hwnd) { }
        public bool CloseWindow(IntPtr hwnd) => false;
    }
}
