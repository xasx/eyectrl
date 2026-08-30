namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Overlays;
using Xunit;

/// <summary>
/// Constructs a real <c>GlassBackdropWindow</c> (WPF <c>Window</c> with XAML), so it must be
/// serialised against the process-wide <c>Application</c> singleton the same way every other
/// window-constructing class in this project is — see
/// <see cref="EyeCtrl.App.Tests.Fixtures.WpfApplicationCollection"/>. Missing this attribute is
/// what let <see cref="Hide_DoesNotThrow_WhenConstructedWithoutShowing"/> fail intermittently in a
/// full-suite run while passing 5/5 in isolation: <c>Xunit.StaFact</c> spins a fresh STA thread
/// per test, and without the collection this class can race another window-constructing test for
/// the <c>Application.Current</c> singleton.
/// </summary>
[Collection("WpfApplication")]
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
