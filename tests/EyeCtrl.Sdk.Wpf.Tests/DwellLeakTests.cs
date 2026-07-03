namespace EyeCtrl.Sdk.Wpf.Tests;

using System.Runtime.CompilerServices;
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
}
