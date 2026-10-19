namespace EyeCtrl.App.Tests.Services;

using System.Windows.Interop;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Overlays;
using EyeCtrl.Core;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// F1 (magnifier-default-zoom fixup 01, review-01): the fit rule reads the screen's shorter side in
/// DIP. Every other gate runs at DPI 1.0, where DIP equals physical, so the "fit from the PHYSICAL
/// short side" mutant (720 DIP × 1.5 = 1080 px) survived: it would switch the rule OFF on exactly
/// the 150 % laptops it exists for. At scale 1.5 the same 1280×720 DIP screen must still cap to 81,
/// and the capture radius follows the DPI scale: (int)(81 × 1.5). Exclusive DpiHelper collection;
/// the scale is restored in a finally. Off-screen STA: EnsureHandle, never Show.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class MagnifierFitDpiTests
{
    private static readonly PhysicalPoint P = new(640, 360);

    [StaTheory]
    [InlineData(1280.0, 720.0)]    // landscape
    [InlineData(720.0, 1280.0)]    // portrait: the one-axis mutants die here
    public void AtScale15_TheFitStaysInDip_AndTheCaptureScales(double screenWidthDip, double screenHeightDip)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(1.5, 1.5);
        try
        {
            var settings = new EyeCtrlSettings();
            settings.Cursor.FilterMode = GazeFilterMode.Direct;
            settings.SelectionMode = SelectionMode.ManualSelection;
            settings.ManualSelection.CircleRadiusPixels = 100;
            settings.ManualSelection.MagnificationLevel = 4.0;

            var provider = new TestFakes.FakeGazeProvider();
            using var engine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
            var messenger = new WeakReferenceMessenger();
            StartManualSelectionMessage? start = null;
            messenger.Register<StartManualSelectionMessage>(this, (_, m) => start = m);

            using var adapter = new GazeEngineAdapter(
                engine, settings, messenger, Dispatcher.CurrentDispatcher,
                NullLogger<GazeEngineAdapter>.Instance, new StubControlFinder(),
                screenDip: () => (screenWidthDip, screenHeightDip));

            engine.InteractionManager.OnFixationStarted(P);
            engine.InteractionManager.OnFixationSettled(P);
            engine.InteractionManager.OnActivationButtonDwelled();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

            Check.WithCustomMessage("the start message must be sent").That(start).IsNotNull();
            Check.WithCustomMessage(
                    $"at 150 % the DIP short side (720) must still cap to 81; saw {start!.Radius}. "
                    + "The physical short side (1080) would let the 800-DIP view fit and answer 100.")
                .That(start.Radius).IsEqualTo(81);
            Check.That(start.Magnification).IsEqualTo(4.0);

            ManualSelectionOverlayWindow? window = null;
            try
            {
                window = new ManualSelectionOverlayWindow();
                new WindowInteropHelper(window).EnsureHandle();
                int capturedPhysicalRadius = -1;
                window.Capture = (_, radius) =>
                {
                    capturedPhysicalRadius = radius;
                    return new CaptureResult(ManualSelectionCaptureBeforeShowTests.Solid(648, 64), 1, 10, 648);
                };

                window.UpdateCenter(P, start.Radius, start.Magnification);

                Check.WithCustomMessage($"the view is 2 × 81 × 4 DIP; saw {window.Width}")
                    .That(window.Width).IsEqualTo(648.0);
                Check.WithCustomMessage("the capture radius follows the DPI scale: (int)(81 × 1.5) = 121")
                    .That(capturedPhysicalRadius).IsEqualTo((int)(81 * 1.5));
            }
            finally
            {
                window?.Close();
            }
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    private sealed class StubControlFinder : IControlFinder
    {
        public ControlInfo? FindTarget(PhysicalPoint point) => null;
    }
}
