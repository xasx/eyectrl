namespace EyeCtrl.Platform.Tests.Services;

using EyeCtrl.Platform.Services;

/// <summary>
/// The Run value name must be injectable, because every launch calls <c>SetStartOnBoot</c> and a
/// <c>false</c> deletes the value — which is how a scoped test run would otherwise remove the
/// default instance's autostart entry.
/// </summary>
public class WindowsStartupRegistrarNameTests
{
    [Fact]
    public void ReportsItsDefaultValueName()
    {
        var registrar = new WindowsStartupRegistrar();

        Check.That(registrar.EffectiveValueName).IsEqualTo("EyeCtrl");
        Check.That(WindowsStartupRegistrar.DefaultValueName).IsEqualTo("EyeCtrl");
    }

    [Fact]
    public void ReportsAnInjectedValueName()
    {
        var registrar = new WindowsStartupRegistrar("EyeCtrl_run7");

        Check.That(registrar.EffectiveValueName).IsEqualTo("EyeCtrl_run7");
    }

    [Fact]
    public void IsRegistered_ReadsTheInjectedValueName_NotTheDefault()
    {
        // The live "EyeCtrl" value may or may not exist on this box, so asserting on it would be
        // environment-dependent. A GUID-suffixed name certainly does not exist, which proves the
        // read honours the injected name without touching the real autostart entry.
        var registrar = new WindowsStartupRegistrar("EyeCtrl_absent_" + Guid.NewGuid().ToString("N"));

        Check.That(registrar.IsRegistered).IsFalse();
    }
}
