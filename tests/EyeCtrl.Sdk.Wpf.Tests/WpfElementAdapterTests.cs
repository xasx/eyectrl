namespace EyeCtrl.Sdk.Wpf.Tests;

using System.Windows.Controls;
using EyeCtrl.Sdk.Abstractions;
using EyeCtrl.Sdk.Wpf;

public class WpfElementAdapterTests
{
    [StaFact]
    public void Mode_ReflectsAttachedPropertyChanges_AfterRegistration()
    {
        var btn = new Button();
        Dwell.SetMode(btn, DwellMode.Focus);
        var adapter = new WpfElementAdapter("t1", btn);

        Check.That(adapter.Mode).IsEqualTo(DwellMode.Focus);

        Dwell.SetMode(btn, DwellMode.Invoke);
        Check.That(adapter.Mode).IsEqualTo(DwellMode.Invoke);
    }
}
