namespace EyeCtrl.App.Tests.Keyboard;

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// F18's hide rule, on a SHOWN window: a hidden keyboard comes back at rest, the ring's C1 rule.
/// This file is deliberately NOT in the no-visible-window guard's headless list — it shows a
/// window — so it builds the window through <see cref="InvisibleTestWindow.PrepareCloaked"/>,
/// makes it click-through, parks it far off-screen and SKIPS if DWM cannot cloak it. It never
/// shows an uncloaked window.
/// </summary>
[Collection("WpfApplication")]
public class KeyboardFiredStepHideTests
{
    private const int KeyDwellMs = 450;
    private const int PredictionDwellMs = 500;
    private const string TextKey = "key_0_1";

    private static KeyboardLayout ShippedQwerty() =>
        KeyboardLayoutLoader.Load(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Resources", "Layouts", "qwerty.json"));

    private static Dictionary<string, PhysicalPoint> Centres(KeyboardWindow w) =>
        (Dictionary<string, PhysicalPoint>)typeof(KeyboardWindow)
            .GetField("_keyScreenPositions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    private static Dictionary<string, EyeCtrl.Controls.DwellOutlineOverlay> Outlines(KeyboardWindow w) =>
        (Dictionary<string, EyeCtrl.Controls.DwellOutlineOverlay>)typeof(KeyboardWindow)
            .GetField("_keyOutlines", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    private static void Feed(KeyboardWindow window, PhysicalPoint at, long fromUs, long toUs,
        long stepUs = 30_000)
    {
        Check.WithCustomMessage(
                $"Feed({fromUs}..{toUs}) step {stepUs}: the endpoint is not reachable on the step "
                + "grid, so the final sample would be dropped and the fire would not happen.")
            .That((toUs - fromUs) % stepUs).IsEqualTo(0);

        for (var t = fromUs; t <= toUs; t += stepUs)
            window.UpdateGaze(at, t);
    }

    [StaFact]
    public void HidingTheKeyboardMidStep_ReturnsItToRest_SoTheNextShowStepsNothing()
    {
        // Mutation: drop the hide handler's End → the outline is still lit (and still stepped)
        // after Hide → RED.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var settings = new EyeCtrlSettings();
        settings.Keyboard.SmartCapitalizationEnabled = false;
        var vm = new KeyboardViewModel(settings, new WeakReferenceMessenger());
        var window = InvisibleTestWindow.PrepareCloaked(new KeyboardWindow());
        try
        {
            window.SetViewModel(vm);
            window.LoadLayout(ShippedQwerty(), 1.0,
                keyDwellMs: KeyDwellMs, predictionDwellMs: PredictionDwellMs, cooldownMs: 0);
            if (InvisibleTestWindow.IsCloaked(window) != true)
                Assert.Skip("DWM cannot cloak a window here; this test never shows an uncloaked window.");

            window.SetOverlayInteraction(clickThrough: true, focusable: false);
            window.Left = -12000;
            window.Top = -12000;
            window.Show();
            window.UpdateLayout();

            var content = (FrameworkElement)window.Content;
            var topLeft = content.PointToScreen(new Point(0, 0));
            window.UpdateGaze(new PhysicalPoint(topLeft.X - 500, topLeft.Y - 500), 0);

            var centres = Centres(window);
            Check.That(centres.ContainsKey(TextKey)).IsTrue();
            var outline = Outlines(window)[TextKey];
            Feed(window, centres[TextKey], 1_000_000, 1_480_000);
            Check.That(window.IsKeyFiredStepLit).IsTrue();
            Check.That(outline.IsFired).IsTrue();

            window.Hide();

            Check.WithCustomMessage("The hide left the fired step lit.")
                .That(window.IsKeyFiredStepLit).IsFalse();
            Check.WithCustomMessage("The hide left the key's outline stepped.")
                .That((outline.Outline.StrokeThickness, outline.Halo.StrokeThickness))
                .IsEqualTo((0.0, 0.0));
            Check.That(outline.IsFired).IsFalse();

            window.Show();
            window.UpdateLayout();
            Check.WithCustomMessage("The next show brought the step back.")
                .That(window.IsKeyFiredStepLit).IsFalse();
            foreach (var (id, other) in Outlines(window))
            {
                Check.WithCustomMessage($"{id} is stepped after a hide and re-show.")
                    .That(other.IsFired).IsFalse();
            }
        }
        finally { window.Close(); }
    }
}
