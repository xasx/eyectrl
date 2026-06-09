namespace EyeCtrl.Sdk.WinUI;

using System.Collections.Concurrent;
using EyeCtrl.Sdk.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

public static class Dwell
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(Dwell), new PropertyMetadata(false, OnEnabledChanged));

    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
        "Mode", typeof(DwellMode), typeof(Dwell), new PropertyMetadata(DwellMode.Invoke));

    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);
    public static DwellMode GetMode(DependencyObject o) => (DwellMode)o.GetValue(ModeProperty);
    public static void SetMode(DependencyObject o, DwellMode v) => o.SetValue(ModeProperty, v);

    private static readonly DwellClient _client = new();
    private static readonly ConcurrentDictionary<FrameworkElement, Entry> _entries = new();
    private static int _started;
    private static long _idCounter;
    private static DispatcherQueueTimer? _refreshTimer;

    private sealed record Entry(WinUiElementAdapter Adapter,
                                RoutedEventHandler Loaded, RoutedEventHandler Unloaded);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        if ((bool)e.NewValue) Register(el);
        else Unregister(el);
    }

    private static void EnsureStarted(FrameworkElement el)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _client.Start();
        _refreshTimer = el.DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(200);
        _refreshTimer.Tick += (_, _) =>
        {
            foreach (var entry in _entries.Values)
                _client.OnAdapterLayoutChanged(entry.Adapter.Id);
        };
        _refreshTimer.Start();
    }

    private static void Register(FrameworkElement el)
    {
        EnsureStarted(el);
        if (_entries.ContainsKey(el)) return;

        var id = $"winui_{Interlocked.Increment(ref _idCounter)}";
        var adapter = new WinUiElementAdapter(id, el, GetMode(el));

        RoutedEventHandler loaded = (_, _) => _client.RegisterAdapter(adapter);
        RoutedEventHandler unloaded = (_, _) => _client.UnregisterAdapter(id);
        el.Loaded += loaded;
        el.Unloaded += unloaded;

        _entries[el] = new Entry(adapter, loaded, unloaded);
        if (el.IsLoaded) _client.RegisterAdapter(adapter);
    }

    private static void Unregister(FrameworkElement el)
    {
        if (!_entries.TryRemove(el, out var entry)) return;
        el.Loaded -= entry.Loaded;
        el.Unloaded -= entry.Unloaded;
        _client.UnregisterAdapter(entry.Adapter.Id);
    }
}
