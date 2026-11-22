namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// Fixup-02 (drv4 review, 22.11): F1's shared final Display step (Place-here included), F2's chip
/// text inset, F3's arming pins for M20/M21/M22 and the honest F8 pin. Every test is headless: a
/// real <see cref="NoteWindow"/> is built, arranged and rendered, but never shown.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class NoteRevealFixup02Tests
{
    private const double Scale = 2.0;

    private static NoteManager NewManager(double textSize, NoteSettings? settings = null) =>
        new(new WeakReferenceMessenger(), hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider,
            noteSettings: settings ?? new NoteSettings { TextSizeDip = (int)textSize });

    private static void Sample(NoteManager manager, PhysicalPoint point, long t) =>
        manager.Receive(new GazeCursorMovedMessage(new GazeFrame(point, t, IsInGap: false)));

    private static long Hold(NoteManager manager, PhysicalPoint point, long start, long ms,
        int stepUs = 33_000)
    {
        long end = start + (ms * 1000);
        for (long t = start; t <= end; t += stepUs)
            Sample(manager, point, t);
        return end + stepUs;
    }

    private static string Saved(NoteManager manager, string text, double dipX, double dipY)
    {
        manager.CreateNoteAt(new PhysicalPoint(dipX * 2, dipY * 2));
        var id = manager.ActiveNotes[^1].Id;
        manager.Apply(new NoteAppend(text));
        manager.FinishEdit(id, commit: true);
        return id;
    }

    private static PhysicalPoint P(double dipX, double dipY) => new(
        DpiHelper.LogicalToPhysicalX(dipX), DpiHelper.LogicalToPhysicalY(dipY));

    private static PhysicalPoint Away => P(1800, 1000);

    // ── F1: the reviewer's R3_1 and R4_P1(place) ports — RED before the fix ──

    [StaTheory]
    [InlineData(18.0)]
    [InlineData(36.0)]
    public void F1_R3_1_AfterAMovePlace_TheWindowStillContainsTheRevealTarget(double size)
    {
        using var manager = NewManager(size);
        DpiHelper.Initialize(Scale, Scale);
        try
        {
            var id = Saved(manager, "hi", 800, 500);
            AssertHolds(manager, id, "after save");
            long t = NoteTestBench.Reveal(manager, id, 0);
            t = Hold(manager, NoteTestBench.CellCentre(manager, id, 1), t, 800);
            Check.That(manager.IsMoving).IsTrue();
            t = Hold(manager, P(400, 900), t, 1_200);
            var disc = manager.PlaceWindowForTest!;
            var discCentre = P(manager.PlaceWindowRectForTest.X + disc.DiscCentreDip.X,
                manager.PlaceWindowRectForTest.Y + disc.DiscCentreDip.Y);
            t = Hold(manager, discCentre, t, 800);
            Check.That(manager.IsMoving).IsFalse();
            Check.That(manager.IsRevealedForTest(id)).IsFalse();
            AssertHolds(manager, id, "after move-place");
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaTheory]
    [InlineData(18.0)]
    [InlineData(36.0)]
    public void F1_R4_P1_PlaceHere_KeepsTheRevealTargetInsideTheWindow(double size)
    {
        using var manager = NewManager(size);
        DpiHelper.Initialize(Scale, Scale);
        try
        {
            var id = Saved(manager, "hi", 800, 500);
            Check.That(Holds(manager, id)).IsEqualTo("ok");
            long t = NoteTestBench.Reveal(manager, id, 0);
            t = Hold(manager, Away, t, 300);
            t = Hold(manager, NoteTestBench.CellCentre(manager, id, 1), t, 800);
            Check.That(manager.IsMoving).IsTrue();
            t = Hold(manager, Away, t, 300);
            t = Hold(manager, P(400, 900), t, 1_200);
            var disc = manager.PlaceWindowForTest!;
            var discCentre = P(manager.PlaceWindowRectForTest.X + disc.DiscCentreDip.X,
                manager.PlaceWindowRectForTest.Y + disc.DiscCentreDip.Y);
            t = Hold(manager, discCentre, t, 800);
            Check.WithCustomMessage("place: the move ended").That(manager.IsMoving).IsFalse();
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.That(manager.IsRevealedForTest(id)).IsFalse();
            Check.WithCustomMessage($"{size}: place").That(Holds(manager, id)).IsEqualTo("ok");
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── F1: one walk over every path back to rest ──

    [StaTheory]
    [InlineData(18.0, "place")]
    [InlineData(36.0, "place")]
    [InlineData(18.0, "otherside")]
    [InlineData(36.0, "otherside")]
    [InlineData(18.0, "cancel")]
    [InlineData(36.0, "cancel")]
    [InlineData(18.0, "undo")]
    [InlineData(36.0, "undo")]
    [InlineData(18.0, "editdone")]
    [InlineData(36.0, "editdone")]
    [InlineData(18.0, "keyboard")]
    [InlineData(36.0, "keyboard")]
    [InlineData(18.0, "textsize")]
    [InlineData(36.0, "textsize")]
    public void F1_EveryPathBackToRest_KeepsTheRevealTargetInsideTheWindow(double size, string path)
    {
        var messenger = new WeakReferenceMessenger();
        var settings = new NoteSettings { TextSizeDip = (int)size };
        var probe = new NoteManagerTests.FakeKeyboardProbe { IsVisible = false };
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider, noteSettings: settings);
        if (path == "keyboard")
            manager.SetKeyboardProbe(probe);
        DpiHelper.Initialize(Scale, Scale);
        try
        {
            var id = Saved(manager, "hi", 800, 500);
            long t = NoteTestBench.Reveal(manager, id, 0);
            Check.That(manager.IsRevealedForTest(id)).IsTrue();

            switch (path)
            {
                case "place":
                    t = Hold(manager, NoteTestBench.CellCentre(manager, id, 1), t, 800);
                    Check.That(manager.IsMoving).IsTrue();
                    t = Hold(manager, P(400, 900), t, 1_200);
                    var disc = manager.PlaceWindowForTest!;
                    t = Hold(manager, P(
                        manager.PlaceWindowRectForTest.X + disc.DiscCentreDip.X,
                        manager.PlaceWindowRectForTest.Y + disc.DiscCentreDip.Y), t, 800);
                    Check.That(manager.IsMoving).IsFalse();
                    break;
                case "otherside":
                    t = Hold(manager, NoteTestBench.CellCentre(manager, id, 1), t, 800);
                    Check.That(manager.IsMoving).IsTrue();
                    t = Hold(manager, Away, t, 300);
                    t = Hold(manager, NoteTestBench.CellCentre(manager, id, 0), t, 900);
                    Check.That(manager.IsMoving).IsFalse();
                    break;
                case "cancel":
                    t = Hold(manager, NoteTestBench.CellCentre(manager, id, 1), t, 800);
                    Check.That(manager.IsMoving).IsTrue();
                    t = Hold(manager, Away, t, 300);
                    t = Hold(manager, NoteTestBench.CellCentre(manager, id, 1), t, 900);
                    Check.That(manager.IsMoving).IsFalse();
                    break;
                case "undo":
                    t = Hold(manager, NoteTestBench.CellCentre(manager, id, 3), t, 800);
                    Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
                    t = Hold(manager, Away, t, 300);
                    t = Hold(manager, manager.ChipUndoCentreForTest(id), t, 900);
                    Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
                    break;
                case "editdone":
                    t = Hold(manager, NoteTestBench.CellCentre(manager, id, 0), t, 800);
                    Check.That(manager.IsEditing).IsTrue();
                    manager.FinishEdit(id, commit: true);
                    Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
                    break;
                case "keyboard":
                    t = Hold(manager, NoteTestBench.CellCentre(manager, id, 0), t, 800);
                    Check.That(manager.IsEditing).IsTrue();
                    // The keyboard rises over the Edit card, then Done restores the Display note
                    // while it is still up; the later hide must not disturb the cleared window.
                    probe.RectsProvider = () => new[]
                    {
                        new PhysicalRect(
                            DpiHelper.LogicalToPhysicalX(600), DpiHelper.LogicalToPhysicalY(300),
                            DpiHelper.LogicalToPhysicalX(900), DpiHelper.LogicalToPhysicalY(600))
                    };
                    probe.IsVisible = true;
                    probe.RaiseChanged();
                    manager.FinishEdit(id, commit: true);
                    Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
                    AssertHolds(manager, id, "Done with the keyboard up");
                    probe.IsVisible = false;
                    probe.RaiseChanged();
                    break;
                case "textsize":
                    // Back to rest first (the reveal hides after 2 s away), then the T8 re-derive.
                    t = Hold(manager, Away, t, 2_500);
                    Check.That(manager.IsRevealedForTest(id)).IsFalse();
                    settings.TextSizeDip = 40;
                    break;
            }

            Check.WithCustomMessage($"{size}/{path}: the path must end at rest")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.WithCustomMessage($"{size}/{path}: the path must end unrevealed")
                .That(manager.IsRevealedForTest(id)).IsFalse();
            AssertHolds(manager, id, $"{size}/{path}");
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    private static string Holds(NoteManager manager, string id)
    {
        var target = manager.RevealTargetForTest(id);
        var window = manager.PlacementForTest(id)!.Window;
        var w = manager.WindowForTest(id)!;
        var real = new Rect(w.Left, w.Top, w.Width, w.Height);
        return target is null ? "no target"
            : (window.Contains(target.Value) && real.Contains(target.Value)) ? "ok"
            : $"window {window} / real {real} does not contain target {target}";
    }

    private static void AssertHolds(NoteManager manager, string id, string when)
    {
        Check.WithCustomMessage($"{when}: the note has a reveal target")
            .That(manager.RevealTargetForTest(id)).IsNotNull();
        Check.WithCustomMessage($"{when}: {Holds(manager, id)}").That(Holds(manager, id)).IsEqualTo("ok");
        var target = manager.RevealTargetForTest(id)!.Value;
        var fill = manager.WindowForTest(id)!.RevealFillForTest;
        Check.WithCustomMessage($"{when}: an at-rest Display note must paint the reveal target")
            .That(fill).IsNotNull();
        var placementWindow = manager.PlacementForTest(id)!.Window;
        double fillCanvasLeft = Canvas.GetLeft(fill!);
        double fillCanvasTop = Canvas.GetTop(fill!);
        Check.WithCustomMessage(
                $"{when}: the fill must start inside the snapped window "
                + $"(canvas {fillCanvasLeft:F2},{fillCanvasTop:F2}, target {target}, "
                + $"placement window {placementWindow})")
            .That(fillCanvasLeft >= -1e-6 && fillCanvasTop >= -1e-6).IsTrue();
    }

    // ── F2: every chip text keeps its 8 DIP right inset and its 8 DIP disc gap ──

    public static IEnumerable<object[]> ChipInsetCases()
    {
        foreach (double scale in new[] { 1.0, 2.0 })
            foreach (double size in new[] { 18.0, 28.0, 36.0, 48.0 })
                yield return [size, scale];
    }

    [StaTheory]
    [MemberData(nameof(ChipInsetCases))]
    public void F2_EveryChipText_KeepsItsRightInset_AndItsDiscGap(double size, double scale)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var warnings = new List<string>();
        var table = ShippedGerman.Load(warnings.Add);
        Check.WithCustomMessage("the shipped German table must load without warnings")
            .That(warnings).IsEmpty();
        var texts = new[]
        {
            ChipText(table, UiLanguage.English, removed: true),
            ChipText(table, UiLanguage.English, removed: false),
            ChipText(table, UiLanguage.German, removed: true),
            ChipText(table, UiLanguage.German, removed: false),
        };
        Check.WithCustomMessage("the German half must not silently fall back to English")
            .That(texts[2] != texts[0] && texts[3] != texts[1]).IsTrue();
        var column = NoteLayout.ChipTextWidthDip(size, texts);
        var cardSize = NoteLayout.ChipCardSize(size, texts);
        DpiHelper.Initialize(scale, scale);
        try
        {
            var window = NewWindow("chip-inset");
            var raw = NoteLayout.Place(
                new PhysicalPoint(600 * scale, 1000 * scale), NoteSide.Right, cardSize,
                NoteTestBench.WorkArea, Array.Empty<Rect>(), scale)!;
            var placement = NoteLayout.WithTagClearance(raw, size);
            window.ApplyPlacement(placement);
            window.SetState(NoteState.PendingUndo);
            var canvas = (Canvas)window.Content;
            var card = NoteLayout.RectLocal(placement);
            var discRight = NoteLayout.ChipCellCenter(placement, 0).X
                + LayoutConstants.NoteButtonRadius;
            double innerRight = card.Right - 2; // the chip state's 2 DIP border

            foreach (var (text, label) in new[]
            {
                (texts[0], "en/removed"), (texts[1], "en/discarded"),
                (texts[2], "de/removed"), (texts[3], "de/discarded"),
            })
            {
                window.SetChipText(text, size, column);
                canvas.Measure(new Size(window.Width, window.Height));
                canvas.Arrange(new Rect(0, 0, window.Width, window.Height));
                canvas.UpdateLayout();
                var block = window.ChipTextForTest!;
                var content = window.ContentBorderForTest!;
                double textLeft = Canvas.GetLeft(content) + block.Margin.Left;
                var formatted = new FormattedText(text, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
                    block.FontSize, Brushes.Black, 1.0) { MaxTextWidth = block.Width };
                double metricRight = textLeft + formatted.Width;

                Check.WithCustomMessage($"{size}@{scale} {label}: the text must draw in the measured column")
                    .That(formatted.Width).IsLessOrEqualThan(column + 1e-6);
                Check.WithCustomMessage(
                        $"{size}@{scale} {label}: ink right {metricRight:F2} must stay >= 8 DIP inside the "
                        + $"inner border {innerRight:F2} (card {card}, column {column:F1})")
                    .That(innerRight - metricRight).IsGreaterOrEqualThan(8 - 1e-6);
                Check.WithCustomMessage(
                        $"{size}@{scale} {label}: the text left {textLeft:F2} must stay >= 8 DIP right "
                        + $"of the disc {discRight:F2}")
                    .That(textLeft - discRight).IsGreaterOrEqualThan(8 - 1e-6);

                // A real headless render of the text block itself (transparent background, so the
                // alpha channel IS the ink). Nothing is clipped by the card, nothing spills right.
                var ink = RenderTextInk(block, scale);
                Check.WithCustomMessage($"{size}@{scale} {label}: the headless render must show text ink")
                    .That(ink).IsNotNull();
                double inkLeft = textLeft + ink!.Value.Left;
                double inkRight = textLeft + ink.Value.Right;
                Check.WithCustomMessage(
                        $"{size}@{scale} {label}: rendered ink right {inkRight:F2} must stay >= 8 DIP "
                        + $"inside the inner border {innerRight:F2}")
                    .That(innerRight - inkRight).IsGreaterOrEqualThan(8 - 1e-6);
                Check.WithCustomMessage(
                        $"{size}@{scale} {label}: rendered ink left {inkLeft:F2} must stay >= 8 DIP "
                        + $"right of the disc {discRight:F2}")
                    .That(inkLeft - discRight).IsGreaterOrEqualThan(8 - 1e-6);
            }
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    private static string ChipText(StringTable table, UiLanguage language, bool removed) =>
        NoteLayout.NoBreakBeforeDot(table.T(language,
            removed ? "overlays.note.chip.removed" : "overlays.note.chip.discarded"));

    private static NoteWindow NewWindow(string id)
    {
        var window = new NoteWindow(id);
        InvisibleTestWindow.Prepare(window);
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        return window;
    }

    /// <summary>The drawn text's ink extents in DIP relative to the block (its own render, so the
    /// transparent canvas's alpha channel IS the glyph coverage), or null when the render shows
    /// no ink at all. A real RenderTargetBitmap at <paramref name="scale"/>.</summary>
    private static (double Left, double Right)? RenderTextInk(TextBlock block, double scale)
    {
        int w = (int)Math.Ceiling(block.ActualWidth * scale);
        int h = (int)Math.Ceiling(block.ActualHeight * scale);
        if (w <= 0 || h <= 0) return null;
        var bitmap = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(block);
        int stride = w * 4;
        var pixels = new byte[stride * h];
        bitmap.CopyPixels(pixels, stride, 0);

        int minX = -1, maxX = -1;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (pixels[(y * stride) + (x * 4) + 3] == 0) continue; // transparent: no ink
            if (minX < 0 || x < minX) minX = x;
            if (x > maxX) maxX = x;
        }
        return minX < 0 ? null : (minX / scale, (maxX + 1) / scale);
    }

    // ── F3: the arming clock's own rules (M20, M21, M22) ──

    /// <summary>Reveals the note and leaves the gaze on the card, so the arming clock exists and
    /// is still disarmed. Returns the timestamp of the last on-card sample.</summary>
    private static long RevealAndStay(NoteManager manager, string id)
    {
        var rest = manager.DrawnCardForTest(id);
        var centre = P(rest.Left + (rest.Width / 2), rest.Top + (rest.Height / 2));
        long t = Hold(manager, Away, 0, 200);
        t = Hold(manager, centre, t, 600);
        Check.That(manager.IsRevealedForTest(id)).IsTrue();
        Check.WithCustomMessage("the freshly revealed row must start disarmed")
            .That(manager.RowArmedForTest(id)).IsFalse();
        return t;
    }

    [StaTheory]
    [InlineData(18.0)]
    [InlineData(36.0)]
    public void F3_M20_AReturnIntoTheCard_ResetsTheArmingClock(double size)
    {
        using var manager = NewManager(size);
        DpiHelper.Initialize(Scale, Scale);
        try
        {
            var id = Saved(manager, "hi", 600, 500);
            long t = RevealAndStay(manager, id);
            t = Hold(manager, Away, t, 100);
            Check.WithCustomMessage("100 ms outside must not arm yet").That(manager.RowArmedForTest(id)).IsFalse();
            t = Hold(manager, P(manager.DrawnCardForTest(id).Left + 20,
                manager.DrawnCardForTest(id).Top + 20), t, 100);
            Check.WithCustomMessage("a return into the card must reset the clock, not keep the 100 ms")
                .That(manager.RowArmedForTest(id)).IsFalse();
            t = Hold(manager, Away, t, 100);
            Check.WithCustomMessage(
                    "two 100 ms outside runs split by a return into the card must not arm (M20)")
                .That(manager.RowArmedForTest(id)).IsFalse();
            t = Hold(manager, Away, t, 200);
            Check.WithCustomMessage("150 ms continuously outside arms the row").That(manager.RowArmedForTest(id)).IsTrue();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaTheory]
    [InlineData(18.0)]
    [InlineData(36.0)]
    public void F3_M21_ASingleThreeHundredMsSampleGap_Outside_CreditsNothing(double size)
    {
        using var manager = NewManager(size);
        DpiHelper.Initialize(Scale, Scale);
        try
        {
            var id = Saved(manager, "hi", 600, 500);
            long t = RevealAndStay(manager, id);
            Sample(manager, Away, t + 33_000);
            Check.That(manager.RowArmedForTest(id)).IsFalse();
            // One 300 ms silence while outside: valid-sample time, not free arming time.
            Sample(manager, Away, t + 333_000);
            Check.WithCustomMessage("a 300 ms sample gap outside must credit nothing (M21)")
                .That(manager.RowArmedForTest(id)).IsFalse();
            // Normal cadence resumes: the clock still needs its own 150 ms.
            for (long s = t + 366_000; s <= t + 500_000; s += 33_000)
                Sample(manager, Away, s);
            Check.WithCustomMessage("after the gap the normal 150 ms still arms").That(manager.RowArmedForTest(id)).IsTrue();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaTheory]
    [InlineData(18.0)]
    [InlineData(36.0)]
    public void F3_M22_GatedOutsideSamples_DoNotArm(double size)
    {
        using var manager = NewManager(size);
        DpiHelper.Initialize(Scale, Scale);
        try
        {
            var id = Saved(manager, "hi", 600, 500);
            long t = RevealAndStay(manager, id);
            Sample(manager, Away, t + 33_000);
            manager.SetOccluderSources(noteNoticeBounds: () => new PhysicalRect(
                Away.X - 1000, Away.Y - 1000, 2000, 2000));
            // 300 ms of genuine occlusion while the gaze is outside: no information for arming.
            for (long s = t + 66_000; s <= t + 366_000; s += 33_000)
                Sample(manager, Away, s);
            Check.WithCustomMessage("gated outside samples must not arm the row (M22)")
                .That(manager.RowArmedForTest(id)).IsFalse();
            manager.SetOccluderSources(noteNoticeBounds: () => null);
            // The gate pause left no credit; the first valid sample re-anchors and 150 ms arm.
            for (long s = t + 399_000; s <= t + 600_000; s += 33_000)
                Sample(manager, Away, s);
            Check.WithCustomMessage("after the gate opens, real outside time arms").That(manager.RowArmedForTest(id)).IsTrue();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }
}
