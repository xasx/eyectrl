namespace EyeCtrl.App.Tests.Overlays;

using System;
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
/// input channel is an eye tracker. What it can prove is that the window resolves icons through
/// ActionIcons.For and does not reintroduce a glyph string, which is the regression that would
/// otherwise be invisible until someone looked at the ring.
/// </summary>
public class ActionMenuIconRenderTests
{
    private const string WindowPath = "src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs";

    /// <summary>
    /// The window's source with comments already stripped. <c>SourceScanner.ScannedFiles</c>
    /// strips them itself, so nothing here needs to call <c>StripComments</c> a second time; a
    /// comment mentioning a glyph must not fail a guard about live code.
    /// </summary>
    /// <returns>The comment-stripped source of <see cref="WindowPath"/>.</returns>
    private static string ReadSource() =>
        SourceScanner.ScannedFiles().Single(f => f.Relative == WindowPath).Text;

    [Fact]
    public void TheWindow_ResolvesIconsThroughTheRegistry()
    {
        var source = ReadSource();

        Check.WithCustomMessage($"{WindowPath} no longer calls ActionIcons.For")
             .That(source).Contains("ActionIcons.For(");
    }

    [Fact]
    public void TheWindow_PassesNoGlyphString()
    {
        var source = ReadSource();

        Check.WithCustomMessage(
                $"{WindowPath} passes a symbol to DwellArcButton.Create. The action menu renders "
                + "drawn icons; a glyph here would render on top of one.")
            .That(source).Not.Contains("itemVm.Symbol");
    }

    [Fact]
    public void EveryIconTheRingCanAskFor_Resolves()
    {
        // Belt and braces against a registry entry being deleted: the ring can request an icon for
        // any InteractionAction plus Back.
        foreach (var icon in Enum.GetValues<ActionIcon>())
            Check.WithCustomMessage($"{icon} does not resolve").That(ActionIcons.For(icon)).IsNotNull();
    }
}
