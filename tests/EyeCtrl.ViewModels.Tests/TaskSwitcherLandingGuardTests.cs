namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// T1 — the landing guard on the task switcher. Opening the switcher from the toolbar's
/// Task Switcher button leaves the gaze resting on row 1; without a guard the tile drawn under
/// that resting gaze fires 0.63–0.69 s later (9 of 29 picks in the owner's 07.–12.11 logs).
/// The agreed rule (raw samples only, no fixation stream):
/// <list type="bullet">
/// <item>(A) no tile accumulates dwell until a valid sample is more than 50 DIP from the gaze
/// at opening; an invalid anchor (no sample, or a gap) skips (A);</item>
/// <item>(B) after (A) lifts, a tile becomes eligible only on entry — a sample inside its face
/// whose previous valid sample was outside it; eligibility then persists for the session;</item>
/// <item>the close disc is NEVER gated.</item>
/// </list>
/// Every case runs at 100 % and 200 % DPI: the radius is 50 <em>DIP</em>, so a scale that
/// compares physical pixels against the DIP constant would pass at 1.0 and misfire at 2.0.
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class TaskSwitcherLandingGuardTests
{
    private const long Ms = 1_000;

    // DIP coordinates. Tile 1 spans x 308..492, y 348..452 at 100 %; tile 2 is 500 DIP away.
    private static readonly PhysicalPoint Tile1Dip = new(400, 400);
    private static readonly PhysicalPoint Tile2Dip = new(900, 400);
    private static readonly PhysicalPoint WithinTile1Dip = new(420, 400);
    private static readonly PhysicalPoint EmptyDip = new(700, 900);
    private static readonly PhysicalPoint CloseDip = new(1000, 1100);

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void OpeningGazeRestingOnATile_AccumulatesNothing_HoweverLongItRests(double scale)
    {
        using var h = new Harness(scale);

        // The gaze the toolbar button left behind: it is already on tile 1 when the switcher opens.
        h.Sample(h.Tile1, 0);
        h.Show();

        h.Hold(h.Tile1, 30_000, 3_000_000);

        Check.That(h.Selections).IsEmpty();
        Check.That(h.Vm.IsVisible).IsTrue();
        Check.That(h.Tile(1).DwellProgress).IsEqualTo(0);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void MovingWithinTheSameTile_ByMoreThan50Dip_StillDoesNotAccumulate(double scale)
    {
        using var h = new Harness(scale);

        // Anchor 80 DIP left of the point it moves to, both inside the same 184x104 face.
        h.Sample(h.P(340, 400), 0);
        h.Show();
        h.Hold(h.P(340, 400), 30_000, 200_000);

        h.Hold(h.WithinTile1, 230_000, 3_000_000);

        Check.That(h.Selections).IsEmpty();
        Check.That(h.Tile(1).DwellProgress).IsEqualTo(0);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void SmallDriftFromTheAnchorAcrossATileEdge_IsNotAnEntry(double scale)
    {
        using var h = new Harness(scale);

        // The anchor lands in the inter-tile gap 10 DIP outside tile 1's left face; tracker drift
        // then carries the gaze 20 DIP across the edge — below the 50 DIP release distance. (B)
        // alone would call this an entry (the previous sample was outside the face); (A) is the
        // gate that keeps a landing on the gap inert.
        h.Sample(h.P(298, 400), 0);
        h.Show();
        h.Hold(h.P(318, 400), 30_000, 3_000_000);

        Check.That(h.Selections).IsEmpty();
        Check.That(h.Tile(1).DwellProgress).IsEqualTo(0);

        // A real look still works: leave past the release distance, then enter the face.
        h.Hold(h.EmptyPoint(), 3_030_000, 3_300_000);
        h.Hold(h.P(318, 400), 3_330_000, 4_500_000);
        Check.That(h.Selections).ContainsExactly(new IntPtr(1));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void LeavingTheTile_ThenReEntering_DwellsAndActivates(double scale)
    {
        using var h = new Harness(scale);

        h.Sample(h.Tile1, 0);
        h.Show();
        h.Hold(h.Tile1, 30_000, 500_000);        // guarded rest
        h.Hold(h.EmptyPoint(), 530_000, 800_000);  // leave (> 50 DIP from the anchor)
        h.Hold(h.Tile1, 830_000, 2_000_000);     // fresh entry → normal dwell

        Check.That(h.Selections).ContainsExactly(new IntPtr(1));
        Check.That(h.Vm.IsVisible).IsFalse();
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void SaccadeFromTheAnchorStraightIntoAnotherTile_DwellsNormally(double scale)
    {
        using var h = new Harness(scale);

        h.Sample(h.Tile1, 0);
        h.Show();
        h.Hold(h.Tile2, 30_000, 1_200_000);

        Check.That(h.Selections).ContainsExactly(new IntPtr(2));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void InvalidAnchorAtOpen_OnlyTheEntryGateApplies(double scale)
    {
        using var h = new Harness(scale);

        // The last sample before opening is a gap: it carries no evidence of where the gaze is,
        // so (A) is skipped and the first real sample on a tile is an entry.
        h.Gap(h.Tile1, 0);
        h.Show();
        h.Hold(h.Tile1, 30_000, 1_200_000);

        Check.That(h.Selections).ContainsExactly(new IntPtr(1));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void GapAtOpen_WithTheLastValidSampleOnTheTile_StillNeedsAFreshEntry(double scale)
    {
        using var h = new Harness(scale);

        h.Sample(h.Tile1, 0);       // last valid sample is on tile 1
        h.Gap(h.Tile1, 30_000);     // …but the gaze is unknown at the moment of opening
        h.Show();
        h.Hold(h.Tile1, 60_000, 2_000_000);

        // (A) is skipped (no valid anchor). The entry gate still compares against the previous
        // VALID sample, which was inside this face — so a blink at opening must not turn the
        // resting look into a pick. Leaving and re-entering lifts it.
        Check.That(h.Selections).IsEmpty();

        h.Hold(h.EmptyPoint(), 2_030_000, 2_300_000);
        h.Hold(h.Tile1, 2_330_000, 3_500_000);
        Check.That(h.Selections).ContainsExactly(new IntPtr(1));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void CloseDisc_IsNeverGated_EvenWhenTheAnchorIsOnIt(double scale)
    {
        using var h = new Harness(scale);

        h.Sample(h.Close, 0);
        h.Show();
        h.Vm.UpdateCloseButtonPosition(h.Close);
        h.Hold(h.Close, 30_000, 1_200_000);

        Check.That(h.Selections).IsEmpty();
        Check.That(h.Vm.IsVisible).IsFalse();
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void PausedTileLook_AfterAFreshEntry_OnlyDismisses(double scale)
    {
        using var h = new Harness(scale);

        h.Sample(h.Tile1, 0);
        h.Show();
        h.Hold(h.EmptyPoint(), 30_000, 300_000);   // (A) lifts, no tile entered
        DwellDrive.Hold(330_000, 1_500_000, t => h.Messenger.Send(
            new GazeCursorMovedMessage(h.Tile1, t) { IsPaused = true }));

        Check.That(h.Selections).IsEmpty();      // paused: no OS input
        Check.That(h.Vm.IsVisible).IsFalse();    // …but the escape still works
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void GuardState_ResetsBetweenSessions(double scale)
    {
        using var h = new Harness(scale);

        h.Sample(h.Tile1, 0);
        h.Show();
        h.Hold(h.EmptyPoint(), 30_000, 300_000);
        h.Hold(h.Tile1, 330_000, 700_000);       // entered; eligibility is now live
        h.Hide();
        Check.That(h.Vm.IsVisible).IsFalse();

        // Reopen with the gaze back on the same tile. A guard that forgot to reset would keep
        // tile 1 eligible (the hwnd id is stable) and fire after the dwell time.
        h.Sample(h.Tile1, 730_000);
        h.Show();
        h.Hold(h.Tile1, 760_000, 3_500_000);

        Check.That(h.Selections).IsEmpty();
        Check.That(h.Tile(1).DwellProgress).IsEqualTo(0);
    }

    private sealed class Harness : IDisposable
    {
        private readonly double _scale;

        internal WeakReferenceMessenger Messenger { get; } = new();
        internal TaskSwitcherViewModel Vm { get; }
        internal List<IntPtr> Selections { get; } = new();

        internal Harness(double scale)
        {
            _scale = scale;
            DpiHelper.Initialize(scale, scale);
            Vm = new TaskSwitcherViewModel(new TwoWindowManager(), new EyeCtrlSettings(), Messenger);
            Messenger.Register<Harness, TaskSwitcherSelectionMessage>(
                this, (_, m) => Selections.Add(m.Hwnd));
        }

        internal PhysicalPoint Tile1 => P(Tile1Dip);
        internal PhysicalPoint Tile2 => P(Tile2Dip);
        internal PhysicalPoint WithinTile1 => P(WithinTile1Dip);
        internal PhysicalPoint Close => P(CloseDip);
        internal PhysicalPoint EmptyPoint() => P(EmptyDip);

        internal PhysicalPoint P(double xDip, double yDip) => new(xDip * _scale, yDip * _scale);
        internal PhysicalPoint P(PhysicalPoint dip) => P(dip.X, dip.Y);

        internal TaskSwitcherTileViewModel Tile(int hwnd) =>
            Vm.Tiles.Single(t => t.Hwnd == new IntPtr(hwnd));

        internal void Show()
        {
            Messenger.Send(new TaskSwitcherToggledMessage());
            Check.That(Vm.IsVisible).IsTrue();
            // The View registers each tile's drawn face after layout — the tile target exists
            // only from here on, which is what made the opening look able to fire.
            Vm.UpdateTilePosition(new IntPtr(1), Face(Tile1));
            Vm.UpdateTilePosition(new IntPtr(2), Face(Tile2));
        }

        internal void Hide() => Messenger.Send(new TaskSwitcherToggledMessage());

        internal PhysicalRect Face(PhysicalPoint center) =>
            PhysicalRect.FromCenter(center, 184 * _scale, 104 * _scale);

        internal void Sample(PhysicalPoint p, long t) =>
            Messenger.Send(new GazeCursorMovedMessage(p, t));

        internal void Gap(PhysicalPoint p, long t) =>
            Messenger.Send(new GazeCursorMovedMessage(new GazeFrame(p, t, IsInGap: true)));

        internal void Hold(PhysicalPoint p, long fromUs, long toUs) =>
            DwellDrive.Hold(Messenger, p, fromUs, toUs);

        public void Dispose()
        {
            Vm.Dispose();
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    private sealed class TwoWindowManager : IWindowManager
    {
        public IReadOnlyList<AppWindow> EnumerateOpenWindows() =>
        [
            new AppWindow(new IntPtr(1), "Notepad", 100, "notepad"),
            new AppWindow(new IntPtr(2), "Calculator", 200, "calc"),
        ];

        public void BringToFront(IntPtr hwnd) { }
        public IntPtr? TryGetEligibleForegroundWindow() => null;
        public void Minimize(IntPtr hwnd) { }
        public void MaximizeOrRestore(IntPtr hwnd) { }
        public bool CloseWindow(IntPtr hwnd) => false;
    }
}
