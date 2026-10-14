namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using Xunit;

/// <summary>
/// The backdrop Mica A/B — the supervised follow-up to the T13 live check's step 10: does the glass
/// backdrop show plain dark because T13's <c>ResizeMode="NoResize"</c> removed the frame DWM paints
/// Mica through (H1), or because overlays never activate and DWM draws Mica as a flat colour on an
/// inactive window (H2)? Four cases, one at a time, in the fixed order below, each held about 8 s
/// (the whole sitting is about a minute — eight seconds, not ten, so the optional fourth case fits
/// the same budget):
/// <list type="number">
/// <item>the pre-T13 <c>ResizeMode="CanResize"</c>, set by the TEST on the shipped window;</item>
/// <item><c>NoResize</c>, as shipped;</item>
/// <item><c>NoResize</c> and ACTIVATED — the test only: <c>SetOverlayInteraction(clickThrough: true,
/// focusable: true)</c> drops <c>WS_EX_NOACTIVATE</c>, then <c>Activate()</c>;</item>
/// <item><c>CanResize</c> and ACTIVATED — separates "both a frame and activation are needed" from
/// "case 3's activation was refused", and answers whether any configuration shows Mica.</item>
/// </list>
///
/// <para>
/// <b>The objective read, per case (review-01 findings 1 and 2).</b> A BASELINE capture is taken
/// with the display affinity still ON — by construction the desktop behind the window — then the
/// affinity is lifted IN THE TEST ONLY, <c>DwmFlush</c> runs twice (a lift takes effect at DWM's
/// next composition) plus a 150 ms pump, and the capture repeats. If the lifted capture matches the
/// baseline (mean and luma stddev within 0.5), the case is <b>UNREADABLE</b>, not a verdict: the
/// lift did not reach the capture, and reading the desktop behind as "Mica is live" is exactly the
/// failure this protocol exists to prevent. The verdict itself is by COMPARISON: black means no
/// material; a luma stddev near zero is the flat fallback (a fixed grey, whatever lies behind);
/// anything else is live only where the mean moved off the baseline's (Mica follows the wallpaper).
/// Both the central fifth and a sparse 1-in-16 sample of the full screen are logged, and the
/// per-case <c>case-mean-rgb</c> line lets the script compare the cases against each other.
/// </para>
///
/// <para>
/// The case readout goes to <c>ab-&lt;case&gt;.txt</c> in <c>EYECTRL_AB_LOG_DIR</c> (the run's log
/// directory): the baseline and lifted statistics, the comparison, the window styles,
/// <c>GetForegroundWindow()</c>, <c>TryApplyMica</c>'s result and the raw
/// <c>DwmSetWindowAttribute</c> HRESULT — <b>after</b> the capture, so a re-apply cannot disturb
/// it — and the classification. The script prints the review-01 decision table over the four
/// verdicts.
/// </para>
///
/// <para>
/// <b>Not a gate.</b> <c>Category=UI</c> (CI's <c>Category!=UI</c> filter excludes it) and skipped
/// unless <c>EYECTRL_LIVE_CHECK=1</c>; run through <c>C:\t\livecheck-scripts\backdrop-ab.ps1</c>
/// only with the owner's go-ahead. Every case proves its window visible and its hint on screen
/// (<see cref="LiveCheck.AssertVisible"/> + <see cref="LiveCheck.AssertShowsText"/>) before the
/// readout. No input is simulated, no gaze is fed and the window is click-through (cases 3-4 keep
/// click-through on), so nothing the owner could do has an effect on the machine. The window closes
/// in a <c>finally</c>.
/// </para>
///
/// <para>
/// <b>For the briefing.</b> In cases 3 and 4 the backdrop takes keyboard focus for the hold, so
/// anything typed during those seconds (an on-screen keyboard) goes nowhere. And if the owner's own
/// EyeCtrl overlays or Tobii effects draw in the screen's centre, they enter the capture with
/// everything else — the baseline is what tells the backdrop apart from whatever lies behind it.
/// </para>
/// </summary>
[Collection("WpfApplication")]
[Trait("Category", "UI")]
public class BackdropAbLiveCheckTests
{
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(8);

