namespace EyeCtrl.App.Tests.Overlays;

using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// T6 fixup-06 G2 — in Edit, the note steps clear of the keyboard even when its anchor lies
/// under it. The owner's keyboard sits on the TOP edge of a 3840x2400 screen at 200 %: physical
/// (502, 0, 2836, 1150) = DIP (251, 0, 1418, 575) — almost the top half. An anchor under it has
/// no clear side, so the old clamp put the card ON the keyboard and the note was invisible.
/// <para>
/// The displaced candidates step the same card just outside the rect it hits, by the clearance
/// gap, inside the inset work area and clear of EVERY avoid rect; the closest accepted candidate
/// to the anchor wins. With no room at all the old clamp stands. Display, Move and the chips
/// keep the placements they always had — the displacement is the Edit state's alone.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class NoteKeyboardAvoidanceTests
{
    /// <summary>The owner's work area in DIP at 2.0 (1920x1152 DIP on the 3840x2400 screen).</summary>
    private static readonly Rect WorkArea = new(0, 0, 1920, 1152);

    /// <summary>The owner's keyboard on the TOP edge, in DIP: physical (502, 0, 2836, 1150) at 2.0.</summary>
    private static readonly Rect KeyboardTop = new(251, 0, 1418, 575);

    /// <summary>The keyboard's edge bar (the Close/Flip pair), on the same top edge.</summary>
    private static readonly Rect EdgeBarTop = new(251, 0, 1418, 40);

    /// <summary>The same keyboard on the BOTTOM edge.</summary>
    private static readonly Rect KeyboardBottom = new(251, 1152 - 575, 1418, 575);

    /// <summary>The same edge bar on the bottom edge.</summary>
    private static readonly Rect EdgeBarBottom = new(251, 1152 - 40, 1418, 40);

    private const double Scale = 2.0;

    /// <summary>The note card at the default text size: 550 wide, the control column's 340 tall.</summary>
    private static readonly Size Card = new(
        NoteLayout.CardWidthDip(NoteSettings.DefaultTextSizeDip),
        NoteLayout.CardHeightDip(NoteSettings.DefaultTextSizeDip, 0, WorkArea.Height));

    private static readonly Rect Inset = new(
        NoteLayout.Constants.EdgeMarginDip,
        NoteLayout.Constants.EdgeMarginDip,
        WorkArea.Width - 2 * NoteLayout.Constants.EdgeMarginDip,
        WorkArea.Height - 2 * NoteLayout.Constants.EdgeMarginDip);

    private static PhysicalPoint Phys(double dipX, double dipY) => new(dipX * Scale, dipY * Scale);

    private static NotePlacement Place(double dipX, double dipY, IReadOnlyList<Rect> avoid,
        bool allowDisplaced = true) =>
        NoteLayout.Place(Phys(dipX, dipY), NoteSide.Right, Card, WorkArea, avoid, Scale,
            exclude: null, allowDisplaced: allowDisplaced)!;

    // ── the pure layout: the owner's geometry, keyboard on the top edge ──

    [Theory]
    [InlineData(960, 300)]
    [InlineData(300, 100)]
    [InlineData(1600, 520)]
    public void AnAnchorUnderTheKeyboard_StepsClearOfIt(double x, double y)
    {
        var avoid = new[] { KeyboardTop, EdgeBarTop };
        var placement = Place(x, y, avoid);

        Check.WithCustomMessage($"the window {placement.Window} must lie inside the inset {Inset}")
            .That(placement.Window.Left >= Inset.Left && placement.Window.Top >= Inset.Top
                && placement.Window.Right <= Inset.Right && placement.Window.Bottom <= Inset.Bottom)
            .IsTrue();
        foreach (var rect in avoid)
        {
            Check.WithCustomMessage($"the window {placement.Window} must not touch the avoid rect {rect}")
                .That(placement.Window.IntersectsWith(rect)).IsFalse();
            Check.WithCustomMessage($"the window {placement.Window} must keep the clearance gap from {rect}")
                .That(GapFrom(placement.Window, rect)).IsGreaterOrEqualThan(NoteLayout.Constants.AvoidGapDip);
        }
    }

    [Theory]
    [InlineData(960, 300)]
    [InlineData(300, 100)]
    [InlineData(1600, 520)]
    public void AnAnchorUnderTheKeyboard_StepsClearOfIt_OnTheBottomEdgeToo(double x, double y)
    {
        var avoid = new[] { KeyboardBottom, EdgeBarBottom };
        var placement = Place(x, y, avoid);

        Check.WithCustomMessage($"the window {placement.Window} must lie inside the inset {Inset}")
            .That(placement.Window.Left >= Inset.Left && placement.Window.Top >= Inset.Top
                && placement.Window.Right <= Inset.Right && placement.Window.Bottom <= Inset.Bottom)
            .IsTrue();
        foreach (var rect in avoid)
        {
            Check.That(placement.Window.IntersectsWith(rect)).IsFalse();
            Check.That(GapFrom(placement.Window, rect)).IsGreaterOrEqualThan(NoteLayout.Constants.AvoidGapDip);
        }
    }

    [Fact]
    public void TheDisplacedWindow_IsTheClosestAcceptedCandidate()
    {
        // The expected window, derived by hand from the owner's geometry: the anchor at DIP
        // (960, 300) is under the top keyboard, so the card steps just BELOW it (the keyboard's
        // bottom edge plus the 24 DIP clearance), centred on the anchor.
        var placement = Place(960, 300, new[] { KeyboardTop, EdgeBarTop });

        Check.WithCustomMessage("the card must sit just below the keyboard, clear of the gap")
            .That(placement.Window.Top).IsEqualTo(KeyboardTop.Bottom + NoteLayout.Constants.AvoidGapDip);
        Check.WithCustomMessage("the card must be centred on the anchor")
            .That(placement.Window.Left + (placement.Window.Width / 2)).IsEqualTo(960);
    }

    [Fact]
    public void NoRoomClearOfTheKeyboard_TakesTheLeastOverlap()
    {
        // T6 fixup-09 Part B (owner-approved, review-08 section 6): the avoid rects cover the whole
        // work area, so nothing clears it. The placement is now the candidate that overlaps LEAST,
        // measured on the drawn window - never worse than the old preferred-side clamp, which is
        // itself one of the candidates.
        var avoid = new[] { new Rect(0, 0, 1920, 1152) };
        var placement = Place(960, 300, avoid);
        var old = Place(960, 300, avoid, allowDisplaced: false);

        double clearance = SignedClearance(placement.Window, avoid[0]);
        // Strictly greater (review-10): the old clamp is itself one of the candidates, so a
        // greater-or-equal was a tautology. Every candidate maximises the clearance, so at least
        // one of them must beat the preferred clamp in this degenerate case.
        Check.WithCustomMessage($"the least-overlap placement ({clearance}) must beat the old clamp ({SignedClearance(old.Window, avoid[0])})")
            .That(clearance).IsStrictlyGreaterThan(SignedClearance(old.Window, avoid[0]));
        Check.WithCustomMessage("and it stays inside the inset work area")
            .That(placement.Window.Left >= Inset.Left && placement.Window.Top >= Inset.Top
                && placement.Window.Right <= Inset.Right && placement.Window.Bottom <= Inset.Bottom)
            .IsTrue();
    }

    // ── T6 fixup-09 Part B: a tall card goes where it overlaps the keyboard least ──

    [Theory]
    [InlineData(300.0, 700.0, 28.0)]
    [InlineData(960.0, 760.0, 28.0)]
    [InlineData(960.0, 760.0, 48.0)]
    public void ATallEditCard_SlidesToTheLeastOverlap_InsteadOfLandingInsideTheKeyboard(
        double x, double y, double textSize)
    {
        // The owner's geometry, a 480 DIP card: taller than the clear room under the top keyboard,
        // so nothing fits. The drawn window must keep a non-negative clearance - it slides just
        // over the keyboard's edge (the reviewer measured 18 DIP at (300, 700)) - instead of the
        // old preferred-side clamp, which sat 70-130 DIP inside it.
        var keyboard = new Rect(251, 0, 1418, 575);
        var card = new Size(NoteLayout.CardWidthDip(textSize), 480);
        var placement = NoteLayout.Place(
            Phys(x, y), NoteSide.Right, card, WorkArea, new[] { keyboard }, Scale,
            exclude: null, allowDisplaced: true, topGrowthDip: NoteLayout.TagProtrusionDip(textSize))!;
        var drawn = NoteLayout.WithTagClearance(placement, textSize);

        double clearance = SignedClearance(drawn.Window, keyboard);
        Check.WithCustomMessage(
                $"a tall card at ({x}, {y}) and {textSize} DIP must not overlap the keyboard (clearance {clearance})")
            .That(clearance).IsGreaterOrEqualThan(0);
    }

    [StaFact]
    public void OutsideEdit_ThePlacementIsExactlyTheOneBeforeTheDisplacement()
    {
        // Display, Move and the chips never displace: a DISPLAY note with the keyboard on
        // screen keeps the placement it always had - the keyboard's rects are the EDIT state's
        // business alone.
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: () => WorkArea);
        var probe = new NoteManagerTests.FakeKeyboardProbe { IsVisible = true };
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            manager.CreateNoteAt(Phys(960, 300));
            var id = manager.ActiveNotes[0].Id;
            manager.Apply(new NoteAppend("saved text"));
            manager.FinishEdit(id, commit: true); // Display
            probe.RectsProvider = () => new[] { ToPhysical(KeyboardTop), ToPhysical(EdgeBarTop) };
            probe.RaiseChanged();

            var display = manager.PlacementForTest(id)!;
            var expected = NoteLayout.WithTagClearance(
                NoteLayout.Place(Phys(960, 300), display.Side, display.Card.Size, WorkArea,
                    Array.Empty<Rect>(), Scale)!,
                NoteSettings.DefaultTextSizeDip);
            Check.WithCustomMessage("outside Edit the placement must not change")
                .That(display.Window).IsEqualTo(expected.Window);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── the real path through the note manager ──

    [StaFact]
    public void InEdit_WithTheAnchorUnderTheKeyboard_TheNoteStepsClearOfIt()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: () => WorkArea);
        var probe = new NoteManagerTests.FakeKeyboardProbe { IsVisible = true };
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            // The anchor under the keyboard: DIP (960, 300) = physical (1920, 600).
            manager.CreateNoteAt(Phys(960, 300));
            var id = manager.ActiveNotes[0].Id;
            probe.RectsProvider = () => new[] { ToPhysical(KeyboardTop), ToPhysical(EdgeBarTop) };
            probe.RaiseChanged();

            var placement = manager.PlacementForTest(id)!;
            Check.WithCustomMessage($"the note {placement.Window} must not be under the keyboard")
                .That(placement.Window.IntersectsWith(KeyboardTop)).IsFalse();
            // The CONTROLS live on the card, so the card is what must keep the clearance: the
            // window's top strip is the tail zone and the number tag, pure chrome, and the tag
            // grows the window upward by its protrusion.
            Check.That(GapFrom(placement.Card, KeyboardTop))
                .IsGreaterOrEqualThan(NoteLayout.Constants.AvoidGapDip);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void Done_PutsTheNoteBackNextToItsAnchor()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: () => WorkArea);
        var probe = new NoteManagerTests.FakeKeyboardProbe { IsVisible = true };
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            manager.CreateNoteAt(Phys(960, 300));
            var id = manager.ActiveNotes[0].Id;
            probe.RectsProvider = () => new[] { ToPhysical(KeyboardTop), ToPhysical(EdgeBarTop) };
            probe.RaiseChanged();
            var displaced = manager.PlacementForTest(id)!;

            manager.Apply(new NoteAppend("typed text"));
            manager.FinishEdit(id, commit: true);

            var after = manager.PlacementForTest(id)!;
            Check.WithCustomMessage("Done must move the note back off the displaced spot")
                .That(after.Window).IsNotEqualTo(displaced.Window);

            // The Display placement next to the anchor: the same derivation with no avoid rects.
            var display = NoteLayout.WithTagClearance(
                NoteLayout.Place(Phys(960, 300), after.Side, after.Card.Size, WorkArea,
                    Array.Empty<Rect>(), Scale)!,
                NoteSettings.DefaultTextSizeDip);
            Check.WithCustomMessage("Done must leave the note next to its anchor")
                .That(after.Window).IsEqualTo(display.Window);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void AKeyboardFlipMidEdit_RePlacesTheNote()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: () => WorkArea);
        var probe = new NoteManagerTests.FakeKeyboardProbe { IsVisible = true };
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            manager.CreateNoteAt(Phys(960, 300));
            var id = manager.ActiveNotes[0].Id;
            probe.RectsProvider = () => new[] { ToPhysical(KeyboardTop), ToPhysical(EdgeBarTop) };
            probe.RaiseChanged();
            var top = manager.PlacementForTest(id)!.Window;

            // The keyboard flips to the bottom edge: the note must step clear of the NEW rect.
            probe.RectsProvider = () => new[] { ToPhysical(KeyboardBottom), ToPhysical(EdgeBarBottom) };
            probe.RaiseChanged();
            var bottom = manager.PlacementForTest(id)!.Window;

            Check.WithCustomMessage("the keyboard flip must re-place the note")
                .That(bottom).IsNotEqualTo(top);
            Check.That(bottom.IntersectsWith(KeyboardBottom)).IsFalse();
            Check.That(GapFrom(bottom, KeyboardBottom))
                .IsGreaterOrEqualThan(NoteLayout.Constants.AvoidGapDip);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void TheMoveStatesOtherSide_StaysTheSideThatExists()
    {
        // The Move state's "Other side" offer must remain a side that EXISTS beside the note: a
        // displaced window is neither, and the Move state is not Edit.
        //
        // The geometry is synthetic on purpose: the keyboard band covers the LEFT of the anchor
        // (so the other side's window is under it) while the card's own control column stands
        // clear of it (so the pick-up's dwell is not occluded). Then no side other than the
        // current one fits, the offer stays hidden - and a displacement would offer it anyway.
        var keyboard = new Rect(0, 0, 1200, 575);
        var anchorX = 1200.0;
        var anchorY = 300.0;

        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: () => WorkArea);
        var probe = new NoteManagerTests.FakeKeyboardProbe { IsVisible = true };
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            manager.CreateNoteAt(Phys(anchorX, anchorY));
            var id = manager.ActiveNotes[0].Id;
            manager.Apply(new NoteAppend("saved text"));
            manager.FinishEdit(id, commit: true);
            probe.RectsProvider = () => new[] { ToPhysical(keyboard) };
            probe.RaiseChanged();

            // Pick the note up for a Move: the other side is then judged over the avoid set.
            long present = 0;
            present = HoldGaze(manager, NoteTestBench.CellCentre(manager, id, 1), present);
            Check.That(manager.IsMoving).IsTrue();

            var current = manager.PlacementForTest(id)!;
            var next = current.Side == NoteSide.Right ? NoteSide.Left : NoteSide.Right;
            var expected = NoteLayout.Place(Phys(anchorX, anchorY), next, current.Card.Size, WorkArea,
                new[] { keyboard }, Scale, exclude: current.Side);

            Check.WithCustomMessage("the other side must not fit at all under the keyboard")
                .That(expected).IsNull();
            Check.WithCustomMessage("the Move's other side must be the one the pre-G2 code produced")
                .That(manager.JudgedOtherSideForTest).IsEqualTo(expected);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── the review's owner-geometry sweep (T6 fixup-07 F2) ──

    /// <summary>The sweep: the keyboard on the top and the bottom edge, the anchor swept across
    /// the screen, all four preferred sides and the three text sizes.</summary>
    public static TheoryData<string> OwnerGeometrySweep()
    {
        var data = new TheoryData<string>();
        double[] anchorsX = [100, 300, 960, 1600, 1820];
        double[] anchorsY = [300, 520, 746, 1000];
        var sides = new[] { NoteSide.Right, NoteSide.Left, NoteSide.Below, NoteSide.Above };
        var sizes = new[] { 18.0, 28.0, 48.0 };
        foreach (var edge in new[] { "top", "bottom" })
            foreach (var x in anchorsX)
                foreach (var y in anchorsY)
                    foreach (var side in sides)
                        foreach (var size in sizes)
                            data.Add($"{edge}|{x}|{y}|{side}|{size}");
        return data;
    }

    [Theory]
    [MemberData(nameof(OwnerGeometrySweep))]
    public void InEdit_TheCardItsCellsAndItsTag_KeepTheClearanceFromTheKeyboard(string sweep)
    {
        // T6 fixup-07 F2: in Edit the 24 DIP clearance holds in BOTH placement passes, measured
        // on the window that is DRAWN - the one the number tag grows upward. So the card, every
        // hit cell and the tag itself all stay a key's reach away from the keyboard.
        var parts = sweep.Split('|');
        bool bottom = parts[0] == "bottom";
        double x = double.Parse(parts[1], CultureInfo.InvariantCulture);
        double y = double.Parse(parts[2], CultureInfo.InvariantCulture);
        var side = Enum.Parse<NoteSide>(parts[3]);
        double textSize = double.Parse(parts[4], CultureInfo.InvariantCulture);

        var keyboard = bottom
            ? new Rect(251, 1152 - 575, 1418, 575)
            : new Rect(251, 0, 1418, 575);
        var card = new Size(
            NoteLayout.CardWidthDip(textSize),
            NoteLayout.CardHeightDip(textSize, 0, WorkArea.Height));

        var placement = NoteLayout.Place(
            Phys(x, y), side, card, WorkArea, new[] { keyboard }, Scale,
            exclude: null, allowDisplaced: true,
            topGrowthDip: NoteLayout.TagProtrusionDip(textSize));
        Check.That(placement).IsNotNull();

        // The DRAWN placement: the manager grows the window by the tag's protrusion.
        var drawn = NoteLayout.WithTagClearance(placement!, textSize);

        Check.WithCustomMessage($"the card of {sweep} must keep the clearance from the keyboard")
            .That(GapFrom(drawn.Card, keyboard)).IsGreaterOrEqualThan(NoteLayout.Constants.AvoidGapDip);

        for (int slot = 0; slot < 4; slot++)
        {
            var (cx, cy) = NoteLayout.ButtonCenter(drawn, slot);
            var cell = new Rect(
                drawn.Window.X + cx - (LayoutConstants.NoteCellDip / 2),
                drawn.Window.Y + cy - (LayoutConstants.NoteCellDip / 2),
                LayoutConstants.NoteCellDip, LayoutConstants.NoteCellDip);
            Check.WithCustomMessage($"cell {slot} of {sweep} must keep the clearance from the keyboard")
                .That(GapFrom(cell, keyboard)).IsGreaterOrEqualThan(NoteLayout.Constants.AvoidGapDip);
        }

        var tag = NoteLayout.TagRect(drawn, textSize);
        var tagRect = new Rect(
            drawn.Window.X + tag.X, drawn.Window.Y + tag.Y, tag.Width, tag.Height);
        Check.WithCustomMessage($"the number tag of {sweep} must keep the clearance from the keyboard")
            .That(GapFrom(tagRect, keyboard)).IsGreaterOrEqualThan(NoteLayout.Constants.AvoidGapDip);
    }

    // ── R1 (T6 fixup-08): the side's own clamped window competes ──

    [Fact]
    public void ABottomEdgeAnchor_KeepsTheCardNearItsAnchor()
    {
        // Review-07 R1: with the displaced pass offered to every failing side, a side that failed
        // only the SCREEN EDGE lost to a displaced candidate - an Edit note near the bottom edge
        // was pulled 186 DIP up, just under the keyboard. Each side's own clamped window now
        // competes, so the card stays by its anchor (about 93 DIP, as fixup-06 had it).
        double size = 28;
        var placement = NoteLayout.Place(
            Phys(820, 1140), NoteSide.Right, CardAt(size), WorkArea, new[] { KeyboardTop, EdgeBarTop }, Scale,
            exclude: null, allowDisplaced: true, topGrowthDip: NoteLayout.TagProtrusionDip(size))!;
        var drawn = NoteLayout.WithTagClearance(placement, size);

        double distance = DistanceFromPointToRect(new Point(820, 1140), drawn.Card);
        Check.WithCustomMessage($"the card must stay by its anchor (fixup-07 pulled it 186 DIP): {distance}")
            .That(distance).IsLessOrEqualThan(93);
    }

    // ── R2 (T6 fixup-08): the manager's tag growth at the Edit call site ──

    [StaFact]
    public void InEdit_At48_TheTagGrowthKeepsTheNumberTagOffTheKeyboard()
    {
        // Review-07 R2: the manager's Edit call site passes the tag's protrusion (26 DIP at 48)
        // as the placement's growth. An Above placement whose RAW window clears the keyboard by
        // 30 would put the number tag 4 DIP from it - only the growth decides, because the tag
        // straddles the card's top edge and reaches ABOVE the raw window.
        var keyboard = new Rect(251, 0, 1418, 575);
        var messenger = new WeakReferenceMessenger();
        var settings = new NoteSettings { TextSizeDip = 48 };
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: () => WorkArea, noteSettings: settings);
        var probe = new NoteManagerTests.FakeKeyboardProbe { IsVisible = true };
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            // The Above side's raw window top lands 30 DIP under the keyboard (605 = 575 + 30).
            manager.CreateNoteAt(Phys(960, 981));
            var id = manager.ActiveNotes[0].Id;
            probe.RectsProvider = () => new[] { ToPhysical(keyboard) };
            probe.RaiseChanged();

            var placement = manager.PlacementForTest(id)!;
            var tag = TagRect(placement, 48);
            Check.WithCustomMessage($"the number tag {tag} must keep the clearance from the keyboard")
                .That(GapFrom(tag, keyboard)).IsGreaterOrEqualThan(NoteLayout.Constants.AvoidGapDip);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [Fact]
    public void TheLeastOverlapTie_GoesToTheCandidateClosestToTheAnchor()
    {
        // Review-10: at (300, 700), 28 DIP, h480 two candidates tie at 18 DIP of clearance - the
        // anchor's own Left side and the Right one about 272 DIP away. The closest-to-anchor
        // tie-break keeps the note on its side; "ties go to the farthest" would jump it Right.
        var keyboard = new Rect(251, 0, 1418, 575);
        var card = new Size(NoteLayout.CardWidthDip(28), 480);
        var placement = NoteLayout.Place(
            Phys(300, 700), NoteSide.Right, card, WorkArea, new[] { keyboard }, Scale,
            exclude: null, allowDisplaced: true, topGrowthDip: NoteLayout.TagProtrusionDip(28))!;
        var drawn = NoteLayout.WithTagClearance(placement, 28);

        Check.WithCustomMessage($"the card must stay on the anchor's side (got {placement.Side} at {drawn.Window})")
            .That(placement.Side).IsEqualTo(NoteSide.Left);
        Check.WithCustomMessage("the drawn window must cover the anchor's x (300)")
            .That(drawn.Window.Left <= 300 && drawn.Window.Right >= 300).IsTrue();
        Check.WithCustomMessage("and keep the clearance")
            .That(SignedClearance(drawn.Window, keyboard)).IsGreaterOrEqualThan(0);
    }

    // ── helpers ──

    /// <summary>The number tag's rect in SCREEN coordinates (NoteLayout returns it window-relative).</summary>
    private static Rect TagRect(NotePlacement placement, double textSizeDip)
    {
        var tag = NoteLayout.TagRect(placement, textSizeDip);
        return new Rect(placement.Window.X + tag.X, placement.Window.Y + tag.Y, tag.Width, tag.Height);
    }

    /// <summary>The note card at a text size, at the control column's floor (no text measured).</summary>
    private static Size CardAt(double textSize) => new(
        NoteLayout.CardWidthDip(textSize),
        NoteLayout.CardHeightDip(textSize, 0, WorkArea.Height));

    /// <summary>The signed clearance between a window and a rect: positive when it clears on an
    /// axis, negative when it overlaps by that much (the production helper's test mirror).</summary>
    private static double SignedClearance(Rect window, Rect rect) => Math.Max(
        Math.Max(window.Left - rect.Right, rect.Left - window.Right),
        Math.Max(window.Top - rect.Bottom, rect.Top - window.Bottom));

    /// <summary>The distance from a point to a rect: zero inside it.</summary>
    private static double DistanceFromPointToRect(Point point, Rect rect)
    {
        double dx = Math.Max(rect.Left - point.X, 0) + Math.Max(point.X - rect.Right, 0);
        double dy = Math.Max(rect.Top - point.Y, 0) + Math.Max(point.Y - rect.Bottom, 0);
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>The clearance between a window and a rect: zero when they touch or overlap.</summary>
    private static double GapFrom(Rect window, Rect rect)
    {
        double dx = Math.Max(rect.Left - window.Right, 0) + Math.Max(window.Left - rect.Right, 0);
        double dy = Math.Max(rect.Top - window.Bottom, 0) + Math.Max(window.Top - rect.Bottom, 0);
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>A DIP rect as the probe reports it: physical pixels.</summary>
    private static PhysicalRect ToPhysical(Rect dip) => new(
        DpiHelper.LogicalToPhysicalX(dip.X),
        DpiHelper.LogicalToPhysicalY(dip.Y),
        DpiHelper.LogicalToPhysicalX(dip.Width),
        DpiHelper.LogicalToPhysicalY(dip.Height));

    /// <summary>Looks away past every control for 200 ms, then holds past the 600 ms dwell -
    /// the cadence the note manager's own dwells expect.</summary>
    private static long HoldGaze(NoteManager manager, PhysicalPoint point, long present, int ms = 1200)
    {
        var away = new PhysicalPoint(point.X + 10_000, point.Y);
        for (var t = present; t <= present + 220_000; t += 33_000)
            manager.OnGazeMoved(away, t);
        for (var t = present + 240_000; t <= present + ms * 1000; t += 33_000)
            manager.OnGazeMoved(point, t);
        return present + ms * 1000 + 33_000;
    }
}
