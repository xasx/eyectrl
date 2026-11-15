namespace EyeCtrl.Setup.Tests.Display;

using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Localization;
using EyeCtrl.Setup;
using EyeCtrl.Setup.Display;
using EyeCtrl.Sdk.Wpf;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// C9 S6e's five fit classes, measured in their production controls at the owner's 2.0:
/// <c>setup420x160</c> (every choice button, 32 DIP, wrapping), <c>setupProse</c> (every 40 DIP
/// prose line in its 1168 x 384 DIP box), <c>setupTitle</c> (the 44 DIP bold band/result title in
/// its 1094 x 468 DIP box) and <c>setupStage</c>/<c>setupDim</c> (36 DIP single rows, measured as
/// the label's full natural width in its 1168 − 30 − 24 DIP slot). English is the baseline; the
/// German draft from the file the App ships is measured too, with no exceptions.
/// </summary>
[Collection(SetupUiWindowsCollection.Name)]
public sealed class SetupFitTests
{
    [Fact]
    public void TheFiveSetupClasses_HaveTheirKeys_AndNoGermanIsMissing()
    {
        Check.That(FitCatalog.ItemsOf("setup420x160")).HasSize(5);
        Check.That(FitCatalog.ItemsOf("setupTitle")).HasSize(24);
        Check.That(FitCatalog.ItemsOf("setupStage")).HasSize(6);
        Check.That(FitCatalog.ItemsOf("setupDim")).HasSize(1);
        Check.That(FitCatalog.ItemsOf("setupProse")).HasSize(4);
        Check.That(GermanDraft.MissingKeys()).IsEmpty();
        Check.WithCustomMessage("The German draft logged load warnings:\n  " + string.Join("\n  ", GermanDraft.LoadWarnings))
            .That(GermanDraft.LoadWarnings).IsEmpty();
    }

    [Fact]
    public void EveryEnglishAndGermanChoiceButton_FitsItsBox()
    {
        RunSta(() =>
        {
            using var pipe = Dwell.UseTestPipeForTests("eyectrl_dwell_setup_test_" + Guid.NewGuid().ToString("N"));

            foreach (var item in FitCatalog.ItemsOf("setup420x160"))
            {
                var english = MeasureButton(item.Key, item.Text);
                Check.WithCustomMessage($"{item}: {english.Detail}")
                    .That(english.Fits).IsTrue();
            }

            foreach (var item in GermanDraft.ItemsOf("setup420x160"))
            {
                var german = MeasureButton(item.Key, item.Text);
                Check.WithCustomMessage($"{item} \"{item.Text}\": {german.Detail}")
                    .That(german.Fits).IsTrue();
            }
        });
    }

    [Fact]
    public void EveryEnglishAndGermanProseLine_FitsItsBox() => ForBothTables("setupProse", MeasureProse);

    [Fact]
    public void EveryEnglishAndGermanTitle_FitsItsBox() => ForBothTables("setupTitle", MeasureTitle);

    [Fact]
    public void EveryEnglishAndGermanStageRow_FitsItsBox() => ForBothTables("setupStage", MeasureStageRow);

    [Fact]
    public void EveryEnglishAndGermanDimHint_FitsItsBox() => ForBothTables("setupDim", MeasureDim);

    private void ForBothTables(string fitClass, Func<FitCatalog.Item, FitVerdict> measure)
    {
        RunSta(() =>
        {
            foreach (var item in FitCatalog.ItemsOf(fitClass))
                Check.WithCustomMessage($"{item}: {measure(item).Detail}")
                    .That(measure(item).Fits).IsTrue();

            foreach (var item in GermanDraft.ItemsOf(fitClass))
                Check.WithCustomMessage($"{item} \"{item.Text}\": {measure(item).Detail}")
                    .That(measure(item).Fits).IsTrue();
        });
    }