    /// <summary>Case 1 — the pre-T13 resize frame, set by the test on the shipped window (H1's probe).</summary>
    [StaFact]
    public void TheBackdrop_WithCanResize_HoldsAndLogsTheMaterial() =>
        RunCase(1, "canresize", "CanResize", ResizeMode.CanResize, activate: false);

    /// <summary>Case 2 — the shipped window, as production shows it.</summary>
    [StaFact]
    public void TheBackdrop_WithNoResize_HoldsAndLogsTheMaterial() =>
        RunCase(2, "noresize", "NoResize", ResizeMode.NoResize, activate: false);

    /// <summary>
    /// Case 3 — the shipped window ACTIVATED by the test (H2's probe): the activation flags are
    /// dropped and <c>Activate()</c> is called; the readout says whether it took.
    /// </summary>
    [StaFact]
    public void TheBackdrop_WithNoResizeActivated_HoldsAndLogsTheMaterial() =>
        RunCase(3, "noresize-activated", "NoResize + activated", ResizeMode.NoResize, activate: true);

    /// <summary>
    /// Case 4 (review-01, optional) — the frame AND activation. The only case that shows live Mica
    /// if both are required, and the one that separates "both" from a refused activation in case 3.
    /// </summary>
    [StaFact]
    public void TheBackdrop_WithCanResizeActivated_HoldsAndLogsTheMaterial() =>
        RunCase(4, "canresize-activated", "CanResize + activated", ResizeMode.CanResize, activate: true);

    private static void RunCase(int caseNumber, string tag, string label, ResizeMode resizeMode, bool activate)
    {
        Step3LiveCheckTests.BeginShowcaseCase();
        GlassBackdropWindow? window = null;
        try
        {
            var settings = Step3LiveCheckTests.BackdropSettingsOfTheOwner();
            settings.Enabled = true; // the A/B shows the backdrop itself; production skips only when disabled
            var screen = System.Windows.Forms.Screen.PrimaryScreen!;

            // Production's own hide-button layout, restated from ShowBackdropIfEnabled (:107-111).
            var dipRight = screen.Bounds.Width / DpiHelper.ScaleX - UIConstants.BackdropHideButtonOffset;
            var hideButtonCenter = new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(dipRight),
                DpiHelper.LogicalToPhysicalY(UIConstants.BackdropHideButtonOffset));

            window = new GlassBackdropWindow { ResizeMode = resizeMode };
            window.Show();                        // production order: Show, then the material, then the ✕
            window.ApplyBackdrop(settings);
            window.DrawHideButton(hideButtonCenter);
            window.ShowEscapeHint();

            var activation = "not requested (the case shows the shipped no-activate style)";
            if (activate)
            {
                window.SetOverlayInteraction(clickThrough: true, focusable: true);
                activation = window.Activate() ? "Activate() returned true" : "Activate() returned false";
            }

            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(250));
            var name = $"Backdrop A/B case {caseNumber} ({label})";
            LiveCheck.AssertShowsText(window, name, "Mit Esc beenden");
            LiveCheck.AssertVisible(window, name);

