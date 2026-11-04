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
/// C9 S6e's two fit classes, measured in their production controls at the owner's 2.0:
/// <c>setup420x160</c> (every choice button, 28 DIP, wrapping) and <c>setupProse</c> (every 36 DIP
/// prose line in its 1280 x 384 DIP box). English is the baseline; the German draft from the file
/// the App ships is measured too, with no exceptions.
/// </summary>
[Collection(SetupUiWindowsCollection.Name)]
public sealed class SetupFitTests
{
    [Fact]
    public void TheTwoSetupClasses_HaveTheirKeys_AndNoGermanIsMissing()
    {
        Check.That(FitCatalog.ItemsOf("setup420x160")).HasSize(5);
        Check.That(FitCatalog.ItemsOf("setupProse")).HasSize(33);
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
                var english = MeasureButton(item.Text);
                Check.WithCustomMessage($"{item}: {english}").That(english.Fits).IsTrue();
            }

            foreach (var item in GermanDraft.ItemsOf("setup420x160"))
            {
                var german = MeasureButton(item.Text);
                Check.WithCustomMessage($"{item} \"{item.Text}\": {german}").That(german.Fits).IsTrue();
            }
        });
    }

    [Fact]
    public void EveryEnglishAndGermanProseLine_FitsItsBox()
    {
        RunSta(() =>
        {
            foreach (var item in FitCatalog.ItemsOf("setupProse"))
            {
                var text = Expand(item, item.Text);
                var english = MeasureProse(text);
                Check.WithCustomMessage($"{item} \"{text}\": {english}").That(english.Fits).IsTrue();
            }

            foreach (var item in GermanDraft.ItemsOf("setupProse"))
            {
                var text = Expand(item, item.Text);
                var german = MeasureProse(text);
                Check.WithCustomMessage($"{item} \"{text}\": {german}").That(german.Fits).IsTrue();
            }
        });
    }

    [Fact]
    public void AnOverLongText_FailsBothClasses()
    {
        RunSta(() =>
        {
            using var pipe = Dwell.UseTestPipeForTests("eyectrl_dwell_setup_test_" + Guid.NewGuid().ToString("N"));

            foreach (var text in new[]
                     {
                         new string('W', 300),
                         string.Join(" ", Enumerable.Repeat("Wwww", 2000)),
                     })
            {
                Check.WithCustomMessage($"setup420x160 passed an over-long text: {MeasureButton(text)}")
                    .That(MeasureButton(text).Fits).IsFalse();
                Check.WithCustomMessage($"setupProse passed an over-long text: {MeasureProse(text)}")
                    .That(MeasureProse(text).Fits).IsFalse();
            }
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
        Check.That(SetupWindowMetrics.ChoiceFontSize).IsGreaterOrEqualThan(28);
        Check.That(SetupWindowMetrics.ProseFontSize).IsGreaterOrEqualThan(36);
    }

    private static FitVerdict MeasureButton(string text)
    {
        var button = UninstallChoiceWindow.CreateChoiceButton(
            text, ButtonKind.Normal, "setup_fit_button", () => { });
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
            + $"{FitText.Dip(height)} vs {FitText.Dip(roomHeight)}",
            FitText.ResolvedFace(label), Math.Min(roomWidth - runWidth, roomHeight - height));
    }

    private static FitVerdict MeasureProse(string text)
    {
        var block = UninstallChoiceWindow.CreateProse(text);
        var panel = new StackPanel { Width = SetupWindowMetrics.ProseWidth };
        panel.Children.Add(block);
        using var host = FitHost.Open(
            panel, width: SetupWindowMetrics.ProseWidth, family: Tokens.FontStandard);

        var (run, runWidth) = FitText.WidestRun(block, text);
        var height = FitText.NaturalHeight(host, block, SetupWindowMetrics.ProseWidth);
        var ok = runWidth <= SetupWindowMetrics.ProseWidth && height <= SetupWindowMetrics.ProseHeight;
        return new FitVerdict(ok,
            $"widest run '{run}' {FitText.Dip(runWidth)} vs {SetupWindowMetrics.ProseWidth}, wrapped height "
            + $"{FitText.Dip(height)} vs {SetupWindowMetrics.ProseHeight}",
            FitText.ResolvedFace(block),
            Math.Min(SetupWindowMetrics.ProseWidth - runWidth, SetupWindowMetrics.ProseHeight - height));
    }

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
