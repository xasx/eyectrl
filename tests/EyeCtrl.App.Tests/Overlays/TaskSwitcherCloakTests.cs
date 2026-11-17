namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Settings;
using EyeCtrl.Platform.Icons;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
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
/// Nothing here may paint the window. The fixture's <see cref="InvisibleTestWindow.PrepareCloaked"/>
/// anchors the product's ONE-WAY <c>KeepCloakedForTests</c> seam, and the seam sits at the single
/// <c>WriteCloak</c> site: every uncloak REQUEST (rendered frame, 250 ms fallback, hide) is recorded
/// and never reaches DWM. No test turns that seam off, and a source pin below fails if a second
/// cloak write site appears. The show-path gates also read the cloak back as <c>true</c> before any
/// show and skip otherwise.
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

    /// <summary>
    /// Review fixup-01 (finding 1): the cloak write site is STRUCTURAL. <c>DwmwaCloak</c> may be
    /// named only in its declaration and inside <c>WriteCloak</c>; the dwmapi P/Invoke may be called
    /// only from the seam's <c>NativeSetDwmAttribute</c>; and <c>WriteCloak</c> must go through the
    /// injectable seam, which the write-point test seam sits on.
    /// </summary>
    [Fact]
    public void TheCloakAttribute_HasExactlyOneWriteSite()
    {
        var path = Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Overlays", "TaskSwitcherWindow.xaml.cs");
        var source = SourceScanner.StripComments(File.ReadAllText(path));

        const string signature = "private void WriteCloak(bool cloaked)";
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Check.WithCustomMessage("TaskSwitcherWindow has no WriteCloak(bool cloaked): the cloak write site moved")
            .That(start).IsStrictlyGreaterThan(-1);
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Check.That(end).IsStrictlyGreaterThan(start);
        var body = source[start..end];
        var outside = source.Remove(start, end - start);

        Check.WithCustomMessage(
                "DwmwaCloak is named outside its declaration and WriteCloak — a second cloak write site " +
                "would bypass the one-way test seam. Route it through WriteCloak.")
            .That(Regex.Matches(outside, @"\bDwmwaCloak\b").Count).IsEqualTo(1);   // the const declaration
        Check.WithCustomMessage("WriteCloak must name the cloak attribute exactly once")
            .That(Regex.Matches(body, @"\bDwmwaCloak\b").Count).IsEqualTo(1);
        Check.WithCustomMessage("WriteCloak must write through the injectable SetDwmAttribute seam")
            .That(Regex.IsMatch(body, @"SetDwmAttribute\s*\(\s*hwnd\s*,\s*DwmwaCloak\s*,\s*cloaked\s*\)")).IsTrue();

        // The dwmapi P/Invoke is the seam's default only: its extern declaration plus ONE call in
        // NativeSetDwmAttribute. A direct call anywhere else is a second write path.
        Check.WithCustomMessage(
                "DwmSetWindowAttribute is called somewhere other than NativeSetDwmAttribute — a second " +
                "DWM write path bypasses the write-point seam")
            .That(Regex.Matches(source, @"\bDwmSetWindowAttribute\s*\(").Count).IsEqualTo(2);
        var nativeStart = source.IndexOf("private static int NativeSetDwmAttribute", StringComparison.Ordinal);
        Check.That(nativeStart).IsStrictlyGreaterThan(-1);
        var nativeBody = source[nativeStart..source.IndexOf("\n    }", nativeStart, StringComparison.Ordinal)];
        Check.WithCustomMessage("the single dwmapi call must be in NativeSetDwmAttribute")
            .That(Regex.Matches(nativeBody, @"\bDwmSetWindowAttribute\s*\(").Count).IsEqualTo(1);
    }

    [StaFact]
    public void ShowFullScreen_CloaksBeforeTheShow_AndRequestsTheUncloakOnlyAfterARenderedFrame()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new TaskSwitcherWindow();
        using var viewModel = new TaskSwitcherViewModel(
            windowManager: null!, new EyeCtrlSettings(), new WeakReferenceMessenger());
        try
        {
            InvisibleTestWindow.PrepareCloaked(window);   // anchors the one-way seam

            // SAFETY GATE, before any show: only a window DWM actually reports as cloaked may be
            // shown. No composition / no cloak => skip; showing it would paint a full-screen
            // non-layered window on the owner's screen.
            var prepared = InvisibleTestWindow.IsCloaked(window);
            Assert.SkipWhen(prepared is null, "DWM did not report the cloak state (no composition): refusing to show the switcher.");
            Assert.SkipUnless(prepared == true, "DWM did not cloak the prepared switcher: refusing to paint a full-screen non-layered window.");

            // A VM (with no windows) makes UpdateTilePositions run its whole body, so the order pin
            // sees the real target-registration pass. The fake resolves no icons.
            window.Configure(viewModel, new NoIcons());

            var sequence = new List<string>();
            var realSetDwm = window.SetDwmAttribute;
            window.SetDwmAttribute = (hwnd, attribute, value) =>
            {
                if (attribute == DwmaCloak)
                    sequence.Add(value ? "cloak:true" : "cloak:false");
                return realSetDwm(hwnd, attribute, value);
            };
            var realFlush = window.FlushDwm;
            window.FlushDwm = () =>
            {
                sequence.Add("flush");
                realFlush();
            };
            window.UncloakSuppressedForTests += () => sequence.Add("uncloak-request");
            window.TilePositionsUpdatedForTests += () => sequence.Add("tile-positions");
            EventHandler onRendering = (_, _) => sequence.Add("rendering");
            CompositionTarget.Rendering += onRendering;
            try
            {
                window.ShowFullScreen(toolbarKeepOut: null);

                // The show's cloak goes up while the window is still hidden, and the seam has
                // already suppressed nothing: an uncloak request here would defeat the first frame.
                Check.WithCustomMessage($"the show did not cloak before the first frame: {string.Join(" | ", sequence)}")
                    .That(sequence).Contains("cloak:true");
                Check.WithCustomMessage($"the show uncloaked immediately: {string.Join(" | ", sequence)}")
                    .That(sequence).Not.Contains("cloak:false");
                Check.WithCustomMessage($"the show requested the uncloak before any frame: {string.Join(" | ", sequence)}")
                    .That(sequence).Not.Contains("uncloak-request");

                LiveCheck.PumpFor(TimeSpan.FromMilliseconds(400));

                var cloakTrue = sequence.IndexOf("cloak:true");
                var tilePositions = sequence.IndexOf("tile-positions");
                var frameAfterCloak = sequence.FindIndex(cloakTrue + 1, item => item == "rendering");
                var flush = sequence.IndexOf("flush");
                var uncloakRequest = sequence.IndexOf("uncloak-request");

                Check.WithCustomMessage($"unexpected sequence: {string.Join(" | ", sequence)}")
                    .That(cloakTrue).IsStrictlyGreaterThan(-1);
                Check.WithCustomMessage($"UpdateTilePositions never ran, so the dwell targets were not checked: {string.Join(" | ", sequence)}")
                    .That(tilePositions).IsStrictlyGreaterThan(cloakTrue);
                Check.WithCustomMessage($"no frame rendered between the cloak and the flush: {string.Join(" | ", sequence)}")
                    .That(frameAfterCloak).IsStrictlyGreaterThan(-1);
                Check.WithCustomMessage($"the flush came before a frame rendered: {string.Join(" | ", sequence)}")
                    .That(flush).IsStrictlyGreaterThan(frameAfterCloak);
                Check.WithCustomMessage($"the product never requested the uncloak after the flush: {string.Join(" | ", sequence)}")
                    .That(uncloakRequest).IsStrictlyGreaterThan(flush);
                Check.WithCustomMessage(
                        "dwell-target registration must run BEFORE the uncloak request, or a visible switcher " +
                        $"shows tiles with no targets: {string.Join(" | ", sequence)}")
                    .That(tilePositions).IsStrictlyLessThan(uncloakRequest);

                // The seam suppressed the write: the recorder never saw an uncloak, and DWM still
                // reports the window cloaked.
                Check.WithCustomMessage($"a cloak write escaped the seam: {string.Join(" | ", sequence)}")
                    .That(sequence).Not.Contains("cloak:false");
                Check.That(window.SuppressedUncloakRequestsForTests).IsEqualTo(1);
            }
            finally
            {
                CompositionTarget.Rendering -= onRendering;
            }

            Check.WithCustomMessage("the write-point seam failed — the real switcher is no longer cloaked")
                .That(InvisibleTestWindow.IsCloaked(window) == true).IsTrue();
            Check.That(window.IsVisible).IsTrue();
        }
        finally
        {
            try { window.HideOverlay(); } catch { /* an assertion may have failed mid-show */ }
            window.Close();
        }
    }

    /// <summary>
    /// Review fixup-01 (finding 3, mutant h): if the rendered-frame signal never comes, the 250 ms
    /// fallback must still request the uncloak. The product's rendered-frame trigger is not armed
    /// (<c>SkipRenderedFrameTriggerForTests</c>), so the fallback timer is the only path left. The
    /// seam records the request; DWM is never uncloaked.
    /// </summary>
    [StaFact]
    public void The250msFallback_RequestsTheUncloak_WhenNoFrameIsRendered()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new TaskSwitcherWindow();
        try
        {
            InvisibleTestWindow.PrepareCloaked(window);

            var prepared = InvisibleTestWindow.IsCloaked(window);
            Assert.SkipWhen(prepared is null, "DWM did not report the cloak state (no composition): refusing to show the switcher.");
            Assert.SkipUnless(prepared == true, "DWM did not cloak the prepared switcher: refusing to paint a full-screen non-layered window.");

            // The render trigger must NOT be the path that fires: with it disarmed, only the
            // 250 ms fallback can record the uncloak request.
            window.SkipRenderedFrameTriggerForTests = true;

            var sequence = new List<string>();
            var realSetDwm = window.SetDwmAttribute;
            window.SetDwmAttribute = (hwnd, attribute, value) =>
            {
                if (attribute == DwmaCloak)
                    sequence.Add(value ? "cloak:true" : "cloak:false");
                return realSetDwm(hwnd, attribute, value);
            };
            var realFlush = window.FlushDwm;
            window.FlushDwm = () =>
            {
                sequence.Add("flush");
                realFlush();
            };
            window.UncloakSuppressedForTests += () => sequence.Add("uncloak-request");

            window.ShowFullScreen(toolbarKeepOut: null);
            Check.That(sequence).Contains("cloak:true");

            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(600));

            Check.WithCustomMessage($"the 250 ms fallback never flushed: {string.Join(" | ", sequence)}")
                .That(sequence).Contains("flush");
            Check.WithCustomMessage($"the 250 ms fallback never requested the uncloak: {string.Join(" | ", sequence)}")
                .That(sequence).Contains("uncloak-request");
            Check.WithCustomMessage($"the fallback's request escaped the seam: {string.Join(" | ", sequence)}")
                .That(sequence).Not.Contains("cloak:false");
            Check.That(window.SuppressedUncloakRequestsForTests).IsGreaterOrEqualThan(1);

            Check.WithCustomMessage("the fallback path uncloaked the real window")
                .That(InvisibleTestWindow.IsCloaked(window) == true).IsTrue();
        }
        finally
        {
            try { window.HideOverlay(); } catch { /* an assertion may have failed mid-show */ }
            window.Close();
        }
    }

    private sealed class NoIcons : IAppIconProvider
    {
        /// <summary>No window tiles in these gates, so this is never asked; never null in production either.</summary>
        public ImageSource GetIcon(IntPtr hwnd, uint processId) => null!;
    }
}
