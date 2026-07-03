namespace EyeCtrl.Sdk.Wpf;

using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Threading;
using EyeCtrl.Sdk.Abstractions;

/// <summary>
/// Attached properties enabling gaze dwell on a WPF FrameworkElement.
/// A single process-wide DwellClient connects to EyeCtrl lazily on first use.
/// The SDK holds no static strong reference to any element.
/// Each UI thread gets its own rect-refresh timer so dwell targets track window moves and reconnects.
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

    private static readonly DependencyProperty EntryProperty = DependencyProperty.RegisterAttached(
        "Entry", typeof(object), typeof(Dwell), new PropertyMetadata(null));

    // One refresh timer per UI thread (WPF supports multiple dispatchers). Its Tick runs on
    // that dispatcher's thread, so PointToScreen/ActualWidth are read on the element's own
    // thread. Covers window moves (which fire no LayoutUpdated) and re-sends rects after a
    // service reconnect. Unchanged rects are deduped by OutboundRectTracker, so the
    // 200ms tick normally sends nothing.
    //
    // LIFECYCLE: the group is created lazily on first Loaded for that dispatcher and torn
    // down (timer stopped, group evicted) when the last adapter is removed, driven by the
    // shared AdapterGroupTracker.
    private sealed class ThreadGroup
    {
        public DispatcherTimer Timer = null!;
        public readonly ConcurrentDictionary<string, WpfElementAdapter> Adapters = new();
        public readonly AdapterGroupTracker Tracker;
        public ThreadGroup(Action onEmpty) => Tracker = new AdapterGroupTracker(onEmpty);
    }

    private static readonly ConcurrentDictionary<Dispatcher, ThreadGroup> _groups = new();

    private static readonly DwellHeadCore _core = new("wpf_");

    private sealed record DwellEntry(WpfElementAdapter Adapter,
                                     RoutedEventHandler Loaded, RoutedEventHandler Unloaded);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        if ((bool)e.NewValue) Register(el);
        else Unregister(el);
    }

    private static void Register(FrameworkElement el)
    {
        _core.EnsureStarted();
        if (el.GetValue(EntryProperty) is DwellEntry) return;

        var id = _core.NextId();
        var adapter = new WpfElementAdapter(id, el, GetMode(el));
        RoutedEventHandler loaded = (_, _) => OnLoaded(el, adapter);
        RoutedEventHandler unloaded = (_, _) => OnUnloaded(el, adapter);
        el.Loaded += loaded;
        el.Unloaded += unloaded;
        el.SetValue(EntryProperty, new DwellEntry(adapter, loaded, unloaded));

        if (el.IsLoaded) OnLoaded(el, adapter);
    }

    private static void Unregister(FrameworkElement el)
    {
        if (el.GetValue(EntryProperty) is not DwellEntry entry) return;
        el.ClearValue(EntryProperty);
        el.Loaded -= entry.Loaded;
        el.Unloaded -= entry.Unloaded;
        OnUnloaded(el, entry.Adapter);
    }

    private static void OnLoaded(FrameworkElement el, WpfElementAdapter adapter)
    {
        var group = _groups.GetOrAdd(el.Dispatcher, CreateGroup);
        group.Adapters[adapter.Id] = adapter;
        group.Tracker.Add(adapter.Id);
        _core.Client.RegisterAdapter(adapter);
    }

    private static void OnUnloaded(FrameworkElement el, WpfElementAdapter adapter)
    {
        if (_groups.TryGetValue(el.Dispatcher, out var group))
        {
            group.Adapters.TryRemove(adapter.Id, out _);
            group.Tracker.Remove(adapter.Id);
        }
        _core.Client.UnregisterAdapter(adapter.Id);
    }

    private static ThreadGroup CreateGroup(Dispatcher dispatcher)
    {
        ThreadGroup group = null!;
        group = new ThreadGroup(onEmpty: () =>
        {
            if (_groups.TryGetValue(dispatcher, out var g) && g.Tracker.IsEmpty)
            {
                g.Timer.Stop();
                _groups.TryRemove(dispatcher, out _);
            }
        });
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        timer.Tick += (_, _) =>
        {
            foreach (var adapter in group.Adapters.Values)
                _core.Client.OnAdapterLayoutChanged(adapter.Id);
        };
        timer.Start();
        group.Timer = timer;
        return group;
    }
}
