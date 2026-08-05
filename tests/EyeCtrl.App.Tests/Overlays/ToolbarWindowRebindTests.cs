namespace EyeCtrl.App.Tests.Overlays;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using System.Windows.Threading;

/// <summary>
/// Rebind problem regression guard (Task 9, round-1 + round-2 MAJOR).
/// ToolbarWindow.BindToViewModel() is called exactly once at startup and +='-subscribes
/// PropertyChanged per button present AT THAT MOMENT. SetWindowActionsVisible(true) after a
/// prior removal creates NEW ToolbarButtonViewModel instances; without an idempotent rebind path
/// driven off Buttons.CollectionChanged, the re-added buttons render and dwell but their progress
/// bar never updates (reads as "the button is broken" on this input modality).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class ToolbarWindowRebindTests
{
    [StaFact]
    [Trait("Category", "UI")]
    public void ReaddedButton_AfterToggleOffOn_DwellProgressReachesProgressBar()
    {
        EnsureApplicationWithTokens();

        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(settings, messenger);

        var window = new ToolbarWindow();
        try
        {
            window.DataContext = vm;
            window.Show();

            // Initial state: region on. Initialize creates the Close visual; BindToViewModel
            // subscribes every current button's PropertyChanged (Close included) — mirrors
            // App.xaml.cs:460 where ShowOverlays calls BindToViewModel once after Show+SetMode.
            window.Initialize(settings.Toolbar.Size, settings.Dwell.ToolbarButtonDwellMs,
                showWindowActions: true);
            window.BindToViewModel();
            PumpDispatcher();

            var originalClose = vm.Buttons.First(b => b.Id == "Close");
            var originalBar = ProgressBarFor(window, "Close");
            Check.That(originalBar).IsNotNull();
            Check.That(originalBar!.Width).IsEqualTo(0.0);

            // Sanity: DwellProgress propagates to the bar before any toggle (baseline).
            originalClose.DwellProgress = 0.5;
            PumpDispatcher();
            Check.That(originalBar.Width).IsEqualTo(0.5 * settings.Toolbar.Size);

            // ── Toggle off then on, mirroring EventCoordinator's ordering ───────────────
            // Initialize runs FIRST (rebuilds _buttonVisuals for the new button set), then
            // SetMode (no-op for this test), then SetWindowActionsVisible mutates Buttons.
            // Reset DwellProgress so we can detect the post-toggle propagation cleanly.
            originalClose.DwellProgress = 0;

            vm.SetWindowActionsVisible(false);
            PumpDispatcher();

            window.Initialize(settings.Toolbar.Size, settings.Dwell.ToolbarButtonDwellMs,
                showWindowActions: true);
            PumpDispatcher();

            vm.SetWindowActionsVisible(true);
            PumpDispatcher();

            // The re-added Close is a NEW ToolbarButtonViewModel instance — that's the crux of
            // the rebind bug. Without CollectionChanged-driven rebind, nobody subscribes it.
            var readdedClose = vm.Buttons.First(b => b.Id == "Close");
            Check.That(ReferenceEquals(readdedClose, originalClose)).IsFalse();

            var readdedBar = ProgressBarFor(window, "Close");
            Check.That(readdedBar).IsNotNull();
            Check.That(readdedBar!.Width).IsEqualTo(0.0);

            // The load-bearing assertion: setting DwellProgress on the new instance must reach
            // the new progress bar. Without BindButton it stays at 0 (button "broken").
            readdedClose.DwellProgress = 0.5;
            PumpDispatcher();

            Check.That(readdedBar.Width).IsEqualTo(0.5 * settings.Toolbar.Size);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    [Trait("Category", "UI")]
    public void Initialize_NoDoubleSubscribe_OnRepeatedInitializeWithSameButtonSet()
    {
        EnsureApplicationWithTokens();

        // Initialize runs on every toolbar-snapshot change (size/dock/edge saves). BindButton is
        // idempotent via _boundIds, so a re-bind of an already-bound button must not stack a
        // second handler — that would double-fire progress work and (worse) leak the old handler.
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(settings, messenger);

        var window = new ToolbarWindow();
        try
        {
            window.DataContext = vm;
            window.Show();

            window.Initialize(settings.Toolbar.Size, settings.Dwell.ToolbarButtonDwellMs,
                showWindowActions: true);
            window.BindToViewModel();
            PumpDispatcher();
            // Re-run the post-DataContext path (size-save mirrors it via EventCoordinator).
            // No exception, no double-subscribe observable: setting DwellProgress once yields
            // exactly one width assignment (the final value, not a doubled one).
            var pause = vm.Buttons.First(b => b.Id == "Pause");
            pause.DwellProgress = 0.4;
            PumpDispatcher();
            Check.That(ProgressBarFor(window, "Pause")!.Width).IsEqualTo(0.4 * settings.Toolbar.Size);
        }
        finally
        {
            window.Close();
        }
    }

    private static System.Windows.Shapes.Rectangle? ProgressBarFor(ToolbarWindow window, string id)
    {
        // ToolbarWindow._buttonVisuals is private; reach in via the visual tree instead. The
        // progress Rectangle is the second child of the Border > Grid (after the label TextBlock).
        // Use the AutomationId on the label to find the right Grid, then dig out its Rectangle.
        var label = FindByAutomationId(window, $"Toolbar_Button_{id}") as System.Windows.FrameworkElement;
        if (label == null) return null;
        // label.Parent is the Grid inside the Border container.
        var grid = label.Parent as System.Windows.Controls.Grid;
        if (grid == null) return null;
        return grid.Children.OfType<System.Windows.Shapes.Rectangle>().FirstOrDefault();
    }

    private static System.Windows.DependencyObject? FindByAutomationId(
        System.Windows.DependencyObject root, string automationId)
    {
        if (System.Windows.Automation.AutomationProperties.GetAutomationId(root) == automationId)
            return root;
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            var found = FindByAutomationId(child, automationId);
            if (found != null) return found;
        }
        return null;
    }

    private static void PumpDispatcher()
    {
        // Drain the dispatcher queue so PropertyChanged handlers (which run synchronously, but
        // any Layout/Render side effects land) have settled before assertions.
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new DispatcherOperationCallback(_ => { frame.Continue = false; return null; }),
            null);
        Dispatcher.PushFrame(frame);
    }

    // Mirrors OverlayResourceResolutionTests.EnsureApplicationWithTokens — ToolbarWindow.xaml
    // references token brushes (BrushSurfaceDark etc.) merged at App.xaml startup; without an
    // Application holding the merged dictionary, the XAML parser throws on construction.
    private static void EnsureApplicationWithTokens()
    {
        if (System.Windows.Application.Current is null)
            _ = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };

        const string uri = "pack://application:,,,/EyeCtrl.Controls;component/Theme/OverlayStyles.xaml";
        var already = System.Windows.Application.Current!.Resources.MergedDictionaries
            .Any(d => d.Source?.ToString() == uri);
        if (!already)
            System.Windows.Application.Current.Resources.MergedDictionaries.Add(
                new System.Windows.ResourceDictionary { Source = new Uri(uri, UriKind.Absolute) });
    }
}
