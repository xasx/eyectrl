// tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideWindowTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
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
    public void FullscreenSurface_FillsTheWindow_WithNoScrim_AndCarriesTheApprovedTypography()
    {
        // Owner-approved fullscreen amendment pins (2026-11-18). The fdb92cd0 scrim is SUPERSEDED:
        // the Card itself IS the guide — an edge-to-edge SurfaceDark surface (radius 0) that hides
        // the desktop entirely, so the root grid has exactly ONE child and it fills the window.
        // The approved typography is literal (56 title / 32 body), not the token ramp fdb92cd0
        // rode; the surface's background still resolves through DynamicResource so the theme
        // preview re-skins it.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var vm = new FirstRunGuideViewModel(new WeakReferenceMessenger(), 0,
            new EyeCtrlSettings(), sink: null, dwellRegistry: null);
        var window = new FirstRunGuideWindow(vm);
        InvisibleTestWindow.Prepare(window);
        window.Show();
        try
        {
            var root = (Grid)window.Content;
            Check.WithCustomMessage("the fullscreen surface leaves no scrim behind: the Card is the root grid's ONLY child")
                .That(root.Children.Count == 1 && ReferenceEquals(root.Children[0], window.Card)).IsTrue();
            Check.WithCustomMessage("the Card must fill the window — it IS the fullscreen surface")
                .That(window.Card.HorizontalAlignment == HorizontalAlignment.Stretch
                      && window.Card.VerticalAlignment == VerticalAlignment.Stretch).IsTrue();
            Check.WithCustomMessage("the fullscreen surface is edge-to-edge (radius 0)")
                .That(window.Card.CornerRadius).IsEqualTo(new CornerRadius(0));

            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);   // flush deferred resource resolution
            var surfaceBrush = window.Card.Background;
            Check.WithCustomMessage("the surface's SurfaceDark background must resolve through DynamicResource")
                .That(surfaceBrush).IsNotNull();
            Check.That(surfaceBrush).IsSameReferenceAs(window.Card.TryFindResource("BrushSurfaceDark"));

            Check.That(window.TitleText.FontSize).IsEqualTo(56);
            Check.That(window.BodyText.FontSize).IsEqualTo(32);

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

            var faces = new (string What, TextBlock Label, double Size)[]
            {
                ("choice card", cardLabel, 32),
                ("chrome Close", closeLabel, 32),
                ("theme chip", chipLabel, 32),
                ("dwell read-out", dwellReadOut, 40),
            };
            foreach (var (what, label, size) in faces)
            {
                Check.WithCustomMessage($"the {what} label must carry the owner-approved {size} DIP scale")
                    .That(label.FontSize).IsEqualTo(size);
            }

            // The approved geometry: action cards 320×190, Close 280×160, knob ± 200×160, and the
            // 220-DIP practice ring whose dwell radius the VM now mirrors (110 DIP). The ring
            // builds into an EMPTY StepContent slot (SetPractice skips a Grid), so the theme
            // grid steps aside first — visual pins only; the target-id flow lives in
            // FirstRunGuideWindowRenderTests.
            var actionCard = (Border)window.ChoiceRow.Children[0];
            Check.That(actionCard.Width).IsEqualTo(320);
            Check.That(actionCard.Height).IsEqualTo(190);
            var closeCard = (Border)window.ChromeCloseHost.Content;
            Check.That(closeCard.Width).IsEqualTo(280);
            Check.That(closeCard.Height).IsEqualTo(160);
            var knobCard = (Border)((StackPanel)window.KnobRow.Children[1]).Children[0];
            Check.That(knobCard.Width).IsEqualTo(200);
            Check.That(knobCard.Height).IsEqualTo(160);
            window.SetThemeGrid(false, []);
            window.SetPractice(true);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            var ring = ((Grid)window.StepContent.Content).Children.OfType<Ellipse>().First(e => e.StrokeThickness > 0);
            Check.That(ring.Width).IsEqualTo(220);
            Check.That(ring.Height).IsEqualTo(220);
            Check.That(ring.StrokeThickness).IsEqualTo(6);
        }
        finally
        {
            window.Close();
        }
    }
}
