namespace EyeCtrl.Sdk.Wpf;

using System.Windows;
using EyeCtrl.Sdk.Abstractions;

/// <summary>
/// Attached properties enabling gaze dwell on a WPF FrameworkElement.
/// A single process-wide DwellClient connects to EyeCtrl lazily on first use.
/// The SDK holds no static strong reference to any element.
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

    private static readonly DwellClient _client = new();
    private static int _started;
    private static long _idCounter;

    private sealed record DwellEntry(WpfElementAdapter Adapter,
                                     RoutedEventHandler Loaded, RoutedEventHandler Unloaded,
                                     EventHandler LayoutHandler);

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
        if (el.GetValue(EntryProperty) is DwellEntry) return;

        var id = $"wpf_{Interlocked.Increment(ref _idCounter)}";
        var adapter = new WpfElementAdapter(id, el, GetMode(el));
        EventHandler layoutHandler = (_, _) => _client.OnAdapterLayoutChanged(adapter.Id);
        RoutedEventHandler loaded = (_, _) => _client.RegisterAdapter(adapter);
        RoutedEventHandler unloaded = (_, _) => _client.UnregisterAdapter(adapter.Id);
        el.SetValue(EntryProperty, new DwellEntry(adapter, loaded, unloaded, layoutHandler));

        el.Loaded += loaded;
        el.Unloaded += unloaded;
        el.LayoutUpdated += layoutHandler;

        if (el.IsLoaded) _client.RegisterAdapter(adapter);
    }

    private static void Unregister(FrameworkElement el)
    {
        if (el.GetValue(EntryProperty) is not DwellEntry entry) return;
        el.ClearValue(EntryProperty);
        el.Loaded -= entry.Loaded;
        el.Unloaded -= entry.Unloaded;
        el.LayoutUpdated -= entry.LayoutHandler;
        _client.UnregisterAdapter(entry.Adapter.Id);
    }
}
