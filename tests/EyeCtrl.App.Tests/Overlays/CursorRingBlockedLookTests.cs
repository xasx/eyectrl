namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;

/// <summary>
/// C10's blocked ring look. The owner's decision (03.11): the bands are DASHED, never dimmed —
/// C1's lesson is that the ring must not get harder to see — and the cue lives in the window's
/// bands, never in <see cref="System.Windows.Window.Opacity"/> (the pin/quiet channel) and never in
/// view-model state. The window is built headlessly and never shown.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class CursorRingBlockedLookTests
{
    [StaFact]
    public void SetBlocked_DashesTheBands_WithoutChangingStrengthOrWindowOpacity()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(2.0, 2.0);
        var window = new CursorCircleWindow();
        window.UpdateAppearance(new CursorSettings());

        var windowOpacity = window.Opacity;
        var haloOpacity = window.CircleHalo.Opacity;
        var coreOpacity = window.Circle.Opacity;
        var haloThickness = window.CircleHalo.StrokeThickness;
        var coreThickness = window.Circle.StrokeThickness;

        window.SetBlocked(true);

        Check.That(window.IsBlockedLookOn).IsTrue();
        Check.That(window.Circle.StrokeDashArray.Count).IsEqualTo(2);
        Check.That(window.CircleHalo.StrokeDashArray.Count).IsEqualTo(2);
        Check.WithCustomMessage("the blocked look must not dim the owner's pointer (C1 lesson).")
            .That(window.Circle.Opacity).IsEqualTo(coreOpacity);
        Check.WithCustomMessage("the blocked look must not dim the owner's pointer (C1 lesson).")
            .That(window.CircleHalo.Opacity).IsEqualTo(haloOpacity);
        Check.WithCustomMessage("the blocked look must not thin the bands.")
            .That(window.Circle.StrokeThickness).IsEqualTo(coreThickness);
        Check.WithCustomMessage("the blocked look must not thin the bands.")
            .That(window.CircleHalo.StrokeThickness).IsEqualTo(haloThickness);
        Check.WithCustomMessage("Window.Opacity is the pin/quiet channel; a block must never write it.")
            .That(window.Opacity).IsEqualTo(windowOpacity);

        window.SetBlocked(false);

        Check.That(window.IsBlockedLookOn).IsFalse();
        Check.That(window.Circle.StrokeDashArray).IsEmpty();
        Check.That(window.CircleHalo.StrokeDashArray).IsEmpty();
        Check.That(window.Circle.Opacity).IsEqualTo(coreOpacity);
        Check.That(window.Opacity).IsEqualTo(windowOpacity);
    }

    [StaFact]
    public void ASettingsChangeWhileBlocked_KeepsTheBlockedLook()
    {
        // UpdateAppearance re-applies the whole stack; it must re-apply the block on top, or a
        // settings edit mid-block would silently restore a normal-looking ring.
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(2.0, 2.0);
        var window = new CursorCircleWindow();
        window.UpdateAppearance(new CursorSettings());

        window.SetBlocked(true);
        window.UpdateAppearance(new CursorSettings { CircleSize = 180 });

        Check.That(window.IsBlockedLookOn).IsTrue();
        Check.That(window.Circle.StrokeDashArray.Count).IsEqualTo(2);
        Check.That(window.Width).IsEqualTo(180);
    }

    [StaFact]
    public void SettingTheSameStateTwice_WritesNothingTwice()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(1.0, 1.0);
        var window = new CursorCircleWindow();
        window.UpdateAppearance(new CursorSettings());

        window.SetBlocked(true);
        var dashes = window.Circle.StrokeDashArray.Count;
        var opacity = window.Circle.Opacity;
        window.SetBlocked(true);

        Check.That(window.Circle.StrokeDashArray.Count).IsEqualTo(dashes);
        Check.That(window.Circle.Opacity).IsEqualTo(opacity);
    }
}
