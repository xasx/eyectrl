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
/// G5 (magnifier-default-zoom, Part 2, decision 3(a)): on a 720-DIP screen the start message, the
/// overlay's size and capture radius, and the action menu's display and preview radii all come
/// from the ONE effective radius (the fit rule's 81 DIP for 4x / circle 100), not from the raw
/// setting. Off-screen STA: the overlay gets a handle but is never shown. Mutants: any site
/// reading the raw setting, the screen source using the width instead of the shorter side, or the
/// cap using nothing.
/// </summary>
[Collection("WpfApplication")]
public class MagnifierFitWiringTests
{
    private static readonly PhysicalPoint P = new(640, 360);

    [StaFact]
    public void OnA720DipScreen_StartMenuAndOverlayShareOneEffectiveRadius()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        settings.SelectionMode = SelectionMode.ManualSelection;
        settings.ManualSelection.CircleRadiusPixels = 100;
        settings.ManualSelection.MagnificationLevel = 4.0;

        var provider = new TestFakes.FakeGazeProvider();
        using var engine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
        var messenger = new WeakReferenceMessenger();
        StartManualSelectionMessage? start = null;
        ShowActionMenuMessage? menu = null;
        messenger.Register<StartManualSelectionMessage>(this, (_, m) => start = m);
        messenger.Register<ShowActionMenuMessage>(this, (_, m) => menu = m);

        using var adapter = new GazeEngineAdapter(
            engine, settings, messenger, Dispatcher.CurrentDispatcher,
            NullLogger<GazeEngineAdapter>.Instance, new StubControlFinder(),
            screenDip: () => (1280, 720));   // the width is larger: using it would skip the fit

        // A manual-selection activation at P.
        engine.InteractionManager.OnFixationStarted(P);
        engine.InteractionManager.OnFixationSettled(P);
        engine.InteractionManager.OnActivationButtonDwelled();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        Check.WithCustomMessage($"the start message must be sent; saw radius {start?.Radius}")
            .That(start).IsNotNull();
        Check.WithCustomMessage($"the start message carries the effective radius; saw {start!.Radius}")
            .That(start.Radius).IsEqualTo(81);
        Check.That(start.Magnification).IsEqualTo(4.0);

        // The overlay consumes that radius: view and capture radius both derive from it.
        var window = new ManualSelectionOverlayWindow();
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            int capturedPhysicalRadius = -1;
            window.Capture = (_, radius) =>
            {
                capturedPhysicalRadius = radius;
                return new CaptureResult(ManualSelectionCaptureBeforeShowTests.Solid(648, 64), 1, 10, 648);
            };

            window.UpdateCenter(P, start.Radius, start.Magnification);

            Check.WithCustomMessage($"the view is 2 x 81 x 4 DIP; saw {window.Width}")
                .That(window.Width).IsEqualTo(648.0);
            Check.That(window.Height).IsEqualTo(648.0);
            Check.WithCustomMessage("the capture radius is the effective radius, scaled")
                .That(capturedPhysicalRadius).IsEqualTo((int)(81 * DpiHelper.ScaleX));
        }
        finally
        {
            window.Close();
        }

        // Back from the magnifier: the menu places its buttons outside the same effective circle and
        // previews it.
        engine.InteractionManager.OnManualSelectionDwell(new PhysicalPoint(700, 400));
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        Check.WithCustomMessage("the action-menu message must be sent").That(menu).IsNotNull();
        Check.WithCustomMessage($"the preview radius is the effective one; saw {menu!.ManualSelectionRadius}")
            .That(menu.ManualSelectionRadius).IsEqualTo(81);
        Check.WithCustomMessage($"the display radius is effective x zoom; saw {menu.ManualSelectionDisplayRadius}")
            .That(menu.ManualSelectionDisplayRadius).IsEqualTo(324);
    }

    private sealed class StubControlFinder : IControlFinder
    {
        public ControlInfo? FindTarget(PhysicalPoint point) => null;
    }
}
