namespace EyeCtrl.App.Tests.Overlays;

using System.Windows;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Core;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using NFluent;
using Xunit;

/// <summary>
/// B2's activation button, headless and at the user's ScaleX 2.0. The face is a static 100 DIP disc
/// centred on the offer's B; the drawn size is deliberately smaller than the 150 px hit radius, so
/// "drawn = counted" is about the CENTRE, not the size.
///
/// <para>
/// NOTE (test-windows-invisible): none of these tests ever SHOWS the window — <c>ShowAt</c> only
/// positions it and starts the fade (production calls <c>ShowAt</c> and then <c>Show()</c>), so
/// there is nothing to make invisible and NO <see cref="InvisibleTestWindow"/> helper is used. In
/// particular NOT <c>PrepareCloaked</c>: it creates the window's HWND before <c>ShowAt</c>, and a
/// window that already has an HWND stores Left/Top through the REAL monitor's DPI (whole device
/// pixels), while these tests fake the scale with <c>DpiHelper.Initialize</c>. Without an HWND,
/// Left/Top are plain stored doubles and the arithmetic is exact at any real DPI.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class ActivationButtonWindowTests
{
    private static readonly double HitPx = LayoutConstants.ActivationButtonHitRadiusPx;

    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void DrawnCentre_ReadBack_EqualsB_InPhysicalPixels(double scale)
    {
        // The window positions in DIP from a physical B; the read-back converts Left/Top + half the
        // window back to physical pixels, so a DIP/px mix-up on any edge or corner goes red.
        var priorX = DpiHelper.ScaleX;
        var priorY = DpiHelper.ScaleY;
        try
        {
            WpfApplicationGate.EnsureApplicationWithTokens();
            DpiHelper.Initialize(scale, scale);

            foreach (var b in new[]
                     {
                         new PhysicalPoint(0, 0), new PhysicalPoint(3839, 0),
                         new PhysicalPoint(0, 2399), new PhysicalPoint(3839, 2399),
                         new PhysicalPoint(1920, 1200), new PhysicalPoint(1234, 567),
                     })
            {
                var window = new ActivationButtonWindow();
                try
                {
                    window.ShowAt(b, HitPx);
                    var drawnX = DpiHelper.LogicalToPhysicalX(window.Left + (window.Width / 2));
                    var drawnY = DpiHelper.LogicalToPhysicalY(window.Top + (window.Height / 2));
                    Check.WithCustomMessage($"scale {scale}, B=({b.X},{b.Y}): drawn centre ({drawnX},{drawnY})")
                        .That(drawnX).IsCloseTo(b.X, 0.5);
                    Check.That(drawnY).IsCloseTo(b.Y, 0.5);
                }
                finally
                {
                    window.Close();
                }
            }
        }
        finally
        {
            DpiHelper.Initialize(priorX, priorY);
        }
    }

    [StaFact]
    public void TheDisc_IsOneHundredDip_AndTheWindowFitsIt()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ActivationButtonWindow();
        try
        {
            Check.That(ActivationButtonFace.Diameter).IsEqualTo(100.0);
            Check.That(ActivationButtonFace.Diameter).IsEqualTo(2 * LayoutConstants.SharedButtonRadius);
            Check.That(window.Face.Face.Width).IsEqualTo(ActivationButtonFace.Diameter);
            Check.That(window.Face.Face.Height).IsEqualTo(ActivationButtonFace.Diameter);

            window.ShowAt(new PhysicalPoint(1000, 800), HitPx);
            Check.That(window.Width).IsCloseTo(ActivationButtonFace.Diameter + 4.0, 1e-9);
            Check.That(window.Height).IsCloseTo(ActivationButtonFace.Diameter + 4.0, 1e-9);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void HideImmediate_ResetsProgressAndPaused_AndReShowStartsAtZero()
    {
        // A re-show must present an empty disc at the new point, never the previous fill (its last
        // height included) for a frame.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ActivationButtonWindow();
        try
        {
            window.ShowAt(new PhysicalPoint(1000, 800), HitPx);
            window.UpdateDwellProgress(0.7);
            window.SetFillPaused(true);
            Check.That(window.Face.FillHeightDip).IsCloseTo(70.0, 1e-9);
            Check.That(window.Face.Fill.Opacity).IsEqualTo(DwellPausedCue.ProgressOpacity);
            Check.That(window.HasAnimatedProperties).IsTrue();  // the fade-in holds Opacity

            window.HideImmediate();

            Check.That(window.HasAnimatedProperties).IsFalse();
            Check.That(window.Opacity).IsEqualTo(0.0);
            Check.That(window.Face.FillHeightDip).IsEqualTo(0.0);
            Check.That(window.Face.Fill.Opacity).IsEqualTo(1.0);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void SetContent_NamesTheAction_AndDrawsItsGlyph()
    {
        // The word and the glyph both follow the mode the offer was shown in. Mutation: keep one
        // label for both modes → the second assertion goes red.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ActivationButtonWindow();
        try
        {
            Check.That(StringTable.Current.T("overlays.activationButton.label.magnifier"))
                .IsEqualTo(StringTable.EmbeddedEnglish.T("overlays.activationButton.label.magnifier"));
            Check.That(StringTable.EmbeddedEnglish.T("overlays.activationButton.label.magnifier"))
                .IsEqualTo("Magnifier");
            Check.That(StringTable.EmbeddedEnglish.T("overlays.activationButton.label.menu"))
                .IsEqualTo("Menu");

            window.SetContent(ActivationButtonContent.Magnifier);
            Check.That(window.Face.Content).IsEqualTo(ActivationButtonContent.Magnifier);
            Check.That(window.Face.UpperLabel.Text)
                .IsEqualTo(StringTable.Current.T("overlays.activationButton.label.magnifier"));
            Check.That(window.Face.LowerLabel.Text).IsEqualTo(window.Face.UpperLabel.Text);
            Check.That(window.Face.UpperIconStroke).IsNotNull();
            Check.That(window.Face.UpperIconStroke!.Data)
                .IsSameReferenceAs(ActivationIcons.Magnifier.Stroke);
            Check.That(window.Face.UpperIconFill).IsNull();

            window.SetContent(ActivationButtonContent.Menu);
            Check.That(window.Face.Content).IsEqualTo(ActivationButtonContent.Menu);
            Check.That(window.Face.UpperLabel.Text)
                .IsEqualTo(StringTable.Current.T("overlays.activationButton.label.menu"));
            Check.That(window.Face.UpperIconStroke).IsNull();
            Check.That(window.Face.UpperIconFill).IsNotNull();
            Check.That(window.Face.UpperIconFill!.Data)
                .IsSameReferenceAs(ActionIcons.For(ActionIcon.More).Fill);
            Check.That(window.Face.LowerIconFill!.Data)
                .IsSameReferenceAs(ActionIcons.For(ActionIcon.More).Fill);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void TheDisc_IsClickThrough_AndExcludedFromCapture()
    {
        // It sits over the offered word, so it must never become a mouse target over whatever is
        // underneath it — the same contract the cursor ring has.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ActivationButtonWindow();
        try
        {
            Check.That(window.IsClickThrough).IsTrue();
            Check.That(window.ExcludeFromScreenCapture).IsTrue();
        }
        finally
        {
            window.Close();
        }
    }
}
