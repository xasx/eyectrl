namespace EyeCtrl.Sdk.Wpf;

using System.Collections.Concurrent;
using System.Windows;
using EyeCtrl.Sdk.Abstractions;

/// <summary>
/// Attached properties enabling gaze dwell on a WPF FrameworkElement.
/// A single process-wide DwellClient connects to EyeCtrl lazily on first use.
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

    private static readonly DwellClient _client = new();
    private static readonly ConcurrentDictionary<FrameworkElement, DwellEntry> _byElement = new();
    private static int _started;
    private static long _idCounter;

    private sealed record DwellEntry(WpfElementAdapter Adapter, EventHandler LayoutHandler);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        if ((bool)e.NewValue) Register(el);
        else Unregister(el);
    }

    private static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) _client.Start();
    }

    private static void Register(FrameworkElement el)
    {
        EnsureStarted();
        if (_byElement.ContainsKey(el)) return;

        var id = $"wpf_{Interlocked.Increment(ref _idCounter)}";
        var adapter = new WpfElementAdapter(id, el, GetMode(el));
        EventHandler layoutHandler = (_, _) => _client.OnAdapterLayoutChanged(adapter.Id);
        var entry = new DwellEntry(adapter, layoutHandler);
        _byElement[el] = entry;

        el.Loaded += OnLoaded;
        el.Unloaded += OnUnloaded;
        el.LayoutUpdated += layoutHandler;

        if (el.IsLoaded) _client.RegisterAdapter(adapter);
    }

    private static void Unregister(FrameworkElement el)
    {
        if (!_byElement.TryRemove(el, out var entry)) return;
        el.Loaded -= OnLoaded;
        el.Unloaded -= OnUnloaded;
        el.LayoutUpdated -= entry.LayoutHandler;
        _client.UnregisterAdapter(entry.Adapter.Id);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && _byElement.TryGetValue(el, out var entry))
            _client.RegisterAdapter(entry.Adapter);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && _byElement.TryGetValue(el, out var entry))
            _client.UnregisterAdapter(entry.Adapter.Id);
    }
}
