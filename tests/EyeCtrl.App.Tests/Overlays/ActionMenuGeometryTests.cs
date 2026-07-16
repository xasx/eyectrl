namespace EyeCtrl.App.Tests.Overlays;

using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// Headless regression tests for the action-menu button layout. No window is constructed and no
/// input is simulated, so these are safe to run without disturbing an eye-tracking user.
///
/// Guards commit 894ea34, which placed buttons at ABSOLUTE screen coordinates on the window-relative
/// MenuCanvas — pushing every button off-canvas so the ring rendered empty while the preview circle
/// and indicator (which use the canvas center) still showed.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class ActionMenuGeometryTests
{
    [Theory]
    [InlineData(1.0, false)]
    [InlineData(1.0, true)]
    [InlineData(2.0, false)]  // 200% DPI (the user's setup)
    [InlineData(2.0, true)]
    public void EveryActionButton_LandsFullyOnCanvas(double scale, bool showBack)
    {
        DpiHelper.Initialize(scale, scale);

        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ActionMenuViewModel(settings, messenger);

        // Menu center as an absolute physical-screen point (as GazeEngineAdapter supplies it).
        var center = new PhysicalPoint(1200 * scale, 800 * scale);
        vm.Receive(new ShowActionMenuMessage(
            center,
            settings.ActionMenu.VisibleActions,
            ShowBack: showBack,
            DwellTimeMs: 600,
            GazePoint: center,
            ManualSelectionDisplayRadius: 0,
            CircleSize: settings.Cursor.CircleSize));

        // Window layout exactly as ActionMenuWindow.Show computes it.
        var windowSize = ActionMenuGeometry.WindowSize(vm.EffectiveRingRadius);
        var canvasCenter = new Point(windowSize / 2, windowSize / 2);

        // The visible actions must actually produce buttons ("no buttons in the action menu" symptom).
        Check.That(vm.Items.Count).IsStrictlyGreaterThan(0);

        foreach (var item in vm.Items)
        {
            var c = ActionMenuGeometry.ButtonCanvasCenter(item.Position, center, canvasCenter);
            var msg = $"button '{item.Id}' canvas-center {c} escapes the window (size {windowSize:F0}, scale {scale})";

            // The whole button (center ± radius) must sit inside the window, or WPF clips it away.
            Check.WithCustomMessage(msg).That(c.X - ActionMenuGeometry.ButtonRadius).IsStrictlyGreaterThan(0);
            Check.WithCustomMessage(msg).That(c.X + ActionMenuGeometry.ButtonRadius).IsStrictlyLessThan(windowSize);
            Check.WithCustomMessage(msg).That(c.Y - ActionMenuGeometry.ButtonRadius).IsStrictlyGreaterThan(0);
            Check.WithCustomMessage(msg).That(c.Y + ActionMenuGeometry.ButtonRadius).IsStrictlyLessThan(windowSize);
        }
    }

    [Fact]
    public void ButtonCanvasCenter_IsWindowRelative_NotAbsolute()
    {
        DpiHelper.Initialize(1.0, 1.0);

        var center = new PhysicalPoint(1500, 900);           // absolute menu center
        var canvasCenter = new Point(255, 255);              // window-relative center
        var itemPosition = new PhysicalPoint(1695, 900);     // a ring button 195px right of center

        var c = ActionMenuGeometry.ButtonCanvasCenter(itemPosition, center, canvasCenter);

        // Window-relative: 255 + (1695-1500) = 450 (on-canvas), NOT the absolute 1695 (off-canvas).
        Check.That(c.X).IsCloseTo(450d, 0.0001);
        Check.That(c.Y).IsCloseTo(255d, 0.0001);
    }
}
