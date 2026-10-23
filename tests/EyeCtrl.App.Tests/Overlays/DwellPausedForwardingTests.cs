namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// A1 fixup-01 M2: the hop from an item view model's held-dwell flag to the element the owner
/// actually sees. The detector and the view models are pinned elsewhere; these drive the flag and
/// read the REAL rendered progress element — the DwellArcButton the window painted, or the
/// realised ProgressBar of a XAML template — for every A1 surface that shows one.
///
/// <para>
/// Nothing here shows a window except the action menu, whose ring is painted only inside its
/// <c>Show</c>: that one goes through <see cref="InvisibleTestWindow.Prepare"/>, exactly as
/// <c>ActionMenuWindowDimRenderTests</c> does. The XAML bars are laid out in the fit harness's
/// invisible off-screen <c>HwndSource</c>, which realises item templates without a window.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class DwellPausedForwardingTests
{
    private const double Held = DwellPausedCue.ProgressOpacity;

    // ------------------------------------------------------------------ the four code-behinds

    [StaFact]
    public void Toolbar_ForwardsThePausedFlag_ToTheButtonsArc()
    {
        var (window, vm) = ToolbarTestHelpers.NewBoundToolbar();
        var pause = vm.Buttons.Single(b => b.Id == "Pause");

        pause.IsDwellPaused = true;
        Check.That(window.ButtonForTest("Pause").ProgressPath.Opacity).IsEqualTo(Held);

        pause.IsDwellPaused = false;
        Check.That(window.ButtonForTest("Pause").ProgressPath.Opacity).IsEqualTo(1.0);
    }

    // Mutation (A1-f1): ToolbarWindow's IsDwellPaused handler branch removed -> red.

    [StaFact]
    public void Toolbar_KeepsTheDim_AcrossAnIconSwapRebuild()
    {
        var (window, vm) = ToolbarTestHelpers.NewBoundToolbar();
        var pause = vm.Buttons.Single(b => b.Id == "Pause");
        pause.IsDwellPaused = true;

        pause.IconKey = "Play";   // SwapIcon rebuilds the DwellArcButton: a fresh arc at opacity 1

        Check.WithCustomMessage("the rebuilt arc lost the held look")
            .That(window.ButtonForTest("Pause").ProgressPath.Opacity).IsEqualTo(Held);
    }

    // Mutation (A1-f2): the SetPaused re-apply in the IconKey branch removed -> red.

    [StaFact]
    public void Toolbar_AppliesAHeldFlag_ThatPredatesTheBinding()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = InvisibleTestWindow.Prepare(new ToolbarWindow());
        window.Initialize(ToolbarTestHelpers.ButtonSize, 700, includeReset: false);
        var vm = new ToolbarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        vm.Buttons.Single(b => b.Id == "Pause").IsDwellPaused = true;

        window.DataContext = vm;
        window.BindToViewModel();

        Check.That(window.ButtonForTest("Pause").ProgressPath.Opacity).IsEqualTo(Held);
    }

    // Mutation (A1-f3): the SetPaused in the initial-apply block removed -> red.

    [StaFact]
    public void ModesMenu_ForwardsThePausedFlag_ToTheRowsArc()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var vm = new ModesMenuViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        var window = InvisibleTestWindow.Prepare(new ModesMenuWindow());
        window.Configure(vm, 120);
        vm.SetRowScreenPositions(window.BuildButtonsAt(120, 120, 250));
        var row = vm.Buttons.First();

        row.IsDwellPaused = true;
        Check.That(window.DrawnButtons[row.Id].ProgressPath.Opacity).IsEqualTo(Held);

        row.IsDwellPaused = false;
        Check.That(window.DrawnButtons[row.Id].ProgressPath.Opacity).IsEqualTo(1.0);
    }

    // Mutation (A1-f4): ModesMenuWindow's IsDwellPaused branch removed -> red.

    [StaFact]
    public void EdgeBar_ForwardsThePausedFlag_AndRebuildsWithIt()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        var window = InvisibleTestWindow.Prepare(new EdgeButtonBarWindow { DataContext = vm });
        vm.ShowButtons(
            [new EdgeButtonDefinition("Drag_cancel", "✕", 700, m => m.Send(new DragCancelMessage()), 30)],
            new PhysicalPoint(1000, 500));
        window.SyncButtons();
        var state = vm.Buttons.Single();

        state.IsDwellPaused = true;
        Check.That(window.DrawnButtons["Drag_cancel"].ProgressPath.Opacity).IsEqualTo(Held);

        window.SyncButtons();   // a rebuild while held keeps the look
        Check.That(window.DrawnButtons["Drag_cancel"].ProgressPath.Opacity).IsEqualTo(Held);

        state.IsDwellPaused = false;
        Check.That(window.DrawnButtons["Drag_cancel"].ProgressPath.Opacity).IsEqualTo(1.0);
    }

    // Mutation (A1-f5): EdgeButtonBarWindow's handler branch removed -> red.
    // Mutation (A1-f6): the SetPaused at build removed -> the rebuild check goes red.

    [StaFact]
    public void ActionMenu_ForwardsThePausedFlag_ToTheItemsArc()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var vm = new ActionMenuViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        vm.Receive(new ShowActionMenuMessage(
            Center: new PhysicalPoint(500, 500),
            Actions: new List<InteractionAction> { InteractionAction.LeftClick, InteractionAction.RightClick },
            CircleSize: 250, DwellTimeMs: 600, GazePoint: new PhysicalPoint(500, 500), ShowBack: false));

        var window = InvisibleTestWindow.Prepare(new ActionMenuWindow { DataContext = vm });
        try
        {
            window.Show(
                new PhysicalPoint(500, 500),
                new List<InteractionAction> { InteractionAction.LeftClick, InteractionAction.RightClick },
                showBackButton: false, dwellTimeMs: 600, ringRadius: 250, previewCircleSize: 250,
                borderColor: "#FFFFFF", positionIndicatorStyle: PositionIndicatorStyle.Crosshair);
            var item = vm.Items.First();

            item.IsDwellPaused = true;
            Check.That(window.DrawnButtonsForTest[item.Id].ProgressPath.Opacity).IsEqualTo(Held);

            item.IsDwellPaused = false;
            Check.That(window.DrawnButtonsForTest[item.Id].ProgressPath.Opacity).IsEqualTo(1.0);
        }
        finally
        {
            window.Hide();
            window.Close();
        }
    }

    // Mutation (A1-f7): ActionMenuWindow's IsDwellPaused branch removed -> red.

    // ------------------------------------------------------------------ the three XAML bindings

    private static ProgressBar RealisedBar(Window window, Func<FrameworkElement, ProgressBar> find, out FitHost host)
    {
        var root = (FrameworkElement)window.Content;
        var context = window.DataContext;
        window.Content = null;
        root.DataContext = context;   // detached from the window, the root no longer inherits it
        host = FitHost.Open(root, inheritFrom: window);
        return find(root);
    }

    [StaFact]
    public void TaskSwitcherTile_BindsThePausedFlag_ToItsBarsOpacity()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = InvisibleTestWindow.Prepare(new TaskSwitcherWindow());
        var tile = new TaskSwitcherTileViewModel(new IntPtr(4242), "target", 1234, "target", new PhysicalPoint(0, 0));
        window.TilesPanel.ItemsSource = new[] { tile };

        var bar = RealisedBar(window, r => FitText.Descendants<ProgressBar>(r).Single(), out var host);
        using (host)
        {
            Check.That(bar.Opacity).IsEqualTo(1.0);
            tile.IsDwellPaused = true;
            Check.WithCustomMessage("the tile bar's Opacity binding did not follow IsDwellPaused")
                .That(bar.Opacity).IsEqualTo(Held);
            tile.IsDwellPaused = false;
            Check.That(bar.Opacity).IsEqualTo(1.0);
        }
    }

    // Mutation (A1-f8): the binding path typo'd (IsDwellPausedX) -> a silent binding error, red.

    [StaFact]
    public void QuickActionsTile_BindsThePausedFlag_ToItsBarsOpacity()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = InvisibleTestWindow.Prepare(new QuickActionsGridWindow());
        var tile = new QuickActionTileViewModel(new QuickAction { Label = "a", Type = QuickActionType.KeyboardShortcut });
        window.TilesControl.ItemsSource = new[] { tile };

        var bar = RealisedBar(window,
            r => FitText.Descendants<ProgressBar>(FitText.Descendants<Border>(r).Single(b => b.Name == "TileBorder")).Single(),
            out var host);
        using (host)
        {
            tile.IsDwellPaused = true;
            Check.That(bar.Opacity).IsEqualTo(Held);
            tile.IsDwellPaused = false;
            Check.That(bar.Opacity).IsEqualTo(1.0);
        }
    }

    // Mutation (A1-f9): the tile binding removed -> red.

    private sealed class NoActions : IQuickActionsStore
    {
        public IReadOnlyList<QuickAction> LoadAll() => Array.Empty<QuickAction>();
        public void Save(IReadOnlyList<QuickAction> actions) { }
        public QuickAction? FindById(string id) => null;
    }

    [StaFact]
    public void QuickActionsClose_BindsCloseDwellPaused_ToItsBarsOpacity()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = InvisibleTestWindow.Prepare(new QuickActionsGridWindow());
        var vm = new QuickActionsGridViewModel(new NoActions(), new EyeCtrlSettings(), new WeakReferenceMessenger());
        window.DataContext = vm;
        var closeButton = window.CloseButton;

        var bar = RealisedBar(window, _ => FitText.Descendants<ProgressBar>(closeButton).Single(), out var host);
        using (host)
        {
            vm.CloseDwellPaused = true;
            Check.That(bar.Opacity).IsEqualTo(Held);
            vm.CloseDwellPaused = false;
            Check.That(bar.Opacity).IsEqualTo(1.0);
        }
    }

    // Mutation (A1-f10): the close binding names IsDwellPaused instead of CloseDwellPaused -> red.
}
