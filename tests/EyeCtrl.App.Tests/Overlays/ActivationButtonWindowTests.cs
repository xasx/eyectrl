namespace EyeCtrl.App.Tests.Overlays;

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Core;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using NFluent;
using Xunit;
using SelectionMode = EyeCtrl.Core.Models.SelectionMode;

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

    [StaTheory]
    [InlineData(SelectionMode.ManualSelection)]
    [InlineData(SelectionMode.AutoSelection)]
    [InlineData(SelectionMode.None)]
    public void EverySelectionMode_DrawsTheOneRingMenuSign_AndNoText(SelectionMode mode)
    {
        // F1: the owner's sign in every selection mode — six open circles around a filled centre
        // dot — and no word at all. The face takes no mode argument any more; running the face once
        // per mode pins that the same sign is what each one gets.
        // Mutation: the old magnifier back → the stroke data changes; the label back → a TextBlock
        // appears in the tree.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ActivationButtonWindow();
        try
        {
            Check.WithCustomMessage($"{mode}: the face lost its ring-menu stroke")
                .That(window.Face.UpperIconStroke).IsNotNull();
            Check.That(window.Face.UpperIconStroke!.Data)
                .IsSameReferenceAs(ActivationIcons.RingMenu.Stroke);
            Check.That(window.Face.UpperIconFill).IsNotNull();
            Check.That(window.Face.UpperIconFill!.Data)
                .IsSameReferenceAs(ActivationIcons.RingMenu.Fill);
            Check.That(window.Face.LowerIconStroke!.Data)
                .IsSameReferenceAs(ActivationIcons.RingMenu.Stroke);
            Check.That(window.Face.LowerIconFill!.Data)
                .IsSameReferenceAs(ActivationIcons.RingMenu.Fill);

            // The face is the tree root: this window is never shown, so its own visual tree is not
            // connected yet; the face's children are.
            Check.WithCustomMessage("the activation button draws a text element; F1 removed the word")
                .That(Descendants(window.Face).OfType<TextBlock>().ToList()).IsEmpty();
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void TheWindow_CarriesTheActionMenuAccessibleName()
    {
        // F1 removed the visible word; the sign still needs a name for UIA. Mutation: drop the
        // AutomationProperties.SetName → the window has no name.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ActivationButtonWindow();
        try
        {
            Check.That(StringTable.EmbeddedEnglish.T("overlays.activationButton.name"))
                .IsEqualTo("Action menu");
            Check.That(System.Windows.Automation.AutomationProperties.GetName(window))
                .IsEqualTo(StringTable.Current.T("overlays.activationButton.name"));
            Check.That(ShippedGerman.Load().T("overlays.activationButton.name"))
                .IsEqualTo("Aktionsmenü");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Every visual descendant of <paramref name="root"/>.</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
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
