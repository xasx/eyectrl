namespace EyeCtrl.Sdk.WinUI;

using System.Collections.Concurrent;
using EyeCtrl.Sdk.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

/// <summary>
/// Attached properties enabling gaze dwell on a WinUI <see cref="FrameworkElement"/>.
/// A single process-wide DwellClient connects to EyeCtrl lazily on first use. Each UI thread
/// gets its own rect-refresh timer, so multi-window apps (each window on its own dispatcher)
/// track layout on the correct thread. The SDK holds no static strong reference to any element:
/// per-element state lives on the element itself, so it is collected with the element.
/// </summary>
public static class Dwell
{
    /// <summary>Identifies the Dwell.Enabled attached property. Set <c>true</c> to make an element gaze-dwellable.</summary>
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(Dwell), new PropertyMetadata(false, OnEnabledChanged));

    /// <summary>Identifies the Dwell.Mode attached property (<see cref="DwellMode.Invoke"/> or <see cref="DwellMode.Focus"/>; defaults to Invoke).</summary>
    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
        "Mode", typeof(DwellMode), typeof(Dwell), new PropertyMetadata(DwellMode.Invoke));

    /// <summary>Gets the value of the Dwell.Enabled attached property.</summary>
    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);

    /// <summary>Sets the value of the Dwell.Enabled attached property.</summary>
    public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);

    /// <summary>Gets the value of the Dwell.Mode attached property.</summary>
    public static DwellMode GetMode(DependencyObject o) => (DwellMode)o.GetValue(ModeProperty);

    /// <summary>Sets the value of the Dwell.Mode attached property.</summary>
    public static void SetMode(DependencyObject o, DwellMode v) => o.SetValue(ModeProperty, v);

    // Per-element companion state is stashed on the element itself via a private attached property,
    // so it is collected together with the element — the SDK keeps no static reference to any element.
    private static readonly DependencyProperty EntryProperty = DependencyProperty.RegisterAttached(
        "Entry", typeof(object), typeof(Dwell), new PropertyMetadata(null));

    private static readonly DwellClient _client = new();
    private static int _started;
    private static long _idCounter;

    // One refresh timer per UI thread. Its Tick runs on that dispatcher's thread, so reading
    // thread-affine layout (XamlRoot / TransformToVisual / ActualWidth) always happens on the
    // element's own thread. Adapters are added on Loaded and removed on Unloaded, so only
    // currently-loaded elements are referenced here.
    //
    // LIFECYCLE: the group is created lazily on first Loaded for that dispatcher, and torn
    // down (timer stopped, group removed from _groups) when the last adapter is removed.
    // This avoids leaking a per-dispatcher timer for any dispatcher that ever hosted a dwell
    // element. The cleanup is driven by AdapterGroupTracker, which is unit-tested in
    // EyeCtrl.Sdk.WinUI.Tests.
    private sealed class ThreadGroup
    {
        public DispatcherQueueTimer Timer = null!;
        public readonly ConcurrentDictionary<string, WinUiElementAdapter> Adapters = new();
        public readonly AdapterGroupTracker Tracker;
        public ThreadGroup(Action onEmpty) => Tracker = new AdapterGroupTracker(onEmpty);
    }

    private static readonly ConcurrentDictionary<DispatcherQueue, ThreadGroup> _groups = new();

    private sealed record Entry(WinUiElementAdapter Adapter,
                                RoutedEventHandler Loaded, RoutedEventHandler Unloaded);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        if ((bool)e.NewValue) Register(el);
        else Unregister(el);
    }

    private static void Register(FrameworkElement el)
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) _client.Start();
        if (el.GetValue(EntryProperty) is Entry) return;

        var id = $"winui_{Interlocked.Increment(ref _idCounter)}";
        var adapter = new WinUiElementAdapter(id, el, GetMode(el));

        RoutedEventHandler loaded = (_, _) => OnLoaded(el, adapter);
        RoutedEventHandler unloaded = (_, _) => OnUnloaded(el, adapter);
        el.Loaded += loaded;
        el.Unloaded += unloaded;
        el.SetValue(EntryProperty, new Entry(adapter, loaded, unloaded));

        if (el.IsLoaded) OnLoaded(el, adapter);
    }

    private static void Unregister(FrameworkElement el)
    {
        if (el.GetValue(EntryProperty) is not Entry entry) return;
        el.ClearValue(EntryProperty);
        el.Loaded -= entry.Loaded;
        el.Unloaded -= entry.Unloaded;
        OnUnloaded(el, entry.Adapter);
    }

    private static void OnLoaded(FrameworkElement el, WinUiElementAdapter adapter)
    {
        var group = _groups.GetOrAdd(el.DispatcherQueue, CreateGroup);
        group.Adapters[adapter.Id] = adapter;
        group.Tracker.Add(adapter.Id);
        _client.RegisterAdapter(adapter);
    }

    private static void OnUnloaded(FrameworkElement el, WinUiElementAdapter adapter)
    {
        if (_groups.TryGetValue(el.DispatcherQueue, out var group))
        {
            group.Adapters.TryRemove(adapter.Id, out _);
            group.Tracker.Remove(adapter.Id);
        }
        _client.UnregisterAdapter(adapter.Id);
    }

    // Runs on the owning UI thread (GetOrAdd factory is invoked synchronously by OnLoaded, which
    // fires on that thread), so CreateTimer() binds the timer to the correct dispatcher. The
    // onEmpty callback tears down the group when the tracker reports no remaining adapters.
    private static ThreadGroup CreateGroup(DispatcherQueue dispatcher)
    {
        var group = new ThreadGroup(onEmpty: () =>
        {
            if (_groups.TryGetValue(dispatcher, out var g) && g.Tracker.IsEmpty)
            {
                g.Timer.Stop();
                _groups.TryRemove(dispatcher, out _);
            }
        });
        var timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(200);
        timer.Tick += (_, _) =>
        {
            foreach (var adapter in group.Adapters.Values)
                _client.OnAdapterLayoutChanged(adapter.Id);
        };
        timer.Start();
        group.Timer = timer;
        return group;
    }
}
