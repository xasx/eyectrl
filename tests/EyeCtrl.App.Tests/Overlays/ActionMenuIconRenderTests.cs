namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Core.Models;
using NFluent;
using Xunit;

/// <summary>
/// The ring's icons come from the registry, not from a second mapping inside the window.
///
/// This is a source-level check rather than a rendering one: constructing ActionMenuWindow needs
/// a WPF Application and a screen capture, and this suite may not do that on a machine whose only
/// input channel is an eye tracker. What it can prove is that the painting path resolves icons
/// through ActionIcons.For and does not reintroduce a glyph string, which is the regression that
/// would otherwise be invisible until someone looked at the ring.
///
/// <para>
/// Since the ring painter was extracted, the registry call lives in
/// <see cref="ActionMenuRingPainter"/>, not in the window. The window keeps the second half of
/// the original guard: it passes no glyph, and — G6's window-file rule — it contains no direct
/// <c>DwellArcButton.Create</c>; the painter is the only file that passes the ring radius into
/// Create.
/// </para>
/// </summary>
public class ActionMenuIconRenderTests
{
    private const string WindowPath = "src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs";
    private const string PainterPath = "src/EyeCtrl.App/Overlays/ActionMenuRingPainter.cs";
    private const string SettingsPath = "src/EyeCtrl.App/Views/SettingsWindow.xaml.cs";

    /// <summary>
    /// A scanned file's source with comments already stripped. <c>SourceScanner.ScannedFiles</c>
    /// strips them itself, so nothing here needs to call <c>StripComments</c> a second time; a
    /// comment mentioning a glyph must not fail a guard about live code.
    /// </summary>
    /// <param name="relativePath">Repo-relative path of the file to read.</param>
    /// <returns>The comment-stripped source.</returns>
    private static string ReadSource(string relativePath) =>
        SourceScanner.ScannedFiles().Single(f => f.Relative == relativePath).Text;

    [Fact]
    public void ThePainter_ResolvesIconsThroughTheRegistry()
    {
        var source = ReadSource(PainterPath);

        Check.WithCustomMessage($"{PainterPath} no longer calls ActionIcons.For")
             .That(source).Contains("ActionIcons.For(");
    }

    [Fact]
    public void TheWindow_PassesNoGlyphString()
    {
        var source = ReadSource(WindowPath);

        Check.WithCustomMessage(
                $"{WindowPath} passes a symbol to DwellArcButton.Create. The action menu renders "
                + "drawn icons; a glyph here would render on top of one.")
            .That(source).Not.Contains("itemVm.Symbol");
    }

    /// <summary>
    /// G6's window-file rule: the window paints through the shared painter and never constructs a
    /// button itself. A direct Create here would be a second drawing path that can drift inside
    /// the argument list.
    /// </summary>
    [Fact]
    public void TheWindow_DoesNotCreateButtonsDirectly()
    {
        var source = ReadSource(WindowPath);

        Check.WithCustomMessage(
                $"{WindowPath} contains a direct DwellArcButton.Create call. Extract the drawing "
                + "into ActionMenuRingPainter instead — the painter is the single place the ring "
                + "radius enters Create.")
             .That(source).Not.Contains("DwellArcButton.Create(");
        Check.WithCustomMessage($"{WindowPath} no longer paints through ActionMenuRingPainter")
             .That(source).Contains("ActionMenuRingPainter.Paint(");
    }

    [Fact]
    public void EveryIconTheRingCanAskFor_Resolves()
    {
        // Belt and braces against a registry entry being deleted: the ring can request an icon for
        // any InteractionAction plus Back.
        foreach (var icon in Enum.GetValues<ActionIcon>())
            Check.WithCustomMessage($"{icon} does not resolve").That(ActionIcons.For(icon)).IsNotNull();
    }

    /// <summary>
    /// G6's caller rule: the shared painter has exactly TWO callers in production — the runtime
    /// window and the settings miniature. A third would be a second painting path, which is the
    /// drift the painter exists to prevent; a fourth file referencing the painter's name in live
    /// code fails here. Comments are stripped, so prose mentions do not count.
    /// </summary>
    [Fact]
    public void ThePainter_IsCalledOnlyByTheRuntimeWindowAndTheMiniature()
    {
        var callers = SourceScanner.ScannedFiles()
            .Where(f => f.Text.Contains("ActionMenuRingPainter.Paint(", StringComparison.Ordinal))
            .Select(f => f.Relative)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Check.WithCustomMessage(
                "ActionMenuRingPainter.Paint callers changed. The ring has exactly two painting "
                + "callers — ActionMenuWindow (runtime) and SettingsWindow (the preview) — and a "
                + "third is a second drawing path that can drift inside the argument list. "
                + "Found: " + string.Join(", ", callers))
             .That(callers).ContainsExactly(
                 "src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs",
                 "src/EyeCtrl.App/Views/SettingsWindow.xaml.cs");
    }

    /// <summary>
    /// The miniature's half of G6: it paints through the shared painter and never constructs a
    /// button itself, and it positions slots in its own DIP space — never through the runtime
    /// window's absolute-physical <c>ButtonCanvasCenter</c> seam.
    /// </summary>
    [Fact]
    public void TheMiniature_PaintsThroughThePainter_NotThroughCreateOrTheWindowsSeam()
    {
        var source = ReadSource(SettingsPath);

        Check.WithCustomMessage(
                $"{SettingsPath} contains a direct DwellArcButton.Create call. The miniature must "
                + "paint through ActionMenuRingPainter — the painter is the single place the ring "
                + "radius enters Create.")
             .That(source).Not.Contains("DwellArcButton.Create(");
        Check.WithCustomMessage(
                $"{SettingsPath} no longer paints through ActionMenuRingPainter")
             .That(source).Contains("ActionMenuRingPainter.Paint(");
        Check.WithCustomMessage(
                $"{SettingsPath} calls ActionMenuGeometry.ButtonCanvasCenter. That conversion maps "
                + "ABSOLUTE PHYSICAL screen points to the runtime window's canvas; the miniature "
                + "lays out in DIPs and must not share it (G3).")
             .That(source).Not.Contains("ButtonCanvasCenter");
    }
}
