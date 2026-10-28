namespace EyeCtrl.App.Tests.Fixtures;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// The Quick Actions grid in its production layout, hosted off-screen and never shown — the
/// harness B8's pins share. The window's own content is populated by the XAML's
/// <c>ItemsSource="{Binding Tiles}"</c> and laid out at the size the show path computes
/// (<see cref="QuickActionsGridWindow.ResizeToContent"/>), so the close row, the tile band and the
/// 100 DIP tile faces come from the production XAML and its row maths.
///
/// <para>
/// Nothing is shown: the owner's only input is gaze, and a shown overlay would take the screen.
/// <c>PointToScreen</c> — the source of every dwell target the production push registers — needs a
/// presentation source, so the content is hosted in an off-screen <c>WS_POPUP</c> HwndSource, the
/// same technique <c>FitHost</c> uses. The caller owns the DPI scope
/// (<see cref="DpiHelper"/> is process-global); this host only takes the scale to map DIPs to the
/// physical pixels that scope defines.
/// </para>
/// </summary>
internal sealed class QuickActionsGridHost : IDisposable
{
    private const int WsPopup = unchecked((int)0x80000000);

    private sealed class FakeStore : IQuickActionsStore
    {
        private readonly List<QuickAction> _actions;
        public FakeStore(List<QuickAction> actions) => _actions = actions;

        public IReadOnlyList<QuickAction> LoadAll() => _actions;
        public void Save(IReadOnlyList<QuickAction> actions) { }
        public QuickAction? FindById(string id) => _actions.FirstOrDefault(a => a.Id == id);
    }

    /// <summary>Builds <paramref name="count"/> distinct keyboard-shortcut actions.</summary>
    internal static List<QuickAction> MakeActions(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new QuickAction
            {
                Label = $"Action {i}",
                Type = QuickActionType.KeyboardShortcut,
                Payload = "Ctrl+C",
            })
            .ToList();

    private readonly HwndSource _source;
    private readonly Border _frame;
    private readonly FrameworkElement _root;
    private readonly List<QuickAction> _actions;

    private QuickActionsGridHost(QuickActionsGridWindow window, QuickActionsGridViewModel vm,
        HwndSource source, Border frame, FrameworkElement root, List<QuickAction> actions)
    {
        Window = window;
        Vm = vm;
        _source = source;
        _frame = frame;
        _root = root;
        _actions = actions;
    }

    internal QuickActionsGridWindow Window { get; }
    internal QuickActionsGridViewModel Vm { get; }

    /// <summary>
    /// Builds the grid through <see cref="QuickActionsGridWindow.Configure"/> only — nothing
    /// hand-sets the ItemsControl — and lays it out off-screen at <paramref name="scale"/>.
    /// </summary>
    /// <param name="scale">The DPI scale the registered physical targets must describe.</param>
    /// <param name="tileCount">How many enabled quick actions the fake store holds.</param>
    /// <param name="borderThickness">
    /// Overrides the window chrome's <c>StandardBorderThickness</c> (0 = leave the XAML's value).
    /// The Contrast theme's 2 DIP border is the only theme input to this geometry, so it can be
    /// modelled without activating a theme process-wide.
    /// </param>
    internal static QuickActionsGridHost Open(double scale, int tileCount = 5, double borderThickness = 0)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var actions = MakeActions(tileCount);
        var vm = new QuickActionsGridViewModel(
            new FakeStore(actions), new EyeCtrlSettings(), new WeakReferenceMessenger());
        // Prepared even though nothing shows it: rule A of NoVisibleTestWindowGuardTests is per
        // construction, and this keeps the runtime check's contract if a future edit ever shows it.
        var window = InvisibleTestWindow.Prepare(new QuickActionsGridWindow());
        if (borderThickness > 0)
            window.RootBorder.BorderThickness = new Thickness(borderThickness);
        window.Configure(vm);
        vm.Reload();
        window.ResizeToContent();

        var root = (FrameworkElement)window.Content;
        window.Content = null;