    [Fact]
    public void TheMetrics_PutTheChoiceRowOnTheOwnersWorkArea_WithoutACentreTarget()
    {
        var workArea = new Rect(0, 0, 1920, 1152);
        var choice = SetupWindowMetrics.ChoiceBounds(workArea);
        var progress = SetupWindowMetrics.ProgressBounds(workArea);

        Check.That(SetupWindowMetrics.ChoiceRowWidth).IsEqualTo(1500);
        Check.That(choice.Width).IsCloseTo(1564, 0.01);
        Check.That(choice.Height).IsCloseTo(806.4, 0.01);
        Check.That(choice.Left).IsCloseTo(178, 0.01);
        Check.That(choice.Top).IsCloseTo(172.8, 0.01);

        Check.That(progress.Width).IsCloseTo(1344, 0.01);
        Check.That(progress.Height).IsCloseTo(806.4, 0.01);
        Check.That(progress.Left).IsCloseTo(288, 0.01);

        Check.That(SetupWindowMetrics.ChoiceButtonWidth).IsEqualTo(420);
        Check.That(SetupWindowMetrics.ChoiceButtonHeight).IsEqualTo(160);
        Check.That(SetupWindowMetrics.ChoiceGap).IsEqualTo(120);
        Check.That(SetupWindowMetrics.ChoiceFontSize).IsEqualTo(32);
        Check.That(SetupWindowMetrics.ProseFontSize).IsEqualTo(40);
        Check.That(SetupWindowMetrics.TitleFontSize).IsEqualTo(44);
        Check.That(SetupWindowMetrics.StageFontSize).IsEqualTo(36);
        Check.That(SetupWindowMetrics.DimFontSize).IsEqualTo(36);
        Check.That(SetupWindowMetrics.BodyWidthProgress).IsEqualTo(1168);
        Check.That(SetupWindowMetrics.BodyWidthChoice).IsEqualTo(1388);
        Check.That(SetupWindowMetrics.TitleFitWidth).IsEqualTo(1094);
        Check.That(SetupWindowMetrics.TitleFitHeight).IsEqualTo(468);
    }

    private static FitVerdict MeasureButton(string key, string text)
    {
        var button = UninstallChoiceWindow.CreateChoiceButton(
            text, KindFor(key), "setup_fit_button", () => { });
        var panel = new StackPanel();
        panel.Children.Add(button);
        using var host = FitHost.Open(panel, family: Tokens.FontStandard);

        var label = (TextBlock)button.Content;
        var border = Tokens.Current.BorderThicknessStandard;
        var roomWidth = button.Width - button.Padding.Left - button.Padding.Right - (2 * border);
        var roomHeight = button.Height - button.Padding.Top - button.Padding.Bottom - (2 * border);
        var (run, runWidth) = FitText.WidestRun(label, text);
        var height = FitText.NaturalHeight(host, label, roomWidth);

        var ok = button.ActualWidth >= SetupWindowMetrics.ChoiceButtonWidth - 1e-6
                 && button.ActualHeight >= SetupWindowMetrics.ChoiceButtonHeight - 1e-6
                 && runWidth <= roomWidth
                 && height <= roomHeight;
        return new FitVerdict(ok,
            $"button {FitText.Dip(button.ActualWidth)}×{FitText.Dip(button.ActualHeight)}, widest run "
            + $"'{run}' {FitText.Dip(runWidth)} vs {FitText.Dip(roomWidth)}, wrapped height "
            + $"{FitText.Dip(height)} vs {FitText.Dip(roomHeight)} ({KindFor(key)})",
            FitText.ResolvedFace(label), Math.Min(roomWidth - runWidth, roomHeight - height));
    }

    /// <summary>The button face each classed key is drawn with (the primary/danger weights differ).</summary>
    private static ButtonKind KindFor(string key) => key switch
    {
        "uninstall.choice.keep" or "uninstall.confirmDelete.no" => ButtonKind.Primary,
        "uninstall.confirmDelete.yes" => ButtonKind.Danger,
        _ => ButtonKind.Normal,
    };

    private static FitVerdict MeasureProse(FitCatalog.Item item)
    {
        var text = Expand(item, item.Text);
        var block = UninstallChoiceWindow.CreateProse(text);
        var panel = new StackPanel { Width = SetupWindowMetrics.BodyWidthProgress };
        panel.Children.Add(block);
        using var host = FitHost.Open(panel, width: SetupWindowMetrics.BodyWidthProgress, family: Tokens.FontStandard);
        var (run, runWidth) = FitText.WidestRun(block, text);
        var height = FitText.NaturalHeight(host, block, SetupWindowMetrics.BodyWidthProgress);
        var ok = runWidth <= SetupWindowMetrics.BodyWidthProgress && height <= SetupWindowMetrics.ProseHeight;
        return new FitVerdict(ok,
            $"widest run '{run}' {FitText.Dip(runWidth)} vs {SetupWindowMetrics.BodyWidthProgress}, wrapped height "
            + $"{FitText.Dip(height)} vs {SetupWindowMetrics.ProseHeight}",
            FitText.ResolvedFace(block),
            Math.Min(SetupWindowMetrics.BodyWidthProgress - runWidth, SetupWindowMetrics.ProseHeight - height));
    }

