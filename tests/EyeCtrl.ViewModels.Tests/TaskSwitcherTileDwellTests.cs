namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// S4-A — the switcher's tiles get their own look time: the window tiles and the "Show desktop"
/// tile register <c>max(TaskSwitcherTileDwellMs, ToolbarButtonDwellMs)</c> (default 800 ms, and a
/// tile is never quicker than the toolbar), while the ✕ close disc keeps the toolbar's value so
/// the escape stays quick. Pinned on the REGISTERED targets (DwellTarget.DwellTimeMicroseconds),
/// not on a VM property — the registration is the behaviour the gaze meets.
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class TaskSwitcherTileDwellTests
{
    [Theory]
    [InlineData(600, 800)]   // toolbar quicker than the tile time → the tile's own 800 wins
    [InlineData(900, 900)]   // toolbar slower → the tile follows the toolbar (max rule)
    public void TheTileTarget_CarriesMaxOfTileAndToolbar(int toolbarDwellMs, int expectedDwellMs)
    {
        using var h = new Harness(toolbarDwellMs);

        h.Open();
        h.RegisterTile(0);

        var target = h.Vm.DetectorForTest.Targets.Single(t => t.Id == h.TileHwnd(0).ToString());
        Check.That(target.DwellTimeMicroseconds).IsEqualTo(expectedDwellMs * 1000L);
    }

    [Theory]
    [InlineData(600, 800)]
    [InlineData(900, 900)]
    public void TheDesktopTileTarget_CarriesMaxOfTileAndToolbar(int toolbarDwellMs, int expectedDwellMs)
    {
        using var h = new Harness(toolbarDwellMs);

        h.Open();
        h.RegisterDesktopTile();

        var target = h.Vm.DetectorForTest.Targets.Single(t => t.Id == TaskSwitcherViewModel.DesktopTargetId);
        Check.That(target.DwellTimeMicroseconds).IsEqualTo(expectedDwellMs * 1000L);
    }

    [Fact]
    public void TheCloseTarget_KeepsTheToolbarDwell_EvenWhenTheTileDwellIsLonger()
    {
        // The escape must stay quick: with the tile knob at 900 and the toolbar at its 700 default,
        // the disc registers 700 — a mutant that moves the disc onto the tile time reads 900, red.
        using var h = new Harness(toolbarDwellMs: 700, tileDwellMs: 900);

        h.Open();
        h.RegisterCloseDisc();

        var target = h.Vm.DetectorForTest.Targets.Single(t => t.Id == TaskSwitcherViewModel.CloseTargetId);
        Check.That(target.DwellTimeMicroseconds).IsEqualTo(700 * 1000L);
    }

    [Fact]
    public void TheTileTarget_IsNeverQuickerThanASlowerToolbar_InOneSessionWithBothTiles()
    {
        // Window tile and desktop tile in the SAME session, toolbar slower than the tile knob:
        // all three surface targets together — two tiles at the toolbar's slower value, the disc
        // still at the toolbar value.
        using var h = new Harness(toolbarDwellMs: 900);

        h.Open();
        h.RegisterTile(0);
        h.RegisterDesktopTile();
        h.RegisterCloseDisc();

        // Order-insensitive on purpose: the detector's Targets collection is not insertion-ordered.
        var byId = h.Vm.DetectorForTest.Targets.ToDictionary(t => t.Id, t => t.DwellTimeMicroseconds / 1000);
        Check.That(byId).HasSize(3);
        Check.That(byId[h.TileHwnd(0).ToString()]).IsEqualTo(900L);
        Check.That(byId[TaskSwitcherViewModel.DesktopTargetId]).IsEqualTo(900L);
        Check.That(byId[TaskSwitcherViewModel.CloseTargetId]).IsEqualTo(900L);
    }

    [Fact]
    public void ARaisedTileKnob_AboveTheToolbar_ReachesBothTiles_AndLeavesTheDiscAtTheToolbar()
    {
        // S4-A fixup-01 (review finding 1): the knob's OWN value above its default — tile 1200,
        // toolbar 600 — must reach the window tile and the desktop tile alike (the planned
        // 800 → 1000 ladder step works exactly through this), while the ✕ stays at the toolbar.
        // Reviewer mutant (e), max(800, toolbar), ignores the setting and reads 800 here — red.
        using var h = new Harness(toolbarDwellMs: 600, tileDwellMs: 1200);

        h.Open();
        h.RegisterTile(0);
        h.RegisterDesktopTile();
        h.RegisterCloseDisc();

        // Order-insensitive on purpose: the detector's Targets collection is not insertion-ordered.
        var byId = h.Vm.DetectorForTest.Targets.ToDictionary(t => t.Id, t => t.DwellTimeMicroseconds / 1000);
        Check.That(byId[h.TileHwnd(0).ToString()]).IsEqualTo(1200L);
        Check.That(byId[TaskSwitcherViewModel.DesktopTargetId]).IsEqualTo(1200L);
        Check.That(byId[TaskSwitcherViewModel.CloseTargetId]).IsEqualTo(600L);
    }

    /// <summary>
    /// One open switcher session over one window (so the desktop tile exists too), with the
    /// toolbar's dwell set per test and the tile knob optionally overridden.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly WeakReferenceMessenger _messenger = new();
        private readonly EyeCtrlSettings _settings;

        internal TaskSwitcherViewModel Vm { get; }

        internal Harness(int toolbarDwellMs, int? tileDwellMs = null)
        {
            DpiHelper.Initialize(1.0, 1.0);
            _settings = new EyeCtrlSettings();
            _settings.Dwell.ToolbarButtonDwellMs = toolbarDwellMs;
            if (tileDwellMs is { } tile)
                _settings.Dwell.TaskSwitcherTileDwellMs = tile;
            Vm = new TaskSwitcherViewModel(
                new FakeWindowManager([new AppWindow(new IntPtr(101), "Editor", 200, "editor")]),
                _settings,
                _messenger);
        }

        internal void Open() => _messenger.Send(new TaskSwitcherToggledMessage());

        internal IntPtr TileHwnd(int index) => Vm.Tiles[index].Hwnd;

        internal void RegisterTile(int index) =>
            Vm.UpdateTilePosition(TileHwnd(index), new PhysicalRect(index * 200.0, 0, 184, 104));

        internal void RegisterDesktopTile() =>
            Vm.UpdateDesktopTilePosition(new PhysicalRect(400, 0, 184, 104));

        internal void RegisterCloseDisc() =>
            Vm.UpdateCloseButtonPosition(new PhysicalPoint(960, 1100));

        public void Dispose() => Vm.Dispose();

        private sealed class FakeWindowManager : IWindowManager
        {
            private readonly IReadOnlyList<AppWindow> _windows;

            internal FakeWindowManager(IReadOnlyList<AppWindow> windows) => _windows = windows;

            public IReadOnlyList<AppWindow> EnumerateOpenWindows() => _windows;
            public void BringToFront(IntPtr hwnd) { }
            public IntPtr? TryGetEligibleForegroundWindow() => null;
            public void Minimize(IntPtr hwnd) { }
            public void MinimizeNonBlocking(IntPtr hwnd) { }
            public void MaximizeOrRestore(IntPtr hwnd) { }
            public bool CloseWindow(IntPtr hwnd) => false;
        }
    }
}
