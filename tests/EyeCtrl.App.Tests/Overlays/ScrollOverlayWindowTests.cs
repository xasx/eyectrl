namespace EyeCtrl.App.Tests.Overlays;

using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using Xunit;

/// <summary>
/// Constructs a real <c>ScrollOverlayWindow</c> (WPF <c>Window</c> with XAML) without ever calling
/// <c>Show()</c>, mirroring <see cref="GlassBackdropWindowTests"/> — see
/// <see cref="EyeCtrl.App.Tests.Fixtures.WpfApplicationCollection"/> for why this needs the shared
/// collection and the token-resource Application.
/// </summary>
[Collection("WpfApplication")]
public class ScrollOverlayWindowTests
{
    [StaFact]
    public void EscapeHint_IsVisibleWithNonEmptyText()
    {
        // Unlike the backdrop's hint, scroll mode's hint has no show/hide toggle: this window
        // exists only while scroll mode is active (ScrollModeController constructs and closes it
        // around the whole session), so the hint just needs to always render when the window does.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ScrollOverlayWindow();

        var hint = (TextBlock)typeof(ScrollOverlayWindow)
            .GetField("EscapeHint", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

        Check.That(hint.Visibility).IsNotEqualTo(Visibility.Collapsed);
        Check.That(hint.Text).IsNotEmpty();
    }
}
