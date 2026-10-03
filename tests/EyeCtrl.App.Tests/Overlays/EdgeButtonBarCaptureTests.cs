namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Overlays;
using NFluent;
using Xunit;

/// <summary>
/// Capture hygiene for the shared edge-button bar. The bar parks the action menu's own
/// Close/Help targets and the manual-selection exits on the same window, so it can be on
/// screen at the exact moment the action-menu preview or the magnifier grabs the screen —
/// without <see cref="OverlayWindow.ExcludeFromScreenCapture"/> the user's exits are baked
/// into the captured image as ghosts over the real content.
///
/// <para>
/// Construction-only, like <c>OverlayFontTests.ADerivedWindow_InheritsTheFontFamilyFromOverlayWindow</c>:
/// EdgeButtonBarWindow's XAML has no <c>{StaticResource}</c> lookups, so it constructs cleanly
/// with no Application and needs no WpfApplicationGate, and <c>ExcludeFromScreenCapture</c>'s
/// Win32 interop only runs in <c>OnSourceInitialized</c> — the property read here is the whole
/// claim, with no Show() and no app process.
/// </para>
/// </summary>
public class EdgeButtonBarCaptureTests
{
    [StaFact]
    public void EdgeButtonBar_IsExcludedFromScreenCapture()
    {
        // Follow-up 3's verification: the preview must never bake the edge bars in.
        Check.That(new EdgeButtonBarWindow().ExcludeFromScreenCapture).IsTrue();
    }
}
