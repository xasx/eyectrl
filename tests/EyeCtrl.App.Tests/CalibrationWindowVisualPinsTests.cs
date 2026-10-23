namespace EyeCtrl.App.Tests;

using System;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// Structural pins for the round-2/round-3 calibration fixes — the things the contrast
/// suites cannot see because they read <see cref="ITheme"/> tokens, not constructed
/// windows (round 3, m3): the cards that carry the scrim text, the halo companions of
/// the dot and rings, and the <see cref="CalibrationViewModel.TargetColor"/> attach fix
/// (R1 — the persisted colour was never applied because the view model announces no
/// initial change).
///
/// Joins the WpfApplication collection for the Application-singleton reasons documented
/// on <see cref="WpfApplicationGate"/>.
/// </summary>
[Collection("WpfApplication")]
public class CalibrationWindowVisualPinsTests
{
    private sealed class FakeGazeProvider : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected => true;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private static CalibrationWindow ConstructWindow()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        return new CalibrationWindow();
    }

    private static CalibrationViewModel CreateVm(string targetColor)
    {
        var settings = new CalibrationSettings { PointCount = 3, TargetHoldMs = 200, TargetColor = targetColor };
        return new CalibrationViewModel(
            settings,
            new CalibrationService(),
            new WeakReferenceMessenger(),
            new FakeGazeProvider(),
            new PhysicalPoint(1920, 1080),
            "test-monitor",
            Path.Combine(Path.GetTempPath(), "eyectrl-cal-pin-" + Guid.NewGuid().ToString("N") + ".json"));
    }

    [StaFact]
    public void SetViewModel_AppliesThePersistedTargetColor_WithoutWaitingForAChange()
    {
        var window = ConstructWindow();
        try
        {
            window.SetViewModel(CreateVm("#123456"));

            var fill = (SolidColorBrush)window.TargetDot.Fill;
            Check.WithCustomMessage(
                    "R1: the dot must show the persisted TargetColor at attach — the view " +
                    "model announces no initial change, so waiting for PropertyChanged left " +
                    "the theme accent in place forever.")
                .That(fill.Color).IsEqualTo(Color.FromRgb(0x12, 0x34, 0x56));
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void TargetDot_CarriesAStrokeHaloRim()
    {
        var window = ConstructWindow();
        try
        {
            var stroke = (SolidColorBrush)window.TargetDot.Stroke;
            Check.WithCustomMessage(
                    "The dot is a user-coloured absolute drawn on the black scrim; its rim " +
                    "must be the theme's halo so any pick stays visible (round 3, R1).")
                .That(stroke.Color).IsEqualTo(Tokens.StrokeHalo);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void ScrimText_SitsOnSurfaceDarkCards()
    {
        var window = ConstructWindow();
        try
        {
            foreach (var card in new[] { window.ProgressCard, window.CompletionCard, window.CancelCard, window.CancelDwellButton })
            {
                var background = (SolidColorBrush)card.Background;
                Check.WithCustomMessage(
                        "m2: the cards were SurfaceMedium, which put Midnight's TextDim hint " +
                        "at 4.32:1 — under the 4.5 floor. SurfaceDark clears on every theme.")
                    .That(background.Color).IsEqualTo(Tokens.SurfaceDark);
            }

            Check.WithCustomMessage("The prompt text must live inside its card, not beside it.")
                .That(System.Windows.Media.VisualTreeHelper.GetParent(window.ProgressText))
                .IsSameReferenceAs(window.ProgressCard);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void HaloCompanions_StaySyncedWithTheirRings()
    {
        var window = ConstructWindow();
        try
        {
            // XAML initial state: halo thickness = ring + 2 (the §4.1.3 idiom).
            Check.That(window.ToleranceHalo.StrokeThickness).IsEqualTo(window.ToleranceRing.StrokeThickness + 2);
            Check.That(window.ProgressHalo.StrokeThickness).IsEqualTo(window.ProgressRing.StrokeThickness + 2);

            // The runtime sync (m3a): driving IsOnTarget through the view model must move
            // the halo with the ring — thickness AND opacity. Round 3's mutation showed
            // nothing else catches a desync, which on Daylight reverses the on-target /
            // off-target look.
            var vm = CreateVm("#123456");
            window.SetViewModel(vm);

            vm.IsOnTarget = true;
            AssertSynced(window);
            vm.IsOnTarget = false;
            AssertSynced(window);

            // Round 4, m2: the PROGRESS pair syncs too. It is driven by
            // TargetProgress, and a desync here leaves the halo at its XAML
            // Opacity=0 — on Daylight the dark progress ring would lose its
            // white backing and the "it is filling" signal would vanish.
            vm.TargetProgress = 0.5;
            Check.That(window.ProgressHalo.StrokeThickness)
                 .IsEqualTo(window.ProgressRing.StrokeThickness + 2);
            Check.That(window.ProgressHalo.Opacity).IsEqualTo(window.ProgressRing.Opacity);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertSynced(CalibrationWindow window)
    {
        Check.WithCustomMessage("halo thickness must track the ring's, +2")
            .That(window.ToleranceHalo.StrokeThickness)
            .IsEqualTo(window.ToleranceRing.StrokeThickness + 2);
        Check.WithCustomMessage("halo opacity must track the ring's")
            .That(window.ToleranceHalo.Opacity).IsEqualTo(window.ToleranceRing.Opacity);
    }
}
