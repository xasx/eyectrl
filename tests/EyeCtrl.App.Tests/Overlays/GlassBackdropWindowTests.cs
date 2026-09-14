namespace EyeCtrl.App.Tests.Overlays;

using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
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
        // EnsureApplicationWithTokens is required as of the escape-hint work below: the XAML now
        // carries {StaticResource} references (BrushTextDim, FontSizeBody) that InitializeComponent
        // resolves at construction time, whereas the window previously held only resource-free
        // Canvas content.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();

        var exception = Record.Exception(() => window.Hide());

        Check.That(exception).IsNull();
    }

    [StaFact]
    public void ShowEscapeHint_MakesTheHintVisible()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();

        window.ShowEscapeHint();

        Check.That(EscapeHintOf(window).Visibility).IsEqualTo(Visibility.Visible);
    }

    [StaFact]
    public void HideEscapeHint_CollapsesTheHint()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        window.ShowEscapeHint();

        window.HideEscapeHint();

        Check.That(EscapeHintOf(window).Visibility).IsEqualTo(Visibility.Collapsed);
    }

    [StaFact]
    public void EscapeHint_IsCollapsedByDefault()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();

        Check.That(EscapeHintOf(window).Visibility).IsEqualTo(Visibility.Collapsed);
    }

    [StaFact]
    public void Hide_AlsoCollapsesTheEscapeHint()
    {
        // A stale hint must not carry over into an unrelated later use of this window (e.g. the
        // plain action menu, which shares the same backdrop but never asks for the hint).
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        window.ShowEscapeHint();

        window.Hide();

        Check.That(EscapeHintOf(window).Visibility).IsEqualTo(Visibility.Collapsed);
    }

    /// <summary>Reflects the XAML-named field rather than driving the window through Show(), which
    /// would create a real HWND and run the user32 P/Invoke in <c>OverlayWindow.OnSourceInitialized</c>.</summary>
    private static TextBlock EscapeHintOf(GlassBackdropWindow window) =>
        (TextBlock)typeof(GlassBackdropWindow)
            .GetField("EscapeHint", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;
}
