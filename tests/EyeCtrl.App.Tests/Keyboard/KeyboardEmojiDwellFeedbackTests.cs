namespace EyeCtrl.App.Tests.Keyboard;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// The emoji grid — cells, skin-tone swatches and the special buttons — is a dwell surface like
/// any other, so it must show the same accent outline while it accumulates. It shipped with none:
/// its registrations passed <c>null</c> as the content and attached no overlay, so the grid looked
/// dead while it filled — indistinguishable, for a gaze-only user, from a broken one.
///
/// <para>
/// This is the defect probe, run through the real pipeline: the emoji mode is entered by dwelling
/// the shipped layout's <c>switchEmoji</c> key (not by calling a private method), the grid is
/// built by <c>EmojiGridManager.Enter</c>, and gaze on a cell drives
/// <c>KeyboardViewModel.ProcessKeyGaze</c> → the window's <c>RegisterKeyVisual</c> funnel →
/// <c>DwellOutlineOverlay.SetProgress</c>. It fails on all three ways the fix can regress:
/// the outline dropped from the registrar, the cell's <c>Child</c> set before the outline wraps
/// it (Attach throws on an already-parented element), or the content argument reverted to null
/// (an empty outline: the glyph vanishes silently while dwell still accumulates).
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class KeyboardEmojiDwellFeedbackTests
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int KeyDwellMs = 450;
    private const int PredictionDwellMs = 500;
    private const int CooldownMs = 400;
    private const string CellId = "emoji_0";

    private sealed record Probe(
        KeyboardWindow Window, KeyboardViewModel Vm, FrameworkElement Content, HwndSource Source)
        : IDisposable
    {
        public void Dispose() { Source.Dispose(); Window.Close(); }
    }

    private static string EmojiJsonPath() =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Emojis", "emojis.json");

    private static KeyboardLayout ShippedQwerty() =>
        KeyboardLayoutLoader.Load(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Resources", "Layouts", "qwerty.json"));

    private static Dictionary<string, PhysicalPoint> KeyCentres(KeyboardWindow w) =>
        (Dictionary<string, PhysicalPoint>)typeof(KeyboardWindow)
            .GetField("_keyScreenPositions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    private static Dictionary<string, (Border Container, TextBlock? Label)> KeyVisuals(KeyboardWindow w) =>
        (Dictionary<string, (Border Container, TextBlock? Label)>)typeof(KeyboardWindow)
            .GetField("_keyVisuals", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    private static Dictionary<string, DwellOutlineOverlay> KeyOutlines(KeyboardWindow w) =>
        (Dictionary<string, DwellOutlineOverlay>)typeof(KeyboardWindow)
            .GetField("_keyOutlines", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    /// <summary>
    /// A laid-out keyboard hosted in an HwndSource that is never made visible (the T1 probe path),
    /// with the shipped emoji data wired so the emoji mode can build a grid.
    /// </summary>
    private static Probe Open()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var emojiPath = EmojiJsonPath();
        Check.WithCustomMessage(
                $"The shipped emoji data is not at {emojiPath}, so the grid would never be built "
                + "and every assertion below would be vacuous.")
            .That(File.Exists(emojiPath)).IsTrue();

        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        var vm = new KeyboardViewModel(settings, messenger);

        var w = new KeyboardWindow();
        w.SetViewModel(vm);
        w.LoadLayout(ShippedQwerty(), 1.0,
            keyDwellMs: KeyDwellMs, predictionDwellMs: PredictionDwellMs, cooldownMs: CooldownMs);
        w.SetEmojiData(emojiPath,
            Path.Combine(Path.GetTempPath(), "eyectrl_emoji_test_" + Guid.NewGuid().ToString("N") + ".json"));

        var content = (FrameworkElement)w.Content;
        w.Content = null;

        var source = new HwndSource(new HwndSourceParameters("kb-emoji-probe")
        {
            WindowStyle = WsPopup,
            PositionX = -30000,
            PositionY = -30000,
            Width = 1,
            Height = 1,
        });

        var factor = 1.0 / source.CompositionTarget!.TransformToDevice.M11;
        content.LayoutTransform = new ScaleTransform(factor, factor);
        source.RootVisual = content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();

        Check.WithCustomMessage(
                "No PresentationSource: UpdateGaze's gate would catch PointToScreen's throw and "
                + "return false, and every assertion would pass VACUOUSLY. Fix the environment or "
                + "the fixture, never delete this guard.")
            .That(PresentationSource.FromVisual(content)).IsNotNull();

        return new Probe(w, vm, content, source);
    }

    private static void Feed(Probe probe, PhysicalPoint at, long fromUs, long toUs, long stepUs = 30_000)
    {
        Check.WithCustomMessage(
                $"Feed({fromUs}..{toUs}) step {stepUs}: the endpoint is not reachable on the step "
                + "grid, so the final sample would be dropped and a completion at the endpoint "
                + "would silently never happen. Round the endpoint to the cadence.")
            .That((toUs - fromUs) % stepUs).IsEqualTo(0);

        for (var t = fromUs; t <= toUs; t += stepUs)
            probe.Window.UpdateGaze(at, t);
    }

    /// <summary>
    /// Enters emoji mode through the real activation path — dwelling the shipped layout's
    /// <c>switchEmoji</c> key — and refreshes the grid's positions. Ticket 1's fixture trap
    /// applies to the caller's next read: after the layout rebuild no pass has run, so every new
    /// cell centre collapses to one unarranged point until <c>UpdateLayout</c>.
    /// </summary>
    private static void EnterEmojiMode(Probe probe)
    {
        probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);

        Check.WithCustomMessage(
                "The shipped qwerty layout has no live switchEmoji key centre, so emoji mode "
                + "could not be entered and everything below would be vacuous.")
            .That(KeyCentres(probe.Window).ContainsKey("switchEmoji")).IsTrue();
        Feed(probe, KeyCentres(probe.Window)["switchEmoji"], 30_000, 480_000);
        Check.WithCustomMessage("Dwelling switchEmoji did not enter emoji mode.")
            .That(probe.Vm.IsEmojiMode).IsTrue();

        probe.Content.UpdateLayout();
        probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 510_000);
    }

    [StaFact]
    public void EmojiCells_ShowDwellProgress_AndKeepTheirGlyphInTheTree()
    {
        var probe = Open();
        try
        {
            EnterEmojiMode(probe);

            var visuals = KeyVisuals(probe.Window);
            var outlines = KeyOutlines(probe.Window);
            Check.WithCustomMessage($"The emoji grid registered no '{CellId}' dwell target.")
                .That(visuals.ContainsKey(CellId)).IsTrue();
            Check.WithCustomMessage(
                    "The emoji cell is registered as a dwell target but has no outline, so it "
                    + "accumulates dwell in complete silence — the defect this ticket closes.")
                .That(outlines.ContainsKey(CellId)).IsTrue();

            var container = visuals[CellId].Container;
            var outline = outlines[CellId];
            Check.WithCustomMessage("The cell's child is not the dwell outline's layout.")
                .That(container.Child).IsSameReferenceAs(outline.Layout);

            var layout = (Grid)container.Child;
            Check.WithCustomMessage(
                    "The outline wrapped no glyph: the cell is blank while it still accumulates "
                    + "dwell. An outline-only assertion stays green through this.")
                .That(layout.Children[0]).IsInstanceOf<Emoji.Wpf.TextBlock>();
            var glyph = (Emoji.Wpf.TextBlock)layout.Children[0];
            Check.WithCustomMessage(
                    "The glyph element carries no emoji inlines, so the cell renders blank.")
                .That(glyph.EmojiInlines.Any()).IsTrue();

            // Arm the progress leg: a cell with no screen centre would never accumulate and the
            // assertion below would read a permanent zero as success.
            Check.WithCustomMessage(
                    "The emoji cell has no screen centre, so gaze on it would never accumulate "
                    + "and the progress assertion would be vacuous.")
                .That(KeyCentres(probe.Window).ContainsKey(CellId)).IsTrue();

            var fillAtRest = ((SolidColorBrush)container.Background).Color;
            // Binds the live cell to the token the Controls perceptibility gate measures: without
            // this, changing EmojiGridBackgroundColor would leave that gate passing while it
            // measures a surface the grid no longer uses.
            Check.WithCustomMessage(
                    $"The cell's fill is {fillAtRest}, not Tokens.SurfaceMedium — the outline's "
                    + "perceptibility against the emoji cells is then gated on the wrong surface.")
                .That(fillAtRest).IsEqualTo(Tokens.SurfaceMedium);
            Feed(probe, KeyCentres(probe.Window)[CellId], 1_000_000, 1_180_000);

            Check.WithCustomMessage(
                    $"The cell's outline is {outline.Outline.StrokeThickness} DIP after 180 ms of "
                    + "a 450 ms dwell — no progress feedback reaches the emoji surface.")
                .That(outline.Outline.StrokeThickness).IsStrictlyGreaterThan(0.0);
            Check.WithCustomMessage("The outline exceeded its full-dwell thickness mid-dwell.")
                .That(outline.Outline.StrokeThickness)
                .IsLessOrEqualThan(DwellOutlineOverlay.MaxThickness);
            Check.WithCustomMessage("The halo must sit just outside the accent, as on the keys.")
                .That(outline.Halo.StrokeThickness).IsStrictlyGreaterThan(outline.Outline.StrokeThickness);

            // Glyph contrast at full dwell equals its contrast at rest, structurally: the outline
            // leaves the fill and the content untouched. Asserted at full thickness explicitly.
            outline.SetProgress(1.0);
            Check.That(outline.Outline.StrokeThickness).IsEqualTo(DwellOutlineOverlay.MaxThickness);
            Check.WithCustomMessage(
                    "The dwell changed the cell's fill, so the glyph's contrast at the end of the "
                    + "dwell would differ from its contrast at rest.")
                .That(((SolidColorBrush)container.Background).Color).IsEqualTo(fillAtRest);
            Check.WithCustomMessage("The dwell rewrote the glyph, or removed it from the cell.")
                .That(KeyVisuals(probe.Window)[CellId].Container.Child)
                .IsSameReferenceAs(outline.Layout);
            Check.That(((Grid)container.Child).Children[0]).IsSameReferenceAs(glyph);
        }
        finally { probe.Dispose(); }
    }

    /// <summary>
    /// The activation path is the one a mid-dwell probe does not reach: completing the dwell
    /// calls <c>ActivateKey</c>, which raises <c>ActiveModifiers</c> unconditionally, and
    /// <c>UpdateShiftDisplay</c> then writes every cell's label. That write binds to the BASE
    /// <c>TextBlock.Text</c> property (the label slot is base-typed), which replaces the
    /// Emoji.Wpf <c>EmojiInline</c> with a plain <c>Run</c> — the whole visible grid loses its
    /// colour after the first emoji. The damage does not recover: re-running the derived setter
    /// with the same value suppresses its callback (the derived DP never changed), and nothing
    /// rebuilds the grid after an activation — <c>OnKeyDwellCompleted</c> takes the
    /// no-<c>SpecialAction</c> branch and never calls <c>BuildGrid</c> — so the cells stay
    /// monochrome until a category or page change. This test dwells to COMPLETION: a test that
    /// stops at 90 % of the path is how that shipped green.
    ///
    /// <para>
    /// Every registered cell is asserted, not one: the claim is that the whole visible grid
    /// loses its colour, so a write that spared the cell the test happened to look at must fail
    /// here. Each cell's glyph identity is pinned too — surviving is the requirement, and
    /// surviving as the SAME glyph is what proves the cell was neither rebuilt nor swapped.
    /// </para>
    /// </summary>
    [StaFact]
    public void EmojiCell_Activation_KeepsItsColorGlyph()
    {
        var probe = Open();
        try
        {
            EnterEmojiMode(probe);

            var visuals = KeyVisuals(probe.Window);
            var outlines = KeyOutlines(probe.Window);
            Check.That(visuals.ContainsKey(CellId)).IsTrue();
            Check.That(KeyCentres(probe.Window).ContainsKey(CellId)).IsTrue();

            var cells = visuals.Keys
                .Where(id => id.StartsWith("emoji_", StringComparison.Ordinal))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
            Check.WithCustomMessage(
                    "No emoji cell is registered for the current page, so the post-activation "
                    + "assertions below would pass vacuously.")
                .That(cells).Not.IsEmpty();

            var glyphs = new Dictionary<string, Emoji.Wpf.TextBlock>();
            foreach (var id in cells)
            {
                var cellContainer = visuals[id].Container;
                Check.WithCustomMessage($"{id}: the cell's child is not its dwell outline layout.")
                    .That(cellContainer.Child).IsSameReferenceAs(outlines[id].Layout);
                Check.WithCustomMessage($"{id}: the outline did not wrap the cell's glyph.")
                    .That(((Grid)cellContainer.Child).Children[0]).IsInstanceOf<Emoji.Wpf.TextBlock>();
                glyphs[id] = (Emoji.Wpf.TextBlock)((Grid)cellContainer.Child).Children[0];
                Check.WithCustomMessage($"{id}: the glyph carries no emoji inlines at rest.")
                    .That(glyphs[id].EmojiInlines.Any()).IsTrue();
            }

            var activations = 0;
            probe.Vm.KeyDwellCompleted += id => { if (id == CellId) activations++; };

            // Enters at 1_000_000, completes at 1_450_000; the endpoint must stay on the step grid.
            Feed(probe, KeyCentres(probe.Window)[CellId], 1_000_000, 1_480_000);

            Check.WithCustomMessage(
                    "The cell never activated, so the post-activation assertions would be vacuous "
                    + "(the dwell would have to reach the full key-dwell time to raise "
                    + "ActiveModifiers through ActivateKey).")
                .That(activations).IsEqualTo(1);
            foreach (var id in cells)
            {
                Check.WithCustomMessage(
                        $"Activating {CellId} destroyed {id}'s colour rendering: the base-typed "
                        + "TextBlock.Text write in UpdateShiftDisplay replaced the EmojiInline "
                        + "with a plain Run. Every visible cell goes monochrome after the first "
                        + "emoji.")
                    .That(glyphs[id].EmojiInlines.Any()).IsTrue();
                Check.WithCustomMessage(
                        $"{id}: the cell no longer holds the glyph it did before the dwell, so its "
                        + "content was rebuilt or swapped rather than left alone.")
                    .That(((Grid)visuals[id].Container.Child).Children[0])
                    .IsSameReferenceAs(glyphs[id]);
            }
        }
        finally { probe.Dispose(); }
    }
}
