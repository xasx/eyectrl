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

    /// <summary>The note's EDIT card at the default text size (K2a): the compact card — text
    /// above, one row of three controls below — not the 468 DIP Display column.</summary>
    private static readonly Size Card = NoteLayout.EditCardSize(
        NoteSettings.DefaultTextSizeDip, 0, WorkArea.Height);

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
        // B: the avoid rect covers the whole work area, so there is no band to win - but the
        // screen-edge margin itself is part of the yield, and the farthest legal spot is the
        // 56 DIP floor, not A2's 80. The window stays inside the work area; the margin spends
        // no more than its 24 DIP extra.
        Check.WithCustomMessage("and it stays inside the work area")
            .That(WorkArea.Contains(placement.Window)).IsTrue();
        Check.WithCustomMessage("and spends no more than the margin's 24 DIP extra")
            .That(NoteLayout.GapAndMarginYield(placement.Window, avoid, WorkArea).MarginYield)
            .IsLessOrEqualThan(NoteLayout.Constants.EdgeMarginYieldDip + 1e-6);
    }

    // ── T6 fixup-09 Part B: a tall card spends the margin, then the gap, before it overlaps ──

    [Theory]
    [InlineData(300.0, 700.0, 28.0)]
    [InlineData(960.0, 760.0, 28.0)]
    [InlineData(960.0, 760.0, 48.0)]
    public void ATallEditCard_UsesTheYieldedMarginBeforeItOverlaps(
        double x, double y, double textSize)
    {
        // The owner's geometry, a SYNTHETIC 480 DIP card (the old four-row column). B: the card
        // is taller than the 80 DIP inset's band (1152 - 80 - 575 = 497), so it spends the
        // screen-edge margin's 24 DIP extra first: at 28 the drawn 496 fits the 56 DIP floor's
        // 521 DIP band with the full 24 gap, and at 48 the drawn 510 clears by 11. Either way it
        // is strictly better than the pre-B clamp (which put the 28 card 1 DIP and the 48 card
        // 13 DIP INSIDE the keyboard), and it stays inside the work area with the margin spent.
        var keyboard = new Rect(251, 0, 1418, 575);
        var card = new Size(NoteLayout.CardWidthDip(textSize), 480);
        var placement = NoteLayout.Place(
            Phys(x, y), NoteSide.Right, card, WorkArea, new[] { keyboard }, Scale,
            exclude: null, allowDisplaced: true, topGrowthDip: NoteLayout.TagProtrusionDip(textSize))!;
        var drawn = NoteLayout.WithTagClearance(placement, textSize);
        var old = NoteLayout.Place(
            Phys(x, y), NoteSide.Right, card, WorkArea, new[] { keyboard }, Scale,
            exclude: null, allowDisplaced: false)!;
        var oldDrawn = NoteLayout.WithTagClearance(old, textSize);

        double clearance = SignedClearance(drawn.Window, keyboard);
        Check.WithCustomMessage(
                $"a tall card at ({x}, {y}) and {textSize} DIP must beat the old clamp "
                + $"(new {clearance}, clamp {SignedClearance(oldDrawn.Window, keyboard)})")
            .That(clearance).IsStrictlyGreaterThan(SignedClearance(oldDrawn.Window, keyboard));
        Check.WithCustomMessage($"the window {drawn.Window} must stay inside the work area")
            .That(WorkArea.Contains(drawn.Window)).IsTrue();
        Check.WithCustomMessage("and spend no more than the margin's 24 DIP extra")
            .That(NoteLayout.GapAndMarginYield(drawn.Window, new[] { keyboard }, WorkArea).MarginYield)
            .IsLessOrEqualThan(NoteLayout.Constants.EdgeMarginYieldDip + 1e-6);
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
            var displayWindow = manager.WindowForTest(id)!;
            var expected = NoteLayout.WithRevealTargetClearance(
                NoteLayout.WithTagClearance(
                    NoteLayout.Place(Phys(960, 300), display.Side, display.Card.Size, WorkArea,
                        Array.Empty<Rect>(), Scale)!,
                    NoteSettings.DefaultTextSizeDip),
                NoteLayout.DisplayRestSize(
                    displayWindow.MeasureTextWidthDip("saved text"),
                    Math.Min(displayWindow.MeasureTextHeightDip("saved text"),
                        display.Card.Height - (2 * LayoutConstants.NoteButtonPadding)
                            - LayoutConstants.NoteCellDip)),
                Scale);
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

    // ── B: the NOTE-PLACE compromise line ──

    [StaFact]
    public void AShrunkGap_WritesOneContentFreeNotePlaceLine()
    {
        // The owner's Fn page at the 36 DIP default: the drawn 216 DIP card against the 209.5
        // DIP band. B's answer is a 17.5 DIP gap with the 24 DIP margin extra spent — and one
        // line recording the compromise, with no note text in it.
        var lines = new List<string>();
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: () => WorkArea, noteSettings: new NoteSettings { TextSizeDip = 36 },
            placeInfoLog: lines.Add);
        var probe = new NoteManagerTests.FakeKeyboardProbe { IsVisible = true };
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            probe.RectsProvider = () => new[] { ToPhysical(OwnerKeyboardTop(OwnerFnHeight)) };
            manager.CreateNoteAt(Phys(960, 400));
            manager.Apply(new NoteAppend("Zebra quokka"));
            probe.RaiseChanged();

            Check.WithCustomMessage("at least one placement compromise is reported")
                .That(lines).Not.IsEmpty();
            Check.WithCustomMessage("every line is the closed, content-free shape — the placement is unchanged")
                .That(lines).ContainsOnlyElementsThatMatch(
                    l => l == "NOTE-PLACE note=1 gap=17.5 overlap=0.0 margin=24.0 state=edit");
            Check.WithCustomMessage("no draft text reaches the line")
                .That(lines.Any(l => l.Contains("Zebra", StringComparison.Ordinal)
                    || l.Contains("quokka", StringComparison.Ordinal))).IsFalse();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void ASwitchToTheFnPageMidEdit_WritesTheApplyPathLine()
    {
        // Fixup-02 F8 (mutant N7: the log removed from ApplyPlacementToWindow so only the create
        // path logs). This is the owner's flow exactly: create against the letters page, which
        // leaves the full 24 DIP gap and writes nothing, then switch that same editor to the Fn
        // page. The taller keyboard shrinks the band, the re-place gives way (gap 17.5, the 24
        // DIP margin extra spent), and the applied placement is what the line reports. With the
        // apply-path log gone this scenario writes NO line at all - the create-path-only
        // survivor that AShrunkGap_WritesOneContentFreeNotePlaceLine left alive.
        var lines = new List<string>();
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: () => WorkArea, noteSettings: new NoteSettings { TextSizeDip = 36 },
            placeInfoLog: lines.Add);
        var probe = new NoteManagerTests.FakeKeyboardProbe { IsVisible = true };
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            // The letters page at 36 DIP: the card keeps the full gap and the margin, silent.
            probe.RectsProvider = () => new[] { ToPhysical(OwnerKeyboardTop(OwnerLettersHeight)) };
            manager.CreateNoteAt(Phys(960, 400));
            Check.WithCustomMessage("creating on the letters page must write no line")
                .That(lines).IsEmpty();

            // The mid-Edit page switch: the probe now reports the Fn height, so the manager
            // re-derives and APPLIES a different placement - the one that had to give way.
            probe.RectsProvider = () => new[] { ToPhysical(OwnerKeyboardTop(OwnerFnHeight)) };
            probe.RaiseChanged();

            Check.WithCustomMessage("the page switch must write exactly the one apply-path line")
                .That(lines).HasSize(1);
            Check.That(lines[0]).IsEqualTo("NOTE-PLACE note=1 gap=17.5 overlap=0.0 margin=24.0 state=edit");
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void ASpentMarginWithTheFullGap_StillWritesTheLine()
    {
        // The Fn page at 28 DIP: the gap stays the full 24 and only the margin yields (14.5 of
        // the 24 DIP extra). A compromise worth seeing — the line says so.
        var lines = new List<string>();
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: () => WorkArea, noteSettings: new NoteSettings { TextSizeDip = 28 },
            placeInfoLog: lines.Add);
        var probe = new NoteManagerTests.FakeKeyboardProbe { IsVisible = true };
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            probe.RectsProvider = () => new[] { ToPhysical(OwnerKeyboardTop(OwnerFnHeight)) };
            manager.CreateNoteAt(Phys(960, 400));

            Check.That(lines).HasSize(1);
            Check.That(lines[0]).IsEqualTo("NOTE-PLACE note=1 gap=24.0 overlap=0.0 margin=14.5 state=edit");
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void AFullGapAndMargin_WriteNothing()
    {
        // The letters page at 36 DIP: the 736 DIP keyboard leaves a 336 DIP band at the 80 DIP
        // inset, so both the gap and the margin hold — no line at all.
        var lines = new List<string>();
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: () => WorkArea, noteSettings: new NoteSettings { TextSizeDip = 36 },
            placeInfoLog: lines.Add);
        var probe = new NoteManagerTests.FakeKeyboardProbe { IsVisible = true };
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            probe.RectsProvider = () => new[] { ToPhysical(OwnerKeyboardTop(OwnerLettersHeight)) };
            manager.CreateNoteAt(Phys(960, 400));
            Check.That(lines).IsEmpty();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void TypingThatLeavesThePlacementAlone_WritesNoFurtherLines()
    {
        // Fixup-01 F3: NOTE-PLACE is one line per APPLIED placement, not per derivation. On
        // the yielded letters page at 36 every gaze-typed letter used to re-derive the SAME
        // gap-4 placement and write another line (the reviewer measured 1 after create and 11
        // after 10 letters). The line is now written only where a placement is applied, after
        // ApplyPlacementToWindow's identical-placement skip.
        var lines = new List<string>();
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: () => WorkArea, noteSettings: new NoteSettings { TextSizeDip = 36 },
            placeInfoLog: lines.Add);
        var probe = new NoteManagerTests.FakeKeyboardProbe { IsVisible = true };
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            probe.RectsProvider = () => new[] { ToPhysical(OwnerKeyboardYieldedTop(OwnerLettersHeight)) };
            manager.CreateNoteAt(Phys(960, 700));
            var id = manager.ActiveNotes[0].Id;
            var placed = manager.PlacementForTest(id)!;

            Check.WithCustomMessage("the create writes exactly the one compromise line")
                .That(lines).HasSize(1);

            for (int i = 0; i < 10; i++)
                manager.Apply(new NoteAppend("a"));

            Check.WithCustomMessage("the 10 letters must really leave the card where it was")
                .That(manager.PlacementForTest(id)).IsEqualTo(placed);
            Check.WithCustomMessage("and must add no further line (one placement, one line)")
                .That(lines).HasSize(1);
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

            // The Display placement next to the anchor: the same derivation with no avoid rects,
            // plus the tag strip and (F3) the reveal target's clearance.
            var afterWindow = manager.WindowForTest(id)!;
            var display = NoteLayout.WithRevealTargetClearance(
                NoteLayout.WithTagClearance(
                    NoteLayout.Place(Phys(960, 300), after.Side, after.Card.Size, WorkArea,
                        Array.Empty<Rect>(), Scale)!,
                    NoteSettings.DefaultTextSizeDip),
                NoteLayout.DisplayRestSize(
                    afterWindow.MeasureTextWidthDip("typed text"),
                    Math.Min(afterWindow.MeasureTextHeightDip("typed text"),
                        after.Card.Height - (2 * LayoutConstants.NoteButtonPadding)
                            - LayoutConstants.NoteCellDip)),
                Scale);
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
    public void AKeyboardYieldAndUnYieldMidEdit_MovesTheNoteAndItsCellsClearOfTheKeyboard()
    {
        // The reported bug's shape, on the top edge: a note enters Edit while the keyboard is
        // DOCKED, so it steps clear of the docked rect. The toolbar appears on that edge and the
        // keyboard yields 132 DIP toward the centre — straight onto the note. The toolbar hides,
        // and the gaze-off un-yield takes the keyboard back to the docked rect. Both are position
        // changes only: no size change, no VM flip, no button change. Without the probe's position
        // source the keyboard's rects never reach the note and the yielded keyboard covers its
        // text, ✓ Done and ↩ Discard (the yield half of the report's "The same holds for a yield
        // that moves the keyboard onto a note").
        var yielded = new Rect(KeyboardTop.Left, KeyboardTop.Top + 132, KeyboardTop.Width, KeyboardTop.Height);
        var yieldedBar = new Rect(EdgeBarTop.Left, EdgeBarTop.Top + 132, EdgeBarTop.Width, EdgeBarTop.Height);

        var messenger = new WeakReferenceMessenger();
        // R3: pinned at 28 DIP. The 24 DIP rules this test pins were authored for the 550 DIP
        // card; at the 36 DIP default the drawn card is 362 DIP tall (340 + the tag's 22), so
        // below a keyboard yielded 132 DIP it no longer fits the 381 DIP of clear room, and the
        // least-overlap fallback leaves 19 DIP (card 41, tag strip 19 — no overlap). The yield
        // MECHANICS are this test's subject, so it keeps its original geometry.
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: () => WorkArea, noteSettings: new NoteSettings { TextSizeDip = 28 });
        var probe = new NoteManagerTests.FakeKeyboardProbe { IsVisible = true };
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            // Docked: the anchor lies under the keyboard, and Edit steps the card clear of it.
            probe.RectsProvider = () => new[] { ToPhysical(KeyboardTop), ToPhysical(EdgeBarTop) };
            manager.CreateNoteAt(Phys(960, 300));
            var id = manager.ActiveNotes[0].Id;
            probe.RaiseChanged();
            var docked = manager.PlacementForTest(id)!;
            Check.WithCustomMessage($"the docked keyboard must not cover the note ({docked.Window})")
                .That(docked.Window.IntersectsWith(KeyboardTop)).IsFalse();

            // The toolbar appears on the keyboard's edge: the yield moves the keyboard onto the
            // note's spot. The probe's raise must re-place it clear of the yielded rect.
            probe.RectsProvider = () => new[] { ToPhysical(yielded), ToPhysical(yieldedBar) };
            probe.RaiseChanged();

            var afterYield = manager.PlacementForTest(id)!;
            Check.WithCustomMessage(
                    $"the caretaker never saw the keyboard move: the note stayed at {docked.Window} "
                    + $"under the yielded keyboard {yielded}")
                .That(afterYield.Window).IsNotEqualTo(docked.Window);
            Check.WithCustomMessage($"the note {afterYield.Window} must clear the yielded keyboard {yielded}")
                .That(afterYield.Window.IntersectsWith(yielded)).IsFalse();
            Check.That(GapFrom(afterYield.Window, yielded))
                .IsGreaterOrEqualThan(NoteLayout.Constants.AvoidGapDip);
            AssertCellsClear(afterYield, yielded, yieldedBar);

            // The toolbar hides and the gaze-off un-yield returns the keyboard to the docked edge:
            // the note and both of its exit cells stay clear of the rect it now occupies.
            probe.RectsProvider = () => new[] { ToPhysical(KeyboardTop), ToPhysical(EdgeBarTop) };
            probe.RaiseChanged();
            var afterUnYield = manager.PlacementForTest(id)!;
            Check.WithCustomMessage(
                    $"the note {afterUnYield.Window} must clear the un-yielded keyboard {KeyboardTop}")
                .That(afterUnYield.Window.IntersectsWith(KeyboardTop)).IsFalse();
            Check.That(GapFrom(afterUnYield.Window, KeyboardTop))
                .IsGreaterOrEqualThan(NoteLayout.Constants.AvoidGapDip);
            AssertCellsClear(afterUnYield, KeyboardTop, EdgeBarTop);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void AKeyboardRePlaceThatMovesTheEditingCard_PublishesNoteTargetsChanged_Once()
    {
        // K1 fixup-01 F2: the activation chrome zones re-publish on NoteTargetsChangedMessage
        // only. A probe raise that MOVES the Edit card must send exactly one; an identical raise
        // (ApplyPlacementToWindow's skip) must send none, or the zones go stale and offers are
        // refused over the card's old spot and allowed over the new one.
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: () => WorkArea, noteSettings: new NoteSettings { TextSizeDip = 36 });
        var probe = new NoteManagerTests.FakeKeyboardProbe { IsVisible = true };
        manager.SetKeyboardProbe(probe);
        var raised = 0;
        messenger.Register(new NoteManagerTests.NoteTargetsProbe(() => raised++));

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            probe.RectsProvider = () => new[] { ToPhysical(KeyboardTop), ToPhysical(EdgeBarTop) };
            manager.CreateNoteAt(Phys(960, 300));
            var id = manager.ActiveNotes[0].Id;
            var placed = manager.PlacementForTest(id)!.Window;
            int afterCreate = raised;

            // Same rects: the derived placement is identical, so the skip stays silent.
            probe.RaiseChanged();
            Check.That(manager.PlacementForTest(id)!.Window).IsEqualTo(placed);
            Check.WithCustomMessage("an unchanged keyboard re-place must not publish")
                .That(raised).IsEqualTo(afterCreate);

            // The keyboard moves down onto the card: the re-place moves it, so the zones must be
            // re-published exactly once.
            var moved = new Rect(KeyboardTop.Left, KeyboardTop.Top + 300, KeyboardTop.Width, KeyboardTop.Height);
            probe.RectsProvider = () => new[] { ToPhysical(moved) };
            probe.RaiseChanged();
            Check.WithCustomMessage("the raise must move the card")
                .That(manager.PlacementForTest(id)!.Window).IsNotEqualTo(placed);
            Check.WithCustomMessage("a moved keyboard re-place must publish NoteTargetsChangedMessage exactly once")
                .That(raised).IsEqualTo(afterCreate + 1);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    /// <summary>The ✓ Done (slot 0) and ↩ Discard (slot 3) dwell cells of an Edit placement must
    /// stay a key's reach clear of every rect the keyboard occupies — the cells are what the
    /// caretaker dwells on, not the card's chrome. K2a: those controls sit in the Edit row, so
    /// the cells are read through the state-aware centre.</summary>
    private static void AssertCellsClear(NotePlacement placement, params Rect[] keyboardRects)
    {
        foreach (var slot in new[] { 0, 3 })
        {
            var (cx, cy) = NoteLayout.ButtonCenter(placement, slot, NoteState.Edit);
            var cell = new Rect(
                placement.Window.X + cx - (LayoutConstants.NoteCellDip / 2),
                placement.Window.Y + cy - (LayoutConstants.NoteCellDip / 2),
                LayoutConstants.NoteCellDip, LayoutConstants.NoteCellDip);
            foreach (var rect in keyboardRects)
            {
                Check.WithCustomMessage($"dwell cell {slot} ({cell}) must not sit under the keyboard ({rect})")
                    .That(cell.IntersectsWith(rect)).IsFalse();
                Check.That(GapFrom(cell, rect))
                    .IsGreaterOrEqualThan(NoteLayout.Constants.AvoidGapDip);
            }
        }
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
        // R3: pinned at 28 DIP, like the yield test above. The synthetic premise needs the
        // 550 DIP card, whose right-hand column lands at x 1800 — clear of the keyboard band's
        // 1200 edge; at the 36 DIP default the Display note falls to a Left placement whose
        // column sits inside the band, so the ↕ pick-up (the subject here) could not even start.
        // The OTHER-SIDE rule is what this test pins, not the default size.
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: () => WorkArea, noteSettings: new NoteSettings { TextSizeDip = 28 });
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
            // D: reveal the Display row, then dwell ↕.
            present = NoteTestBench.Reveal(manager, id, present);
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
        var sizes = new[] { 18.0, 28.0, 36.0, 48.0 };
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
        // K2a: an EDITING note places the compact card (text above, one row of controls below).
        var card = NoteLayout.EditCardSize(textSize, 0, WorkArea.Height);

        var placement = NoteLayout.Place(
            Phys(x, y), side, card, WorkArea, new[] { keyboard }, Scale,
            exclude: null, allowDisplaced: true,
            topGrowthDip: NoteLayout.TagProtrusionDip(textSize));
        Check.That(placement).IsNotNull();

        // The DRAWN placement: the manager grows the window by the tag's protrusion.
        var drawn = NoteLayout.WithTagClearance(placement!, textSize);

        AssertDrawnPlacementKeepsClearance(drawn, keyboard, textSize, sweep);
    }

    /// <summary>The sweep's three rules (T6 fixup-07 F2; K2a's Edit row): the card, every
    /// registered control cell and the number tag keep the 24 DIP clearance from the keyboard on
    /// the window that is DRAWN.</summary>
    private static void AssertDrawnPlacementKeepsClearance(
        NotePlacement drawn, Rect keyboard, double textSize, string label)
    {
        Check.WithCustomMessage($"the card of {label} must keep the clearance from the keyboard")
            .That(GapFrom(drawn.Card, keyboard)).IsGreaterOrEqualThan(NoteLayout.Constants.AvoidGapDip);

        foreach (var slot in NoteLayout.SlotsFor(NoteState.Edit))
        {
            var (cx, cy) = NoteLayout.ButtonCenter(drawn, slot, NoteState.Edit);
            var cell = new Rect(
                drawn.Window.X + cx - (LayoutConstants.NoteCellDip / 2),
                drawn.Window.Y + cy - (LayoutConstants.NoteCellDip / 2),
                LayoutConstants.NoteCellDip, LayoutConstants.NoteCellDip);
            Check.WithCustomMessage($"cell {slot} of {label} must keep the clearance from the keyboard")
                .That(GapFrom(cell, keyboard)).IsGreaterOrEqualThan(NoteLayout.Constants.AvoidGapDip);
        }

        var tag = NoteLayout.TagRect(drawn, textSize);
        var tagRect = new Rect(
            drawn.Window.X + tag.X, drawn.Window.Y + tag.Y, tag.Width, tag.Height);
        Check.WithCustomMessage($"the number tag of {label} must keep the clearance from the keyboard")
            .That(GapFrom(tagRect, keyboard)).IsGreaterOrEqualThan(NoteLayout.Constants.AvoidGapDip);
    }

    // ── K1 (sitting 10): the OWNER's real keyboard geometry ──

    /// <summary>F17 (#383): the owner's keyboard at key size 1.75, MEASURED from the real
    /// window at 2.0 — 1642 x 736 DIP on the letters, 123, symbols and accents pages (it was
    /// 662.5) and 1641.2 x 862.5 on the Fn page (it was 789). The sweep uses the letters
    /// page's width for both pages (0.8 DIP wider than the Fn window, so the side strips are
    /// never over-estimated).</summary>
    private const double OwnerLettersHeight = 736.0;

    private const double OwnerFnHeight = 862.5;

    private const double OwnerKeyboardX = 139.0;

    private const double OwnerKeyboardWidth = 1642.0;

    /// <summary>The owner's keyboard docked on the TOP edge, at either page's height.</summary>
    private static Rect OwnerKeyboardTop(double height) => new(OwnerKeyboardX, 0, OwnerKeyboardWidth, height);

    /// <summary>The same keyboard docked at the bottom edge: top = 1152 - height.</summary>
    private static Rect OwnerKeyboardBottom(double height) =>
        new(OwnerKeyboardX, 1152 - height, OwnerKeyboardWidth, height);

    /// <summary>The owner's top keyboard YIELDED below the 140 DIP top toolbar.</summary>
    private static Rect OwnerKeyboardYieldedTop(double height) =>
        new(OwnerKeyboardX, 140, OwnerKeyboardWidth, height);

    /// <summary>The real-keyboard sweep: the three placements above at BOTH F17 page heights,
    /// the anchor rows of the old sweep plus y 100/150 (just under the toolbar), all four sides
    /// and the four text sizes.</summary>
    public static TheoryData<string> OwnerRealGeometrySweep()
    {
        var data = new TheoryData<string>();
        double[] anchorsX = [100, 300, 960, 1600, 1820];
        double[] anchorsY = [100, 150, 300, 520, 746, 1000];
        var sides = new[] { NoteSide.Right, NoteSide.Left, NoteSide.Below, NoteSide.Above };
        var sizes = new[] { 18.0, 28.0, 36.0, 48.0 };
        foreach (var variant in new[] { "top", "bottom", "yielded" })
            foreach (var page in new[] { "letters", "fn" })
                foreach (var x in anchorsX)
                    foreach (var y in anchorsY)
                        foreach (var side in sides)
                            foreach (var size in sizes)
                                data.Add($"{variant}|{page}|{x}|{y}|{side}|{size}");
        return data;
    }

    [Theory]
    [MemberData(nameof(OwnerRealGeometrySweep))]
    public void InEdit_OnTheOwnerRealKeyboard_TheGapGivesWayInTheRightOrder(string sweep)
    {
        // B: the F17 keyboard is 736 DIP (letters) / 862.5 DIP (Fn) and the owner's 36 DIP
        // Edit card is 194 DIP + the tag's 22 = 216 drawn. The Fn dock leaves a 209.5 DIP band
        // at the 80 DIP inset and the yielded letters dock 196 — so the 24 DIP gap and the
        // 80 DIP margin cannot always both hold. The rule this pins: the margin yields first
        // (down to 56), then the gap shrinks; only when nothing fits at gap 0 does the overlap
        // appear, and then it is exactly the geometry's minimum.
        var parts = sweep.Split('|');
        var variant = parts[0];
        string page = parts[1];
        double x = double.Parse(parts[2], CultureInfo.InvariantCulture);
        double y = double.Parse(parts[3], CultureInfo.InvariantCulture);
        var side = Enum.Parse<NoteSide>(parts[4]);
        double textSize = double.Parse(parts[5], CultureInfo.InvariantCulture);

        double height = page == "fn" ? OwnerFnHeight : OwnerLettersHeight;
        var keyboard = variant switch
        {
            "top" => OwnerKeyboardTop(height),
            "bottom" => OwnerKeyboardBottom(height),
            _ => OwnerKeyboardYieldedTop(height),
        };
        // K2a: an EDITING note places the compact card (text above, one row of controls below).
        var card = NoteLayout.EditCardSize(textSize, 0, WorkArea.Height);

        var placement = NoteLayout.Place(
            Phys(x, y), side, card, WorkArea, new[] { keyboard }, Scale,
            exclude: null, allowDisplaced: true,
            topGrowthDip: NoteLayout.TagProtrusionDip(textSize));
        Check.That(placement).IsNotNull();
        var drawn = NoteLayout.WithTagClearance(placement!, textSize);

        // The geometry's maximum achievable gap: the free band between the keyboard and the far
        // work-area edge at the 56 DIP margin floor, minus the drawn window. On this screen the
        // side strips are 83 DIP at the floor (too narrow for any card), so the band is the only
        // candidate and the number below is the best any placement can do.
        double bandAtFloor = BandAt(keyboard, NoteLayout.Constants.EdgeMarginFloorDip) - drawn.Window.Height;
        double bandAt80 = BandAt(keyboard, NoteLayout.Constants.EdgeMarginDip) - drawn.Window.Height;
        double best = Math.Min(NoteLayout.Constants.AvoidGapDip, bandAtFloor);
        double gap = SignedClearance(drawn.Window, keyboard);
        var (_, marginYield) = NoteLayout.GapAndMarginYield(drawn.Window, new[] { keyboard }, WorkArea);

        string label = $"{sweep} (drawn {drawn.Window.Height:F1}, band at floor {bandAtFloor:F1}, best {best:F1}, gap {gap:F1})";
        Check.WithCustomMessage($"{label}: the gap must be at least the geometry's best")
            .That(gap).IsGreaterOrEqualThan(best - 1e-6);
        if (best < 0)
        {
            // Nothing clears even at gap 0: the overlap must be EXACTLY the minimum.
            Check.WithCustomMessage($"{label}: the overlap must be the minimum")
                .That(gap).IsLessOrEqualThan(best + 1e-6);
        }
        Check.WithCustomMessage($"{label}: the drawn window must never leave the work area")
            .That(drawn.Window.Left >= WorkArea.Left && drawn.Window.Top >= WorkArea.Top
                && drawn.Window.Right <= WorkArea.Right && drawn.Window.Bottom <= WorkArea.Bottom)
            .IsTrue();
        Check.WithCustomMessage($"{label}: the margin may spend at most its 24 DIP extra")
            .That(marginYield).IsLessOrEqualThan(NoteLayout.Constants.EdgeMarginYieldDip + 1e-6);

        // Fixup-01 F2: every keyboard here is a full-width horizontal band, so the card can
        // only ever need to move UP or DOWN. The yielding 56 DIP floor is a SHIFT-axis floor:
        // the horizontal design margin (80 DIP) must hold on every one of these cases, whatever
        // the vertical margin spends. Before the fix 768 of the 2880 placements sat at x = 56.
        double designMargin = NoteLayout.Constants.EdgeMarginDip;
        double horizontalYield = Math.Max(0, Math.Max(
            designMargin - drawn.Window.Left, drawn.Window.Right - (WorkArea.Right - designMargin)));
        Check.WithCustomMessage($"{label}: the horizontal margin (x) must never yield on a horizontal band")
            .That(horizontalYield).IsLessOrEqualThan(1e-6);

        // Fixup-01 F6: where the full 80 DIP margin and the 24 DIP gap BOTH fit, the old inset
        // pin holds exactly - pass 1/2 must not be loosened by the yielding ladder.
        if (bandAt80 >= NoteLayout.Constants.AvoidGapDip)
            Check.WithCustomMessage($"{label}: at 80/24 the margin must stay at its design value")
                .That(marginYield).IsEqualTo(0.0);

        AssertDrawnKeepsItsGap(drawn, keyboard, textSize, label, gap);
    }

    /// <summary>The free band between the keyboard and the far work-area edge with the screen-edge
    /// margin at <paramref name="margin"/>: the most room any placement on that side can have.</summary>
    private static double BandAt(Rect keyboard, double margin)
    {
        bool atTop = Math.Abs(keyboard.Top - WorkArea.Top) <= Math.Abs(keyboard.Bottom - WorkArea.Bottom);
        return atTop ? WorkArea.Bottom - margin - keyboard.Bottom : keyboard.Top - WorkArea.Top - margin;
    }

    /// <summary>The card, every registered Edit control cell and the number tag keep at least
    /// the window's own achieved keyboard gap (they are all inside the drawn window).</summary>
    private static void AssertDrawnKeepsItsGap(
        NotePlacement drawn, Rect keyboard, double textSize, string label, double gap)
    {
        Check.WithCustomMessage($"{label}: the card must keep the achieved gap")
            .That(GapFrom(drawn.Card, keyboard)).IsGreaterOrEqualThan(gap - 1e-6);

        foreach (var slot in NoteLayout.SlotsFor(NoteState.Edit))
        {
            var (cx, cy) = NoteLayout.ButtonCenter(drawn, slot, NoteState.Edit);
            var cell = new Rect(
                drawn.Window.X + cx - (LayoutConstants.NoteCellDip / 2),
                drawn.Window.Y + cy - (LayoutConstants.NoteCellDip / 2),
                LayoutConstants.NoteCellDip, LayoutConstants.NoteCellDip);
            Check.WithCustomMessage($"{label}: control cell {slot} must keep the achieved gap")
                .That(GapFrom(cell, keyboard)).IsGreaterOrEqualThan(gap - 1e-6);
            Check.WithCustomMessage($"{label}: control cell {slot} must stay inside the work area")
                .That(WorkArea.Contains(cell)).IsTrue();
        }

        var tag = TagRect(drawn, textSize);
        Check.WithCustomMessage($"{label}: the number tag must keep the achieved gap")
            .That(GapFrom(tag, keyboard)).IsGreaterOrEqualThan(gap - 1e-6);
        Check.WithCustomMessage($"{label}: the number tag must stay inside the work area")
            .That(WorkArea.Contains(tag)).IsTrue();
    }

    /// <summary>B's own pin: the compact Edit card under the yielded top keyboard, per size.
    /// K2a's claim that the card fits the 24 DIP gap is gone after F17: the band at the 80 DIP
    /// inset is 196 DIP, and only the 56 DIP floor's extra 24 lets the gap hold at 18 (a 9 DIP
    /// margin spend). At 28/36 the gap itself gives way, and at 48 the card overlaps by 19 —
    /// the exact minimum the geometry allows.
    /// <para>
    /// The band: 1152 - 2 x 80 (A2's inset) - 140 (the toolbar) - 736 (F17's letters page)
    /// = 196 DIP. At the 56 DIP floor it is 220 DIP.
    /// </para></summary>
    [Theory]
    [InlineData(18.0, 24.0, 9.0)]
    [InlineData(28.0, 20.0, 24.0)]
    [InlineData(36.0, 4.0, 24.0)]
    [InlineData(48.0, -19.0, 24.0)]
    public void TheCompactEditCard_GivesWayToTheYieldedTopKeyboard(
        double textSize, double expectedGap, double expectedMargin)
    {
        double freeBand = Inset.Bottom - OwnerKeyboardYieldedTop(OwnerLettersHeight).Bottom;
        Check.WithCustomMessage(
                "F17: the 80 DIP margin leaves 196 DIP under the yielded 736 DIP letters keyboard")
            .That(freeBand).IsCloseTo(196, 1e-6);

        var placement = NoteLayout.Place(
            Phys(960, 700), NoteSide.Right, NoteLayout.EditCardSize(textSize, 0, WorkArea.Height),
            WorkArea, new[] { OwnerKeyboardYieldedTop(OwnerLettersHeight) }, Scale,
            exclude: null, allowDisplaced: true,
            topGrowthDip: NoteLayout.TagProtrusionDip(textSize))!;
        var drawn = NoteLayout.WithTagClearance(placement, textSize);
        var (gap, margin) = NoteLayout.GapAndMarginYield(
            drawn.Window, new[] { OwnerKeyboardYieldedTop(OwnerLettersHeight) }, WorkArea);

        Check.WithCustomMessage(
                $"{textSize} DIP: the achieved gap must be {expectedGap:F1} (overlap when negative)")
            .That(gap).IsCloseTo(expectedGap, 1e-6);
        Check.WithCustomMessage($"{textSize} DIP: the margin must yield {expectedMargin:F1} DIP")
            .That(margin).IsCloseTo(expectedMargin, 1e-6);
        Check.WithCustomMessage($"{textSize} DIP: the drawn card must stay inside the work area")
            .That(WorkArea.Contains(drawn.Window)).IsTrue();
    }

    // ── R1 (T6 fixup-08): the side's own clamped window competes ──

    [Fact]
    public void ABottomEdgeAnchor_KeepsTheCardNearItsAnchor()
    {
        // Review-07 R1: with the displaced pass offered to every failing side, a side that failed
        // only the SCREEN EDGE lost to a displaced candidate - an Edit note near the bottom edge
        // was pulled 186 DIP up, just under the keyboard. Each side's own clamped window now
        // competes, so the card stays by its anchor. The exact distance follows the geometry: 93
        // DIP at the pre-A2 card and 64 DIP margin, 98 at A2's compact Edit card and 80 margin —
        // still far from the 186 the pull-up caused.
        double size = 28;
        var placement = NoteLayout.Place(
            Phys(820, 1140), NoteSide.Right, CardAt(size), WorkArea, new[] { KeyboardTop, EdgeBarTop }, Scale,
            exclude: null, allowDisplaced: true, topGrowthDip: NoteLayout.TagProtrusionDip(size))!;
        var drawn = NoteLayout.WithTagClearance(placement, size);

        double distance = DistanceFromPointToRect(new Point(820, 1140), drawn.Card);
        Check.WithCustomMessage($"the card must stay by its anchor (fixup-07 pulled it 186 DIP): {distance}")
            .That(distance).IsLessOrEqualThan(100);
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
        // Review-10's tie rule survives, on a card tall enough to reach it under B. The old 480
        // DIP card now CLEARS (the margin yields 23 DIP to keep the full gap), so this probe
        // uses 560 DIP: drawn 576 against the 56 DIP floor's 521 DIP band, nothing clears, and
        // every candidate overlaps by the minimum 55. The anchor's own Left side (its window
        // contains the anchor) beats the Right one 6 DIP away; "ties go to the farthest" would
        // jump the note off its side.
        var keyboard = new Rect(251, 0, 1418, 575);
        var card = new Size(NoteLayout.CardWidthDip(28), 560);
        var placement = NoteLayout.Place(
            Phys(300, 700), NoteSide.Right, card, WorkArea, new[] { keyboard }, Scale,
            exclude: null, allowDisplaced: true, topGrowthDip: NoteLayout.TagProtrusionDip(28))!;
        var drawn = NoteLayout.WithTagClearance(placement, 28);

        Check.WithCustomMessage($"the card must stay on the anchor's side (got {placement.Side} at {drawn.Window})")
            .That(placement.Side).IsEqualTo(NoteSide.Left);
        Check.WithCustomMessage("the drawn window must cover the anchor's x (300)")
            .That(drawn.Window.Left <= 300 && drawn.Window.Right >= 300).IsTrue();
        Check.WithCustomMessage("and its overlap is the minimum the 56 DIP floor leaves")
            .That(SignedClearance(drawn.Window, keyboard)).IsCloseTo(-55, 1e-6);
    }

    // ── T6 fixup-11: the tail never runs through its own card ──

    [Fact]
    public void NoTailEverRunsThroughItsOwnCard()
    {
        // Review-10 (ii): a clamped tail must exist only when the anchor lies beyond the card's
        // tail edge, or it is drawn from that edge back THROUGH the card, pointing away from the
        // anchor. Swept over the reviewer's grid (Display, and Edit with no keyboard / the top
        // keyboard / the bottom keyboard), and where the OLD rule drew an honest tail the new
        // tail must be byte-identical.
        var sizes = new[] { 18.0, 28.0, 48.0 };
        var sides = new[] { NoteSide.Right, NoteSide.Left, NoteSide.Below, NoteSide.Above };
        var avoidSets = new[] { Array.Empty<Rect>(), new[] { KeyboardTop }, new[] { KeyboardBottom } };
        int tails = 0, keptDisplay = 0, keptEdit = 0, removedDisplay = 0, removedEdit = 0;
        int addedDisplay = 0, addedEdit = 0, movedDisplay = 0, movedEdit = 0;
        var failures = new List<string>();

        for (double x = 0; x <= 1920; x += 40)
        {
            for (double y = 0; y <= 1152; y += 20)
            {
                foreach (var size in sizes)
                {
                    // K2a: the Edit branch places the compact Edit card; the Display one keeps
                    // the four-row column card it always had.
                    var displayCard = new Size(
                        NoteLayout.CardWidthDip(size),
                        NoteLayout.CardHeightDip(size, 0, WorkArea.Height));
                    var editCard = NoteLayout.EditCardSize(size, 0, WorkArea.Height);
                    foreach (var preferred in sides)
                    for (int variant = 0; variant < avoidSets.Length + 1; variant++)
                    {
                        bool edit = variant > 0;
                        var avoid = variant == 0 ? Array.Empty<Rect>() : avoidSets[variant - 1];
                        var raw = NoteLayout.Place(Phys(x, y), preferred, edit ? editCard : displayCard, WorkArea, avoid, Scale,
                            exclude: null, allowDisplaced: edit,
                            topGrowthDip: edit ? NoteLayout.TagProtrusionDip(size) : 0.0)!;
                        var drawn = NoteLayout.WithTagClearance(raw, size);

                        var triangle = NoteLayout.TailTriangle(drawn);
                        var anchorPoint = new Point(x, y);
                        var oldCandidates = OldTailCandidates(drawn, anchorPoint);
                        bool oldHad = oldCandidates.Any(o => o is not null);
                        if (triangle is null)
                        {
                            // T6 fixup-12: a REMOVED tail counts too - counting only the drawn
                            // ones hid exactly what this fix does.
                            if (oldHad) { if (edit) removedEdit++; else removedDisplay++; }
                            continue;
                        }
                        tails++;
                        var tip = new Point(drawn.Window.X + triangle[0].X, drawn.Window.Y + triangle[0].Y);
                        if (!TipPointsOutOfTheCard(drawn, tip))
                        {
                            failures.Add($"({x}, {y}) {size} DIP {(edit ? "Edit" : "Display")} {preferred}: tip {tip} vs card {drawn.Card} side {drawn.Side}");
                            continue;
                        }

                        // Honest tail census against the pre-fix formulas: kept / added / moved.
                        if (drawn.TailTip is { } now && oldCandidates.Any(o => o is { } p && p == now))
                        {
                            if (edit) keptEdit++; else keptDisplay++;
                        }
                        else if (!oldHad)
                        {
                            if (edit) addedEdit++; else addedDisplay++;
                        }
                        else
                        {
                            if (edit) movedEdit++; else movedDisplay++;
                        }
                    }
                }
            }
        }

        Check.WithCustomMessage("the sweep must have drawn tails")
            .That(tails).IsStrictlyGreaterThan(0);
        Check.WithCustomMessage(
                $"{failures.Count} of {tails} tails do not point out of the card. "
                + $"Display: kept {keptDisplay}, removed {removedDisplay}, added {addedDisplay}, moved {movedDisplay}. "
                + $"Edit: kept {keptEdit}, removed {removedEdit}, added {addedEdit}, moved {movedEdit}.")
            .That(tails).IsStrictlyGreaterThan(0);
        Check.WithCustomMessage(
                $"{failures.Count} tails do not point out of the card:\n  " + string.Join("\n  ", failures.Take(10)))
            .That(failures).IsEmpty();
    }

    [Fact]
    public void TheBottomEdgeAnchor_At28_KeepsItsTailOutOfTheCard()
    {
        // The defect's own case at a REACHABLE height (review-11 item 2): card h340 at 28, the
        // real bottom-edge Edit note. Its Below and Above own-clamps are the SAME window, and
        // pass 2's identical-window tie-break now prefers the side whose tail can honestly point
        // at the anchor: Above, tail pointing down at the anchor.
        var keyboard = new Rect(251, 0, 1418, 575);
        var card = new Size(NoteLayout.CardWidthDip(28), 340);
        var raw = NoteLayout.Place(Phys(820, 1140), NoteSide.Right, card, WorkArea, new[] { keyboard }, Scale,
            exclude: null, allowDisplaced: true, topGrowthDip: NoteLayout.TagProtrusionDip(28))!;
        var drawn = NoteLayout.WithTagClearance(raw, 28);

        Check.WithCustomMessage($"the anchor's own side must win the identical-window tie (got {drawn.Side})")
            .That(drawn.Side).IsEqualTo(NoteSide.Above);
        Check.WithCustomMessage("the tail must exist").That(drawn.TailTip).IsNotNull();
        var tip = drawn.TailTip!.Value;
        Check.WithCustomMessage($"the tail must point DOWN at the anchor (tip {tip}, card {drawn.Card})")
            .That(tip.Y).IsGreaterOrEqualThan(drawn.Card.Bottom - 0.5);
        Check.WithCustomMessage("and sit on the anchor's own x")
            .That(tip.X).IsCloseTo(820, 1e-6);
    }

    /// <summary>Whether a tail tip leaves the card on its side's tail edge (screen coordinates).</summary>
    private static bool TipPointsOutOfTheCard(NotePlacement placement, Point tip)
    {
        var card = placement.Card;
        return placement.Side switch
        {
            NoteSide.Below => tip.Y <= card.Top + 0.5,
            NoteSide.Above => tip.Y >= card.Bottom - 0.5,
            NoteSide.Right => tip.X <= card.Left + 0.5,
            _ => tip.X >= card.Right - 0.5,
        };
    }

    /// <summary>
    /// The two tail tips the PRE-fixup-11 code could have produced for this placement: the
    /// clamped branch (the nearest window point toward the anchor, clamped along the card's
    /// edge) and the clean branch (the anchor offset by the 6 DIP tip gap). Either match means
    /// the tail did not move.
    /// </summary>
    private static Point?[] OldTailCandidates(NotePlacement placement, Point anchor)
    {
        var card = placement.Card;
        var window = placement.Window;
        double lo = 18 + 4;
        Point?[] result = [null, null];

        // The clamped rule: no tail only when the anchor is inside the card.
        if (!card.Contains(anchor))
        {
            double nx = Math.Clamp(anchor.X, window.Left + 6, Math.Max(window.Left + 6, window.Right - 6));
            double ny = Math.Clamp(anchor.Y, window.Top + 6, Math.Max(window.Top + 6, window.Bottom - 6));
            result[0] = placement.Side switch
            {
                NoteSide.Right or NoteSide.Left => new Point(
                    nx, Math.Clamp(ny, card.Top + lo, Math.Max(card.Top + lo, card.Bottom - lo))),
                _ => new Point(
                    Math.Clamp(nx, card.Left + lo, Math.Max(card.Left + lo, card.Right - lo)), ny),
            };
        }

        // The clean rule: the anchor offset by the tip gap, clamped along the edge.
        Point raw = placement.Side switch
        {
            NoteSide.Right => new Point(anchor.X + 6, anchor.Y),
            NoteSide.Left => new Point(anchor.X - 6, anchor.Y),
            NoteSide.Below => new Point(anchor.X, anchor.Y + 6),
            _ => new Point(anchor.X, anchor.Y - 6),
        };
        result[1] = placement.Side switch
        {
            NoteSide.Right or NoteSide.Left => new Point(
                raw.X, Math.Clamp(raw.Y, card.Top + lo, Math.Max(card.Top + lo, card.Bottom - lo))),
            _ => new Point(
                Math.Clamp(raw.X, card.Left + lo, Math.Max(card.Left + lo, card.Right - lo)), raw.Y),
        };
        return result;
    }

    // ── helpers ──

    /// <summary>The number tag's rect in SCREEN coordinates (NoteLayout returns it window-relative).</summary>
    private static Rect TagRect(NotePlacement placement, double textSizeDip)
    {
        var tag = NoteLayout.TagRect(placement, textSizeDip);
        return new Rect(placement.Window.X + tag.X, placement.Window.Y + tag.Y, tag.Width, tag.Height);
    }

    /// <summary>The EDIT card at a text size, at its row floor (no text measured): the compact
    /// card an editing note places (K2a).</summary>
    private static Size CardAt(double textSize) =>
        NoteLayout.EditCardSize(textSize, 0, WorkArea.Height);

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
