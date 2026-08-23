namespace EyeCtrl.App.Tests.Settings;

using EyeCtrl.App.Views.Settings;
using NFluent;
using Xunit;

public class SettingsRowLayoutTests
{
    [Fact]
    public void ARowSpec_CarriesAnOptionalDescription()
    {
        var without = new ToggleSpec("t", "Start Paused", () => false, _ => { });
        var with = new ToggleSpec("t", "Start Paused", () => false, _ => { })
        {
            Description = "EyeCtrl starts with tracking suspended."
        };

        Check.That(without.Description).IsNull();
        Check.That(with.Description).IsEqualTo("EyeCtrl starts with tracking suspended.");
    }
}