    private static FitVerdict MeasureTitle(FitCatalog.Item item)
    {
        var text = Expand(item, item.Text);
        var block = UninstallChoiceWindow.CreateTitle(text);
        var panel = new StackPanel { Width = SetupWindowMetrics.TitleFitWidth };
        panel.Children.Add(block);
        using var host = FitHost.Open(panel, width: SetupWindowMetrics.TitleFitWidth, family: Tokens.FontStandard);
        var (run, runWidth) = FitText.WidestRun(block, text);
        var height = FitText.NaturalHeight(host, block, SetupWindowMetrics.TitleFitWidth);
        var ok = runWidth <= SetupWindowMetrics.TitleFitWidth && height <= SetupWindowMetrics.TitleFitHeight;
        return new FitVerdict(ok,
            $"widest run '{run}' {FitText.Dip(runWidth)} vs {SetupWindowMetrics.TitleFitWidth}, wrapped height "
            + $"{FitText.Dip(height)} vs {SetupWindowMetrics.TitleFitHeight}",
            FitText.ResolvedFace(block),
            Math.Min(SetupWindowMetrics.TitleFitWidth - runWidth, SetupWindowMetrics.TitleFitHeight - height));
    }

    private static FitVerdict MeasureStageRow(FitCatalog.Item item)
    {
        var block = UninstallChoiceWindow.CreateStageLabel(item.Text);
        return MeasureRow(block, item.Text);
    }

    private static FitVerdict MeasureDim(FitCatalog.Item item)
    {
        var block = UninstallChoiceWindow.CreateProse(
            item.Text, dim: true, size: SetupWindowMetrics.DimFontSize);
        return MeasureRow(block, item.Text);
    }

    private static FitVerdict MeasureRow(TextBlock block, string text)
    {
        // r3 F15: the label sits at BodyWidthProgress − glyph 30 − gap 24 and clips as a whole
        // line — the bound is its full natural width there, not the widest unbreakable word.
        var slot = SetupWindowMetrics.BodyWidthProgress - 30 - 24;
        var panel = new StackPanel { Width = SetupWindowMetrics.BodyWidthProgress };
        panel.Children.Add(block);
        using var host = FitHost.Open(panel, width: SetupWindowMetrics.BodyWidthProgress, family: Tokens.FontStandard);
        var width = FitText.NaturalWidth(block, text);
        var ok = width <= slot;
        return new FitVerdict(ok,
            $"natural width {FitText.Dip(width)} vs the label slot {FitText.Dip(slot)}",
            FitText.ResolvedFace(block), slot - width);
    }

    [Fact]
    public void AnOverLongText_FailsEveryClass()
    {
        RunSta(() =>
        {
            using var pipe = Dwell.UseTestPipeForTests("eyectrl_dwell_setup_test_" + Guid.NewGuid().ToString("N"));

            var singleWord = new string('W', 300);                                 // width path
            var manyWords = string.Join(" ", Enumerable.Repeat("Wwww", 2000));    // wrapped-height path

            Check.That(MeasureButton("fit.probe", singleWord).Fits).IsFalse();
            // r6 minor: the base suite also probed the BUTTON with the wrapped-height path —
            // restored so a wrapping regression in the 420×160 room maths cannot slip through.
            Check.That(MeasureButton("fit.probe", manyWords).Fits).IsFalse();
            Check.That(MeasureProse(ProbeItem("setupProse", singleWord)).Fits).IsFalse();
            Check.That(MeasureProse(ProbeItem("setupProse", manyWords)).Fits).IsFalse();
            Check.That(MeasureTitle(ProbeItem("setupTitle", singleWord)).Fits).IsFalse();
            Check.That(MeasureTitle(ProbeItem("setupTitle", manyWords)).Fits).IsFalse();
            Check.That(MeasureStageRow(ProbeItem("setupStage", singleWord)).Fits).IsFalse();
            Check.That(MeasureDim(ProbeItem("setupDim", singleWord)).Fits).IsFalse();
        });
    }

    private static FitCatalog.Item ProbeItem(string fitClass, string text) =>
        new(fitClass + ".probe", "", text, Array.Empty<string>());

    private static string Expand(FitCatalog.Item item, string text)
    {
        if (item.Args.Count == 0)
            return text;

        var values = item.Args
            .Select(arg => arg == "folder" ? @"%APPDATA%\EyeCtrl" : "1.0.0")
            .Cast<object?>()
            .ToArray();
        return string.Format(CultureInfo.InvariantCulture, text, values);
    }

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
