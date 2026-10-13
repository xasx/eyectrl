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
/// inactive window (H2)? Three cases, one at a time, in the fixed order below, each held about 10 s
/// (the whole sitting is about 1 minute):
/// <list type="number">
/// <item>the pre-T13 <c>ResizeMode="CanResize"</c>, set by the TEST on the shipped window;</item>
/// <item><c>NoResize</c>, as shipped;</item>
/// <item><c>NoResize</c> and ACTIVATED — the test only: <c>SetOverlayInteraction(clickThrough: true,
/// focusable: true)</c> drops <c>WS_EX_NOACTIVATE</c>, then <c>Activate()</c>. A background process
/// may be refused by Windows' foreground lock; the log records whether the backdrop actually became
/// <c>GetForegroundWindow()</c>, so a refused activation is reported rather than read.</item>
/// </list>
///
/// <para>
/// <b>The objective read, per case.</b> Step 10's prologue (the App's own DPI scale, the shipped
/// German, the owner's real material read read-only from <c>%APPDATA%\EyeCtrl\settings.json</c>) and
/// the same production show path (<c>Show → ApplyBackdrop → DrawHideButton</c>, ✕ at production's
/// own centre) — plus a readout written to <c>ab-&lt;case&gt;.txt</c> in <c>EYECTRL_AB_LOG_DIR</c>
/// (the run's log directory): <c>TryApplyMica</c>'s result, the window styles, whether the
/// foreground window is the backdrop, and pixel statistics (mean, stddev, exactly-black fraction)
/// of a screen-capture of the central region. The display affinity is lifted IN THE TEST ONLY
/// (<c>SetWindowDisplayAffinity(hwnd, WDA_NONE)</c>) around that capture and restored straight
/// after; the window still excludes itself from every other capture. Classification: black means no
/// material, a uniform dark grey means Mica's flat inactive fallback, spatial variation means the
/// material is live.
/// </para>
///
/// <para>
/// <b>Not a gate.</b> <c>Category=UI</c> (CI's <c>Category!=UI</c> filter excludes it) and skipped
/// unless <c>EYECTRL_LIVE_CHECK=1</c>; run through <c>C:\t\livecheck-scripts\backdrop-ab.ps1</c>
/// only with the owner's go-ahead. Every case proves its window visible and its hint on screen
/// (<see cref="LiveCheck.AssertVisible"/> + <see cref="LiveCheck.AssertShowsText"/>) before holding.
/// No input is simulated, no gaze is fed and the window is click-through (case 3 keeps
/// click-through on), so nothing the owner could do has an effect on the machine. The window closes
/// in a <c>finally</c>.
/// </para>
/// </summary>
[Collection("WpfApplication")]
[Trait("Category", "UI")]
public class BackdropAbLiveCheckTests
{
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(10);

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

            var log = new StringBuilder();
            log.AppendLine($"backdrop A/B case {caseNumber}: {label}");
            log.AppendLine($"Activation: {activation}");
            log.AppendLine(Readout(window, settings));
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

    /// <summary>Everything the A/B wants logged for a case, except the hold.</summary>
    private static string Readout(GlassBackdropWindow window, BackdropSettings settings)
    {
        var sb = new StringBuilder();
        var hwnd = new WindowInteropHelper(window).Handle;

        sb.AppendLine($"Owner's material: type {settings.Type}, opacity {settings.Opacity:0.##}");

        var style = GetWindowLong(hwnd, GwlStyle);
        var exStyle = GetWindowLong(hwnd, GwlExStyle);
        sb.AppendLine($"Window styles: style 0x{style:X8}, exstyle 0x{exStyle:X8}");
        sb.AppendLine(
            $"  WS_THICKFRAME (the resize frame H1 blames): {(style & WsThickFrame) != 0}; "
            + $"WS_EX_NOACTIVATE: {(exStyle & WsExNoActivate) != 0}; "
            + $"WS_EX_TRANSPARENT (click-through): {(exStyle & WsExTransparent) != 0}");

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
        sb.AppendLine(
            $"TryApplyMica: {mica}; raw DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE, Mica) HRESULT: "
            + $"0x{hresult:X8} (0 = accepted)");
        sb.AppendLine($"GetForegroundWindow() == backdrop hwnd: {GetForegroundWindow() == hwnd}");

        var pixels = Capture(window);
        sb.AppendLine(
            $"Capture of the central {pixels.Width}×{pixels.Height} px (every 2nd pixel): "
            + $"mean RGB ({pixels.MeanR:0.0}, {pixels.MeanG:0.0}, {pixels.MeanB:0.0}), "
            + $"luma mean {pixels.MeanLuma:0.00}, stddev {pixels.StdDev:0.00}, "
            + $"exactly black {pixels.BlackFraction:P1}");
        sb.AppendLine($"Classification: {pixels.Verdict}");
        return sb.ToString();
    }

    /// <summary>
    /// Captures the primary screen's central fifth with this window's display affinity lifted in
    /// the test only, and returns the pixel statistics and the verdict.
    /// </summary>
    private static PixelStats Capture(GlassBackdropWindow window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowDisplayAffinity(hwnd, WdaNone);
        try
        {
            var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            var width = bounds.Width / 5;
            var height = bounds.Height / 5;
            using var bitmap = new System.Drawing.Bitmap(width, height);

            long n = 0;
            long black = 0;
            double sum = 0, sumSq = 0, sumR = 0, sumG = 0, sumB = 0;
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    bounds.Left + (bounds.Width - width) / 2, bounds.Top + (bounds.Height - height) / 2,
                    0, 0, new System.Drawing.Size(width, height), System.Drawing.CopyPixelOperation.SourceCopy);
            }

            for (var y = 0; y < height; y += 2)
            {
                for (var x = 0; x < width; x += 2)
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
            var blackFraction = (double)black / n;
            var verdict = blackFraction >= 0.98
                ? "black — no material on the capture"
                : stdDev < 2.0
                    ? "uniform dark — Mica's flat inactive fallback (or a solid fill)"
                    : "spatially varying — wallpaper-correlated variation, Mica is live";
            return new PixelStats(width, height, sumR / n, sumG / n, sumB / n, mean, stdDev, blackFraction, verdict);
        }
        finally
        {
            SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture); // restore for every later capture
        }
    }

    /// <summary>
    /// Writes the case readout so it is in the run's log directory whatever the runner does with
    /// captured console output: <c>EYECTRL_AB_LOG_DIR</c> (the script sets it) or a temp folder.
    /// </summary>
    private static void WriteLog(int caseNumber, string tag, string text)
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

    private sealed record PixelStats(
        int Width,
        int Height,
        double MeanR,
        double MeanG,
        double MeanB,
        double MeanLuma,
        double StdDev,
        double BlackFraction,
        string Verdict);

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
}
