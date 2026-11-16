namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Windows.Interop;
using System.Windows.Media;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Tests.TestSupport;
using NFluent;
using Xunit;

/// <summary>
/// S4-B: the task switcher is NON-layered (<c>AllowsTransparency=False</c> is what lets DWM
/// thumbnails render into it), so two layered-window assumptions had to change:
///
/// <para>
/// (1) Its background is the theme's SurfaceDark at FULL alpha. A layered window composites the
/// palette's 0xFA alpha against the desktop; a non-layered one composites it against black, so the
/// rendered surface would be darker than the palette says. (2) Its first frame is hidden by a DWM
/// cloak that the show path lifts only after WPF has rendered, because a non-layered window can
/// expose an unpainted first frame (the <c>GlassBackdropWindow</c> precedent).
/// </para>
///
/// <para>
/// Nothing here may paint the window. The show-path gate runs the window CLOAKED:
/// <see cref="InvisibleTestWindow.PrepareCloaked"/> cloaks it, the test reads the cloak back as
/// <c>true</c> before any show and SKIPS otherwise, and the <c>SetDwmAttribute</c> recorder is a
/// SAFETY LATCH — it records every cloak write but forwards it to DWM as <c>true</c>, so even the
/// product's own uncloak request cannot make the real window visible.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class TaskSwitcherCloakTests
{
    private const int DwmaCloak = 13;

    [StaFact]
    public void TaskSwitcher_IsNotLayered_AndPaintsTheOpaqueThemeSurface()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = InvisibleTestWindow.PrepareCloaked(new TaskSwitcherWindow());
        try
        {
            Check.WithCustomMessage(
                    "TaskSwitcherWindow must be NON-layered (AllowsTransparency=False): DWM thumbnails " +
                    "do not render into a layered window.")
                .That(window.AllowsTransparency).IsFalse();

            Check.That(window.Background).IsInstanceOf<SolidColorBrush>();
            var background = (SolidColorBrush)window.Background;
            var expected = Tokens.BrushSurfaceDarkOpaque.Color;

            Check.WithCustomMessage(
                    $"the switcher background is {background.Color}; a non-layered window must paint the " +
                    $"theme's SurfaceDark RGB at full alpha, which is {expected}.")
                .That(background.Color).IsEqualTo(expected);
            Check.WithCustomMessage("the switcher's window background must be fully opaque")
                .That(background.Color.A).IsEqualTo(byte.MaxValue);
            Check.WithCustomMessage("the opaque background must keep the theme's SurfaceDark RGB")
                .That((background.Color.R, background.Color.G, background.Color.B))
                .IsEqualTo((Tokens.SurfaceDark.R, Tokens.SurfaceDark.G, Tokens.SurfaceDark.B));
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void ShowFullScreen_CloaksBeforeTheShow_AndUncloaksOnlyAfterARenderedFrame()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new TaskSwitcherWindow();
        try
        {
            InvisibleTestWindow.PrepareCloaked(window);

            // SAFETY GATE, before any show: only a window DWM actually reports as cloaked may be
            // shown. No composition / no cloak => skip; showing it would paint a full-screen
            // non-layered window on the owner's screen.
            var prepared = InvisibleTestWindow.IsCloaked(window);
            Assert.SkipWhen(prepared is null, "DWM did not report the cloak state (no composition): refusing to show the switcher.");
            Assert.SkipUnless(prepared == true, "DWM did not cloak the prepared switcher: refusing to paint a full-screen non-layered window.");

            // The PRODUCT's uncloak must run for this gate (that is the half being pinned), so the
            // fixture's stay-cloaked seam is switched off — and the recorder below is the safety
            // latch that keeps the REAL window cloaked: every cloak write is forwarded as true.
            window.StayCloakedForTests = false;
            var sequence = new List<string>();
            var realSetDwm = window.SetDwmAttribute;
            window.SetDwmAttribute = (hwnd, attribute, value) =>
            {
                if (attribute != DwmaCloak)
                    return realSetDwm(hwnd, attribute, value);

                sequence.Add(value ? "cloak:true" : "cloak:false");
                return realSetDwm(hwnd, attribute, true);   // SAFETY LATCH: never deliver an uncloak
            };
            var realFlush = window.FlushDwm;
            window.FlushDwm = () =>
            {
                sequence.Add("flush");
                realFlush();
            };
            EventHandler onRendering = (_, _) => sequence.Add("rendering");
            CompositionTarget.Rendering += onRendering;
            try
            {
                window.ShowFullScreen(toolbarKeepOut: null);

                // The show itself only ever writes cloak(true) while the window is still hidden;
                // an uncloak here would defeat the first-frame protection.
                Check.WithCustomMessage($"the show did not cloak before the first frame: {string.Join(" | ", sequence)}")
                    .That(sequence).Contains("cloak:true");
                Check.WithCustomMessage($"the show uncloaked immediately: {string.Join(" | ", sequence)}")
                    .That(sequence).Not.Contains("cloak:false");

                LiveCheck.PumpFor(TimeSpan.FromMilliseconds(400));

                var cloakTrue = sequence.IndexOf("cloak:true");
                var frameAfterCloak = sequence.FindIndex(cloakTrue + 1, item => item == "rendering");
                var flush = sequence.IndexOf("flush");
                var uncloakRequest = sequence.IndexOf("cloak:false");

                Check.WithCustomMessage($"unexpected sequence: {string.Join(" | ", sequence)}")
                    .That(cloakTrue).IsStrictlyGreaterThan(-1);
                Check.WithCustomMessage($"no frame rendered between the cloak and the flush: {string.Join(" | ", sequence)}")
                    .That(frameAfterCloak).IsStrictlyGreaterThan(-1);
                Check.WithCustomMessage($"the flush came before a frame rendered: {string.Join(" | ", sequence)}")
                    .That(flush).IsStrictlyGreaterThan(frameAfterCloak);
                Check.WithCustomMessage($"the product never requested the uncloak after the flush: {string.Join(" | ", sequence)}")
                    .That(uncloakRequest).IsStrictlyGreaterThan(flush);
            }
            finally
            {
                CompositionTarget.Rendering -= onRendering;
            }

            // The latch held: the product asked DWM to lift the cloak, and the real window is still
            // cloaked. This is the property that makes the gate safe on the owner's machine.
            Check.WithCustomMessage("the safety latch failed — the real switcher is no longer cloaked")
                .That(InvisibleTestWindow.IsCloaked(window) == true).IsTrue();
            Check.That(window.IsVisible).IsTrue();
        }
        finally
        {
            try { window.HideOverlay(); } catch { /* the assertion above may have failed mid-show */ }
            window.Close();
        }
    }
}
