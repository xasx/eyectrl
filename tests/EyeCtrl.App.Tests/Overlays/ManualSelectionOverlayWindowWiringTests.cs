namespace EyeCtrl.App.Tests.Overlays;

using System.Text.RegularExpressions;
using EyeCtrl.App.Tests.Localization;
using Xunit;

/// <summary>
/// G7 (fixup 01): the shown line was built and tested, but nothing proved <c>Show</c> emitted it —
/// deleting the <c>Log.Information</c> call stayed green. <c>Show</c> now delegates its pre-show work
/// (capture + the INFO line) to <c>PrepareForShow</c>; the emission test covers the method, and this
/// source pin covers the delegation, so neither half can be deleted silently.
/// </summary>
public class ManualSelectionOverlayWindowWiringTests
{
    private const string SourceFile = "src/EyeCtrl.App/Overlays/ManualSelectionOverlayWindow.xaml.cs";

    [Fact]
    public void Show_DelegatesItsPreShowWorkToPrepareForShow()
    {
        var text = LocalizationTestData.SourceFiles()
            .Single(file => file.Relative == SourceFile).Text;

        var show = Regex.Match(
            text,
            @"public\s+void\s+Show\s*\([^)]*\)\s*\r?\n\s*\{(?<body>.*?)\r?\n\s{4}\}",
            RegexOptions.Singleline);

        Check.WithCustomMessage($"ManualSelectionOverlayWindow.Show was not found in {SourceFile}; the pin is stale.")
            .That(show.Success).IsTrue();
        Check.WithCustomMessage(
                "Show no longer calls PrepareForShow, so the shown INFO line may not be emitted before the "
                + "first visible frame. Restore the delegation (G7).")
            .That(Regex.IsMatch(show.Groups["body"].Value,
                @"PrepareForShow\s*\(\s*center\s*,\s*radius\s*,\s*magnification\s*,\s*gazePoint\s*,\s*recapture\s*\)\s*;"))
            .IsTrue();
    }
}
