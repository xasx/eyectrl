namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Overlays;
using Xunit;

public class GlassBackdropWindowTests
{
    [StaFact]
    public void Hide_DoesNotThrow_WhenConstructedWithoutShowing()
    {
        var window = new GlassBackdropWindow();

        var exception = Record.Exception(() => window.Hide());

        Check.That(exception).IsNull();
    }
}
