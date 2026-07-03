namespace EyeCtrl.Sdk.Wpf.Tests;

using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using EyeCtrl.Sdk.Wpf;

public class DwellLeakTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateEnabledElement()
    {
        var btn = new Button();
        Dwell.SetEnabled(btn, true);
        return new WeakReference(btn);
    }

    // Loaded registers the adapter with the process-wide DwellClient and the per-dispatcher
    // group; Unloaded (which real WPF raises on visual-tree removal) must remove it from both so
    // the element can be collected. Exercises the registration path the plain-Enabled test skips.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateLoadedThenUnloadedElement()
    {
        var btn = new Button();
        Dwell.SetEnabled(btn, true);
        btn.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, btn));
        btn.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, btn));
        return new WeakReference(btn);
    }

    [StaFact]
    public void EnabledElement_IsCollectable_WhenDropped()
    {
        var wr = CreateEnabledElement();

        for (var i = 0; i < 3 && wr.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Check.That(wr.IsAlive).IsFalse();
    }

    [StaFact]
    public void LoadedThenUnloadedElement_IsCollectable_WhenDropped()
    {
        var wr = CreateLoadedThenUnloadedElement();

        for (var i = 0; i < 3 && wr.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Check.That(wr.IsAlive).IsFalse();
    }
}
