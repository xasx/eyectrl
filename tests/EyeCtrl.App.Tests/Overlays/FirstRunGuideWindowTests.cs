// tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideWindowTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Settings;
using EyeCtrl.Controls.Theme;
using EyeCtrl.ViewModels;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using NFluent;
using Xunit;

[Collection("WpfApplication")]
[Trait("Category", "UI")]
public class FirstRunGuideWindowTests
{
    [StaFact]
    public void Window_IsClickThrough_AllowsTransparency_Fullscreen_AndBindsTheVm()
    {
        var vm = new FirstRunGuideViewModel(new WeakReferenceMessenger(), 0,
            new EyeCtrlSettings(), sink: null, dwellRegistry: null);
        var window = new FirstRunGuideWindow(vm);
        InvisibleTestWindow.Prepare(window);
        window.Show();
        try
        {
            Check.That(window.IsClickThrough).IsTrue();
            Check.That(window.AllowsTransparency).IsTrue();
            Check.That(window.DataContext).IsSameReferenceAs(vm);
            Check.That(window.Width).IsStrictlyGreaterThan(SystemParameters.PrimaryScreenWidth - 1);
            Check.That(window.Height).IsStrictlyGreaterThan(SystemParameters.PrimaryScreenHeight - 1);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void ThemePreviewSwap_ReSkinsTheCodeBuiltChips_AndCardBorder()
    {
        // S2-2: the code-built visuals used to snapshot their brushes once (Card.FindResource at
        // build time), so a theme preview only re-skinned the XAML half of the card. Every
        // code-built brush now rides SetResourceReference and re-resolves through Card.Resources.
        var vm = new FirstRunGuideViewModel(new WeakReferenceMessenger(), 0,
            new EyeCtrlSettings(), sink: null, dwellRegistry: null);
        var window = new FirstRunGuideWindow(vm);
        InvisibleTestWindow.Prepare(window);
        window.Show();
        try
        {
            window.SetThemeGrid(true,
            [
                ("Midnight", "guide_theme_midnight"),
                ("Ember", "guide_theme_ember"),
                ("Daylight", "guide_theme_daylight"),
                ("Contrast", "guide_theme_contrast"),
            ]);
            var chips = ((Grid)window.StepContent.Content).Children.OfType<Border>().ToList();
            Check.That(chips).HasSize(4);

            // A theme whose accent differs from what the chips resolved at build time, so the
            // swap genuinely changes the brushes (whatever theme the ambient Application carries).
            var accentBefore = chips[0].BorderBrush as SolidColorBrush;
            var target = ThemeRegistry.All.First(t =>
                accentBefore is null || !Equals(t.BrushAccent.Color, accentBefore.Color));

            ThemeResourceApplier.Apply(window.CardResources, target);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);   // flush invalidations

            Check.WithCustomMessage("every chip's Background must follow the card-local theme swap")
                .That(chips.Select(c => c.Background))
                .ContainsOnlyElementsThatMatch(b => ReferenceEquals(b, window.CardResources["BrushSurfaceMedium"]));
            Check.WithCustomMessage("the chip card's BorderBrush must follow the card-local theme swap too")
                .That(chips[0].BorderBrush).IsSameReferenceAs(window.CardResources["BrushAccent"]);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void Scrim_SitsBehindTheCard_AndGuideTextRidesTheTokenRamp()
    {
        // Live-check follow-up pins (D1/D2). D1: the guide's fullscreen backdrop is transparent,
        // which read as a "white background" over the owner's light desktop — a SurfaceDark@70%
        // scrim sits FIRST in the root grid (a Grid's z-order IS child order, so first = behind
        // the Card) and must resolve its brush through DynamicResource like the card's. It joins
        // no dwell target. D2: every guide text face rides the token ramp at FontSizeSubtitle
        // (18); the title keeps its plan-pinned 24 (= FontSizeTitle, the XAML literal).
        WpfApplicationGate.EnsureApplicationWithTokens();
        var vm = new FirstRunGuideViewModel(new WeakReferenceMessenger(), 0,
            new EyeCtrlSettings(), sink: null, dwellRegistry: null);
        var window = new FirstRunGuideWindow(vm);
        InvisibleTestWindow.Prepare(window);
        window.Show();
        try
        {
            var root = (Grid)window.Content;
            Check.WithCustomMessage(
                    "the scrim must be the FIRST child of the root grid (behind the Card), which must be second")
                .That(ReferenceEquals(root.Children[0], window.Scrim)
                      && ReferenceEquals(root.Children[1], window.Card)).IsTrue();
            Check.That(window.Scrim.Opacity).IsEqualTo(0.7);

            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);   // flush deferred resource resolution
            var scrimBrush = window.Scrim.Background;
            Check.WithCustomMessage("the scrim's SurfaceDark background must resolve through DynamicResource")
                .That(scrimBrush).IsNotNull();
            Check.That(scrimBrush).IsSameReferenceAs(window.Scrim.TryFindResource("BrushSurfaceDark"));

            Check.That(window.BodyText.FontSize).IsEqualTo(Tokens.FontSizeSubtitle);
            Check.That(window.TitleText.FontSize).IsEqualTo(Tokens.FontSizeTitle);

            window.SetChoices([("Start", "guide_start"), ("Later", "guide_later")]);
            window.SetChromeClose(true, "✕");
            window.SetThemeGrid(true,
            [
                ("Midnight", "guide_theme_midnight"),
                ("Ember", "guide_theme_ember"),
                ("Daylight", "guide_theme_daylight"),
                ("Contrast", "guide_theme_contrast"),
            ]);
            window.SetDwellKnob(true);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

            var cardLabel = (TextBlock)((StackPanel)((Border)window.ChoiceRow.Children[0]).Child).Children[0];
            var closeLabel = (TextBlock)((StackPanel)((Border)window.ChromeCloseHost.Content).Child).Children[0];
            var chipLabel = (TextBlock)((StackPanel)((Border)((Grid)window.StepContent.Content).Children[0]).Child).Children[0];
            var dwellReadOut = window.KnobRow.Children.OfType<TextBlock>().Single();

            var faces = new (string What, TextBlock Label)[]
            {
                ("choice card", cardLabel),
                ("chrome Close", closeLabel),
                ("theme chip", chipLabel),
                ("dwell read-out", dwellReadOut),
            };
            foreach (var (what, label) in faces)
            {
                Check.WithCustomMessage($"the {what} label must ride the token ramp at FontSizeSubtitle")
                    .That(label.FontSize).IsEqualTo(Tokens.FontSizeSubtitle);
            }
        }
        finally
        {
            window.Close();
        }
    }
}
