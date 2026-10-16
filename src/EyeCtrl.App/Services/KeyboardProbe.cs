namespace EyeCtrl.App.Services;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The production <see cref="IKeyboardProbe"/> (P1 D5): reads the keyboard VM's visibility, the
/// keyboard window's laid-out bounds and the shared edge bar's button union. Change sources are
/// the same set GazeConsumerInventory wires (P1 D5), each raising <see cref="IKeyboardProbe.Changed"/>
/// one dispatcher hop later (Background — after layout), so a reader reacting to a flip sees the
/// geometry the flip produced, not the one it replaced.
/// </summary>
internal sealed class KeyboardProbe : IKeyboardProbe, IDisposable
{
    private readonly KeyboardViewModel _keyboardVm;
    private readonly Dispatcher _dispatcher;
    private readonly Func<KeyboardWindow> _keyboard;
    private readonly Func<EdgeButtonBarViewModel?> _edgeBarVm;
    private readonly IMessenger _messenger;
    private bool _disposed;

    public event Action? Changed;

    public bool IsVisible => _keyboardVm.IsVisible;

    public KeyboardProbe(KeyboardViewModel keyboardVm, Dispatcher dispatcher, IMessenger messenger,
        Func<KeyboardWindow> keyboard, Func<EdgeButtonBarViewModel?> edgeBarVm)
    {
        _keyboardVm = keyboardVm ?? throw new ArgumentNullException(nameof(keyboardVm));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _messenger = messenger ?? throw new ArgumentNullException(nameof(messenger));
        _keyboard = keyboard ?? throw new ArgumentNullException(nameof(keyboard));
        _edgeBarVm = edgeBarVm ?? throw new ArgumentNullException(nameof(edgeBarVm));

        // Eager, not lazy (P1 D5: "a small, non-lazy, UI-thread object"): the probe is constructed
        // once from EventCoordinator.Initialize, after OverlayManager.Initialize created every
        // window it reads.
        _keyboardVm.PropertyChanged += OnKeyboardVmPropertyChanged;
        var window = _keyboard();
        window.SizeChanged += OnKeyboardLayoutChanged;
        window.Loaded += OnKeyboardLayoutChanged;
        if (_edgeBarVm() is { } bar)
            bar.ButtonSetChanged += Raise;
        _messenger.Register<KeyboardProbe, KeyboardEdgeToggledMessage>(this, (_, _) => Raise());
    }

    public IReadOnlyList<PhysicalRect> Rects()
    {
        var rects = new List<PhysicalRect>(2);

        // The whole keyboard window — grid, prediction bar and category bar together (the same
        // read ActivationChromeZones/GazeConsumerInventory make). Visible AND sourced.
        if (_keyboardVm.IsVisible && _keyboard().TryGetChromeBounds(out var bounds))
            rects.Add(bounds);

        // The visible edge bar's hit-area union, so the Close/Flip pair parked outside the
        // keyboard window still gates note dwell.
        if (_edgeBarVm() is { IsVisible: true } bar
            && GazeConsumerInventory.EdgeButtonBarBounds(
                bar.SeedCenters, ButtonRadii(bar), DpiHelper.ScaleX) is { } barRect)
            rects.Add(barRect);

        return rects;
    }

    /// <summary>The same per-button radius list the inventory builds for its own union — shared,
    /// not copied, so the two readers cannot disagree about a button's size.</summary>
    private static (string Id, double RadiusLogical)[] ButtonRadii(EdgeButtonBarViewModel vm)
    {
        var buttons = new (string Id, double RadiusLogical)[vm.Buttons.Count];
        for (var i = 0; i < vm.Buttons.Count; i++)
            buttons[i] = (vm.Buttons[i].Id, vm.Buttons[i].RadiusLogical);
        return buttons;
    }

    private void OnKeyboardVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // The offer/inventory pair's exact triggers: IsVisible flips on show/hide, IsEmojiMode
        // collapses the prediction bar and resizes the window. The VM raises continuously while
        // typing; anything wider would raise per keystroke.
        if (e.PropertyName is nameof(KeyboardViewModel.IsVisible) or nameof(KeyboardViewModel.IsEmojiMode))
            Raise();
    }

    private void OnKeyboardLayoutChanged(object? sender, System.EventArgs e) => Raise();

    private void Raise() =>
        _dispatcher.InvokeAsync(() =>
        {
            if (!_disposed) Changed?.Invoke();
        }, DispatcherPriority.Background);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _keyboardVm.PropertyChanged -= OnKeyboardVmPropertyChanged;
        var window = _keyboard();
        window.SizeChanged -= OnKeyboardLayoutChanged;
        window.Loaded -= OnKeyboardLayoutChanged;
        if (_edgeBarVm() is { } bar)
            bar.ButtonSetChanged -= Raise;
        _messenger.UnregisterAll(this);
    }
}
