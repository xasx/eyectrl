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
/// the same set GazeConsumerInventory wires (P1 D5) PLUS the keyboard window's position: a
/// toolbar yield / gaze-off un-yield moves the window without changing its size or the VM, and a
/// reader (the notes' Edit card) must re-derive against the rect it landed on. Each source raises
/// <see cref="IKeyboardProbe.Changed"/> one dispatcher hop later (Background — after layout), so a
/// reader reacting to a flip sees the geometry the flip produced, not the one it replaced.
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
        window.SizeChanged += OnKeyboardGeometryChanged;
        window.Loaded += OnKeyboardGeometryChanged;
        // A yield (the toolbar appears on the keyboard's edge) and the gaze-off un-yield move the
        // window by setting Left/Top: no size change, no VM flip, so without this the note's Edit
        // card keeps a placement derived against the vacated rect. LocationChanged covers every
        // such move, including a Flip and a DPI change, and fires once per axis that changed.
        window.LocationChanged += OnKeyboardGeometryChanged;
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
                bar.SeedCenters, GazeConsumerInventory.ButtonRadii(bar), DpiHelper.ScaleX) is { } barRect)
            rects.Add(barRect);

        return rects;
    }

    private void OnKeyboardVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // The offer/inventory pair's exact triggers: IsVisible flips on show/hide, IsEmojiMode
        // collapses the prediction bar and resizes the window. The VM raises continuously while
        // typing; anything wider would raise per keystroke.
        if (e.PropertyName is nameof(KeyboardViewModel.IsVisible) or nameof(KeyboardViewModel.IsEmojiMode))
            Raise();
    }

    private void OnKeyboardGeometryChanged(object? sender, System.EventArgs e) => Raise();

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
        window.SizeChanged -= OnKeyboardGeometryChanged;
        window.Loaded -= OnKeyboardGeometryChanged;
        window.LocationChanged -= OnKeyboardGeometryChanged;
        if (_edgeBarVm() is { } bar)
            bar.ButtonSetChanged -= Raise;
        _messenger.UnregisterAll(this);
    }
}