        var source = new HwndSource(new HwndSourceParameters("b8-quick-actions-layout")
        {
            WindowStyle = WsPopup,
            PositionX = -30000,
            PositionY = -30000,
            Width = 1,
            Height = 1,
        });

        // The window lays out in logical DIPs as it does on screen (geometry is DPI-independent);
        // the RenderTransform — NOT a LayoutTransform, which would change the layout — only scales
        // the physical mapping PointToScreen reports. The content is detached, so the harness
        // takes over the DataContext Configure set on the window.
        var device = source.CompositionTarget!.TransformToDevice.M11;
        var frame = new Border
        {
            Child = root,
            Width = window.Width,
            Height = window.Height,
            DataContext = window.DataContext,
            RenderTransform = new ScaleTransform(scale / device, scale / device),
        };
        source.RootVisual = frame;
        frame.Measure(new Size(window.Width, window.Height));
        frame.Arrange(new Rect(0, 0, window.Width, window.Height));
        frame.UpdateLayout();

        var effective = (root.PointToScreen(new Point(100, 0)).X
                         - root.PointToScreen(new Point(0, 0)).X) / 100.0;
        if (Math.Abs(effective - scale) > 1e-6)
            throw new InvalidOperationException(
                $"the off-screen host's effective scale is {effective:0.###}, not {scale}. The "
                + "registered targets would not describe the DPI under test.");

        return new QuickActionsGridHost(window, vm, source, frame, root, actions);
    }

    /// <summary>
    /// Replaces the store's action list and reloads the VM — the "count grew between shows" case
    /// (<see cref="QuickActionsGridWindow.ResizeToContent"/> then decides the new size).
    /// </summary>
    internal void SetTileCount(int count)
    {
        _actions.Clear();
        _actions.AddRange(MakeActions(count));
        Vm.Reload();
    }

    /// <summary>An element's rectangle in the content's DIP space.</summary>
    internal Rect InContent(FrameworkElement element) =>
        element.TransformToAncestor(_frame).TransformBounds(new Rect(element.RenderSize));

    /// <summary>A content-space DIP point mapped to the physical pixel the production push sees.</summary>
    internal Point ToScreen(Point contentPoint) => _root.PointToScreen(contentPoint);

    /// <summary>
    /// Re-lays the hosted content after <see cref="QuickActionsGridWindow.ResizeToContent"/> changed
    /// the window's computed size (the count-grew re-show) — still off-screen and unshown.
    /// </summary>
    internal void Relayout()
    {
        _frame.Width = Window.Width;
        _frame.Height = Window.Height;
        _frame.Measure(new Size(Window.Width, Window.Height));
        _frame.Arrange(new Rect(0, 0, Window.Width, Window.Height));
        _frame.UpdateLayout();
    }

    /// <summary>
    /// One realised container per configured tile, in VM order. A null container means the XAML's
    /// <c>ItemsSource</c> binding did not populate the control, so this throws with that sentence
    /// rather than a null-reference later.
    /// </summary>
    internal IReadOnlyList<FrameworkElement> TileContainers() =>
        Vm.Tiles.Select(tile =>
        {
            var container = Window.TilesControl.ItemContainerGenerator.ContainerFromItem(tile);
            if (container is not FrameworkElement element)
                throw new InvalidOperationException(
                    $"tile '{tile.Label}' has no realised container — the XAML's "
                    + "ItemsSource=\"{Binding Tiles}\" is missing or broken.");
            return element;
        }).ToList();

    /// <summary>
    /// The realized tile FACE per tile, in VM order: the ItemTemplate's root Border (the drawn
    /// disc whose size F2 couples to the r50 hit circle). Read off the visual tree, not the
    /// template, so it is the effective realized element.
    /// </summary>
    internal IReadOnlyList<Border> TileFaces() =>
        TileContainers().Select(container =>
            VisualTreeHelper.GetChild(container, 0) as Border
            ?? throw new InvalidOperationException(
                "a tile container's visual child is not the ItemTemplate's Border."))
        .ToList();

    public void Dispose()
    {
        _source.RootVisual = null;
        _source.Dispose();
        _frame.Child = null;
        Window.Close();
    }
}