            var hwnd = new WindowInteropHelper(window).Handle;
            var baseline = Grab();                     // affinity still ON: the desktop behind
            SetWindowDisplayAffinity(hwnd, WdaNone);   // lift, in the test only
            ScreenSample lifted;
            var flush1 = 0;
            var flush2 = 0;
            try
            {
                flush1 = DwmFlush();                   // a lift reaches composition at the next frame
                flush2 = DwmFlush();
                LiveCheck.PumpFor(TimeSpan.FromMilliseconds(150));
                lifted = Grab();
            }
            finally
            {
                SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture); // restore for every later capture
            }

            var log = new StringBuilder();
            log.AppendLine($"case: {caseNumber} ({label})");
            log.AppendLine($"Activation: {activation}");
            log.AppendLine($"Owner's material: type {settings.Type}, opacity {settings.Opacity:0.##}");

            var style = GetWindowLong(hwnd, GwlStyle);
            var exStyle = GetWindowLong(hwnd, GwlExStyle);
            log.AppendLine($"Window styles: style 0x{style:X8}, exstyle 0x{exStyle:X8}");
            log.AppendLine(
                $"  WS_THICKFRAME (the resize frame H1 blames): {(style & WsThickFrame) != 0}; "
                + $"WS_EX_NOACTIVATE: {(exStyle & WsExNoActivate) != 0}; "
                + $"WS_EX_TRANSPARENT (click-through): {(exStyle & WsExTransparent) != 0}");
            log.AppendLine($"GetForegroundWindow() == backdrop hwnd: {GetForegroundWindow() == hwnd}");

            log.AppendLine($"Baseline (affinity ON; the desktop behind): {Describe(baseline.Centre)}");
            log.AppendLine($"Baseline full screen (1/16): {Describe(baseline.Full)}");
            log.AppendLine($"Capture (affinity lifted, DwmFlush x2 (0x{flush1:X8}, 0x{flush2:X8}) + 150 ms): "
                + Describe(lifted.Centre));
            log.AppendLine($"Capture full screen (1/16): {Describe(lifted.Full)}");
            log.AppendLine(
                $"case-mean-rgb: {lifted.Centre.MeanR:0.0},{lifted.Centre.MeanG:0.0},{lifted.Centre.MeanB:0.0}");
            log.AppendLine(
                $"baseline-mean-rgb: {baseline.Centre.MeanR:0.0},{baseline.Centre.MeanG:0.0},{baseline.Centre.MeanB:0.0}");

            var verdict = Classify(baseline, lifted, log);

            // The material probes run AFTER the capture: a re-apply restarts DWM's material
            // transition, and the capture must see what the hold shows.
            bool mica;
            try
            {
                mica = (bool)typeof(GlassBackdropWindow)
                    .GetMethod("TryApplyMica", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, null)!;
            }
            catch (TargetInvocationException)
            {
                mica = false;
            }

            var micaType = DwmBackdropMica;
            var hresult = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref micaType, sizeof(int));
            log.AppendLine(
                $"TryApplyMica (re-applied after the capture): {mica}; raw "
                + $"DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE, Mica) HRESULT: 0x{hresult:X8} (0 = accepted)");
            log.AppendLine($"Classification: {verdict}");

            WriteLog(caseNumber, tag, log.ToString());
            LiveCheck.PumpFor(Hold);
        }
        finally
        {
            try { window?.Hide(); } catch { /* the window may already be gone */ }
            try { window?.Close(); } catch { /* the window may already be gone */ }
            Step3LiveCheckTests.EndShowcaseCase();
        }
    }

    /// <summary>
    /// The verdict, by comparison (review-01 finding 2): UNREADABLE first when the lifted capture
    /// matches its baseline (finding 1); then black (no material), flat (uniform) or live
    /// (spatially varying, mean moved off the wallpaper's), with the numbers behind each.
    /// </summary>
    internal static string Classify(ScreenSample baseline, ScreenSample lifted, StringBuilder log)
    {
        var centre = lifted.Centre;
        var deltaMean = Math.Max(
            Math.Abs(centre.MeanR - baseline.Centre.MeanR),
            Math.Max(Math.Abs(centre.MeanG - baseline.Centre.MeanG), Math.Abs(centre.MeanB - baseline.Centre.MeanB)));
        var deltaStdDev = Math.Abs(centre.LumaStdDev - baseline.Centre.LumaStdDev);

        if (deltaMean <= 0.5 && deltaStdDev <= 0.5)
        {
            log.AppendLine(
                $"Comparison: the lifted capture matches the baseline (Δmean {deltaMean:0.0##}, Δstddev {deltaStdDev:0.0##}).");
            return "UNREADABLE (the affinity lift did not reach the capture; re-run)";
        }

        log.AppendLine(
            $"Comparison: mean moves from the baseline's ({baseline.Centre.MeanR:0.0}, {baseline.Centre.MeanG:0.0}, "
            + $"{baseline.Centre.MeanB:0.0}) to ({centre.MeanR:0.0}, {centre.MeanG:0.0}, {centre.MeanB:0.0}) "
            + $"(Δmean {deltaMean:0.0}); luma stddev {baseline.Centre.LumaStdDev:0.00} -> {centre.LumaStdDev:0.00}.");

        if (centre.BlackFraction >= 0.98)
            return "black (no material: the Mica path draws no darken fill, so nothing is behind it)";
        if (centre.LumaStdDev < 1.0)
            return "flat (uniform: Mica's inactive fallback or a solid fill — a fixed grey whatever lies behind)";
        return $"live (spatially varying, wallpaper-correlated: the mean moved {deltaMean:0.0} off the baseline's)";
    }

    /// <summary>One capture pair: the central fifth and the sparse full-screen sample.</summary>
    internal sealed record ScreenSample(SampleStats Centre, SampleStats Full);

    /// <summary>One region's statistics over the sampled pixels.</summary>
    internal sealed record SampleStats(
        double MeanR, double MeanG, double MeanB, double LumaMean, double LumaStdDev, double BlackFraction, int Pixels);

    /// <summary>
    /// The primary screen into one bitmap, sampled centrally (every 2nd px) and full-screen (every
    /// 16th). Shared with round 2 (<see cref="BackdropMaterialLiveCheckTests"/>), which reads the
    /// same way.
    /// </summary>
    internal static ScreenSample Grab()
    {
        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        using var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                bounds.Left, bounds.Top, 0, 0,
                new System.Drawing.Size(bounds.Width, bounds.Height), System.Drawing.CopyPixelOperation.SourceCopy);
        }

        var width = bounds.Width / 5;
        var height = bounds.Height / 5;
        var centre = Stats(bitmap, (bounds.Width - width) / 2, (bounds.Height - height) / 2, width, height, step: 2);
        var full = Stats(bitmap, 0, 0, bounds.Width, bounds.Height, step: 16);
        return new ScreenSample(centre, full);
    }

    internal static SampleStats Stats(
        System.Drawing.Bitmap bitmap, int x0, int y0, int width, int height, int step)
    {
        long n = 0;
        long black = 0;
        double sum = 0, sumSq = 0, sumR = 0, sumG = 0, sumB = 0;
        for (var y = y0; y < y0 + height; y += step)
        {
            for (var x = x0; x < x0 + width; x += step)
            {
                var pixel = bitmap.GetPixel(x, y);
                var luma = 0.2126 * pixel.R + 0.7152 * pixel.G + 0.0722 * pixel.B;
                n++;
                sum += luma;
                sumSq += luma * luma;
                sumR += pixel.R;
                sumG += pixel.G;
                sumB += pixel.B;
                if (pixel.R == 0 && pixel.G == 0 && pixel.B == 0) black++;
            }
        }

        var mean = sum / n;
        var stdDev = Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));
        return new SampleStats(sumR / n, sumG / n, sumB / n, mean, stdDev, (double)black / n, (int)n);
    }

    internal static string Describe(SampleStats stats) =>
        $"mean RGB ({stats.MeanR:0.0}, {stats.MeanG:0.0}, {stats.MeanB:0.0}), luma mean {stats.LumaMean:0.00}, "
        + $"stddev {stats.LumaStdDev:0.00}, exactly black {stats.BlackFraction:P1} over {stats.Pixels} px";

    /// <summary>
    /// Writes the case readout so it is in the run's log directory whatever the runner does with
    /// captured console output: <c>EYECTRL_AB_LOG_DIR</c> (the script sets it) or a temp folder.
    /// Shared with round 2.
    /// </summary>
    internal static void WriteLog(int caseNumber, string tag, string text)
    {
        var directory = Environment.GetEnvironmentVariable("EYECTRL_AB_LOG_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Path.GetTempPath(), "eyectrl-backdrop-ab");
        System.IO.Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"ab-{caseNumber}-{tag}.txt");
        File.WriteAllText(path, text);
        Console.WriteLine($"[backdrop A/B] case {caseNumber} readout written to {path}");
        Console.WriteLine(text);
    }

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsThickFrame = 0x00040000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmBackdropMica = 2;
    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
