namespace EyeCtrl.App.Tests.Overlays;

using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Xunit;

/// <summary>
/// B2b fixup-01 (review 01 F1/F2, adopted from the reviewer's probe): the REAL strip window's
/// note-gate occluder bounds at DPI 2.0. The wiring test runs on a fake strip, so this is the pin on
/// the window's own DIP → physical conversion — and on reporting nothing while the label card is
/// collapsed. The window is cloaked AND off-screen, like ResumeStripZOrderTests' rig; the test skips
/// if DWM cannot cloak. In the DpiHelper collection because it writes the process-global scale.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class StatusStripOccluderBoundsTests
{
    [StaFact]
    public void TheRealStripsBounds_AreTheCardInPhysicalPx_AtDpi2_AndNothingWhileTheCardIsCollapsed()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(2.0, 2.0);
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var strip = InvisibleTestWindow.PrepareCloaked(new PauseIndicatorWindow());
        try
        {
            if (InvisibleTestWindow.IsCloaked(strip) != true)
                Assert.Skip("DWM cannot cloak a window here.");
            strip.SetViewModel(new ResumeStripViewModel(settings, messenger));
            strip.Left = -12000;
            strip.Top = -12000;
            var adapter = new ToolbarVisibilityAdapter(
                TestFakes.MakeOverlayManager(toolbar: new TestFakes.FakeToolbarOverlay(), pauseIndicator: strip),
                Dispatcher.CurrentDispatcher, new ToolbarViewModel(settings, messenger));

            // F1: the label card, converted DIP → physical at 2.0.
            adapter.SetControlState(EyeCtrl.Core.Enums.ControlState.NoTracker);
            Check.That(strip.TryGetVisibleBounds(out var card)).IsTrue();
            var label = strip.LastLayout!.Value.Label;
            Check.That(card).IsEqualTo(new EyeCtrl.Core.Models.PhysicalRect(
                label.X * 2, label.Y * 2, label.Width * 2, label.Height * 2));

            // F2: the window stays up (paused) but the card collapses (the band is visible, so the
            // Paused label waits for the band to leave): a collapsed card occludes nothing.
            adapter.SetPauseIndicator(true);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            adapter.SetControlState(EyeCtrl.Core.Enums.ControlState.Paused);
            Check.That(strip.IsVisible).IsTrue();
            Check.That(strip.StateLabelCard.Visibility).IsEqualTo(Visibility.Collapsed);
            Check.WithCustomMessage("a collapsed label card must report no occluder bounds")
                .That(strip.TryGetVisibleBounds(out _)).IsFalse();
        }
        finally
        {
            strip.Close();
            DpiHelper.Initialize(1.0, 1.0);
        }
    }
}
