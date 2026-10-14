namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shapes;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using Xunit;

/// <summary>
/// The opacity calibration sweep (review-07's revision): the live look's case-1 capture kept 95 % of
/// the desktop's contrast (a real blur collapses it — round 2's r5 went from stddev 39.5 to 6.9), so
/// "the accent dims non-linearly" was unproven. Ten cases, ~5 s each, never activated, all through
/// the accent API:
/// <list type="number">
/// <item><b>C0</b> and <b>C1</b> (controls, FIRST): a fresh window with production's transparent CT
/// and <c>TryApplyAcrylic</c> called DIRECTLY — 0.004 (r5's exact gradient, no
/// <c>ClearContent</c>/<c>DisableAccent</c> first) and 0.30. C1 blurred while production's 0.30 is
/// sharp means production's disable-then-enable kills the blur; both sharp means the alpha
/// suppresses it.</item>
/// <item>the blur (type Mica, production <c>ApplyBackdrop</c>) at <b>0.30 / 0.50 / 0.70 / 0.90 /
/// 1.00</b>;</item>
/// <item>the transparent gradient (type Solid darken, state 2, flags 0x20) at <b>0.30 / 0.60</b>,
/// and one comparison at 0.30 with <b>flags 2</b> (the community convention for the gradient), via a
/// test-native call.</item>
/// </list>
///
/// <para>
/// Every case runs the rounds-1/2 capture protocol (baseline with the affinity ON, lift,
/// <c>DwmFlush</c> ×2 + 150 ms, capture) and writes invariant machine lines the script reads:
/// <c>kind:</c> (control / blur / gradient), <c>state:</c>, <c>flags:</c>, <c>alpha:</c>,
/// <c>rectangles:</c>, <c>accent:</c>, <c>stddev-ratio:</c> (lifted ÷ baseline; a blur ≈ 0.2, the
/// sharp desktop ≈ 0.9+), <c>black-delta:</c>, <c>baseline-luma:</c>, <c>ratio-r/g/b:</c>,
/// <c>ratio-luma:</c>, <c>dim:</c>. <c>backdrop-calibrate.ps1</c> decides blurred vs sharp per case,
/// warns on a non-monotone curve, and interpolates the alpha for a 0.70 ratio over the blurred
/// cases only.
/// </para>
///
/// <para>
/// <b>Not a gate.</b> <c>Category=UI</c> and skipped unless <c>EYECTRL_LIVE_CHECK=1</c>; run through
/// <c>C:\t\livecheck-scripts\backdrop-calibrate.ps1</c> with the pinned copy <c>C:\t\lc-bd4</c>,
/// only with the owner's go-ahead. Keep the screen still: the blur's luminosity layer is additive, so
/// its ratio depends on the content's brightness. No input is simulated and no gaze is fed; the
/// window closes in a <c>finally</c>.
/// </para>
/// </summary>
[Collection("WpfApplication")]
[Trait("Category", "UI")]
public class BackdropCalibrationLiveCheckTests
{
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(5);

    /// <summary>C0 — a fresh window, the accent at r5's exact alpha, applied directly.</summary>
    [StaFact]
    public void TheControl_DirectAccentAtR5sAlpha_LogsItsDim() =>
        RunCase(1, "c0-direct-0004", "control", "C0: direct TryApplyAcrylic(0.004)", BackdropType.Mica, 0.004,
            direct: true, flags: 32);

    /// <summary>C1 — the same direct call at 0.30.</summary>
    [StaFact]
    public void TheControl_DirectAccentAt030_LogsItsDim() =>
        RunCase(2, "c1-direct-030", "control", "C1: direct TryApplyAcrylic(0.30)", BackdropType.Mica, 0.30,
            direct: true, flags: 32);

    /// <summary>Case 3 — the blur at 0.30, production's own path.</summary>
    [StaFact]
    public void TheBlur_AtAlpha030_LogsItsDim() =>
        RunCase(3, "blur-a030", "blur", "blur, alpha 0.30", BackdropType.Mica, 0.30, direct: false, flags: 32);

    /// <summary>Case 4 — the blur at 0.50.</summary>
    [StaFact]
    public void TheBlur_AtAlpha050_LogsItsDim() =>
        RunCase(4, "blur-a050", "blur", "blur, alpha 0.50", BackdropType.Mica, 0.50, direct: false, flags: 32);

    /// <summary>Case 5 — the blur at 0.70.</summary>
    [StaFact]
    public void TheBlur_AtAlpha070_LogsItsDim() =>
        RunCase(5, "blur-a070", "blur", "blur, alpha 0.70", BackdropType.Mica, 0.70, direct: false, flags: 32);

    /// <summary>Case 6 — the blur at 0.90.</summary>
    [StaFact]
    public void TheBlur_AtAlpha090_LogsItsDim() =>
        RunCase(6, "blur-a090", "blur", "blur, alpha 0.90", BackdropType.Mica, 0.90, direct: false, flags: 32);

    /// <summary>Case 7 — the blur at 1.0, which <c>AccentGradientColor</c> can reach.</summary>
    [StaFact]
    public void TheBlur_AtAlpha100_LogsItsDim() =>
        RunCase(7, "blur-a100", "blur", "blur, alpha 1.00", BackdropType.Mica, 1.00, direct: false, flags: 32);

    /// <summary>Case 8 — the transparent gradient at 0.30, production's flags 0x20.</summary>
    [StaFact]
    public void TheTransparentGradient_AtAlpha030_LogsItsDim() =>
        RunCase(8, "gradient-a030", "gradient", "transparent gradient, alpha 0.30", BackdropType.SolidDarken, 0.30,
            direct: false, flags: 32);

    /// <summary>Case 9 — the transparent gradient at 0.60.</summary>
    [StaFact]
    public void TheTransparentGradient_AtAlpha060_LogsItsDim() =>
        RunCase(9, "gradient-a060", "gradient", "transparent gradient, alpha 0.60", BackdropType.SolidDarken, 0.60,
            direct: false, flags: 32);

    /// <summary>Case 10 — the gradient's community flags 2, beside production's 0x20, at 0.30.</summary>
    [StaFact]
    public void TheTransparentGradient_WithFlags2_AtAlpha030_LogsItsDim() =>
        RunCase(10, "gradient-flag2-a030", "gradient", "transparent gradient, flags 2, alpha 0.30",
            BackdropType.SolidDarken, 0.30, direct: true, flags: 2);

    private static void RunCase(
        int caseNumber, string tag, string kind, string label, BackdropType type, double alpha,
        bool direct, int flags)
    {
        Step3LiveCheckTests.BeginShowcaseCase();
        GlassBackdropWindow? window = null;
        try
        {
            var settings = Step3LiveCheckTests.BackdropSettingsOfTheOwner();
            settings.Enabled = true;
            settings.Type = type;      // in-memory only: the owner's file is never written
            settings.Opacity = alpha;  // the case's sweep point
            var screen = System.Windows.Forms.Screen.PrimaryScreen!;

            var dipRight = screen.Bounds.Width / DpiHelper.ScaleX - UIConstants.BackdropHideButtonOffset;
            var hideButtonCenter = new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(dipRight),
                DpiHelper.LogicalToPhysicalY(UIConstants.BackdropHideButtonOffset));

            window = new GlassBackdropWindow();
            window.Show();
            var hwnd = new WindowInteropHelper(window).Handle;
            var ct = HwndSource.FromHwnd(hwnd)!.CompositionTarget!.BackgroundColor;

            // The controls (direct) apply ONLY the accent — a fresh window, no ClearContent and no
            // DisableAccent first, which is exactly r5 and exactly what separates "the alpha
            // suppresses the blur" from "production's disable-then-enable does".
            if (direct && flags == 2)
                _ = ApplyAccentPolicyNative(hwnd, 2, 2, GlassBackdropWindow.AccentGradientColor(alpha));
            else if (direct)
                _ = window.TryApplyAcrylic(alpha);
            else
                window.ApplyBackdrop(settings); // production's own path at this alpha

            window.DrawHideButton(hideButtonCenter);
            window.ShowEscapeHint();

            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(250));
            var name = $"Backdrop calibration, case {caseNumber} ({label})";
            LiveCheck.AssertShowsText(window, name, "Mit Esc beenden");
            LiveCheck.AssertVisible(window, name);

            var rectangles = SolidRectangles(window);

            var baseline = BackdropCapture.Grab();
            SetWindowDisplayAffinity(hwnd, WdaNone);
            BackdropCapture.ScreenSample lifted;
            try
            {
                DwmFlush();
                DwmFlush();
                LiveCheck.PumpFor(TimeSpan.FromMilliseconds(150));
                lifted = BackdropCapture.Grab();
            }
            finally
            {
                SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
            }

            // The production call's BOOL is not readable back; re-applying the same call after the
            // capture gives the same answer.
            var accent = direct && flags == 2
                ? ApplyAccentPolicyNative(hwnd, 2, 2, GlassBackdropWindow.AccentGradientColor(alpha)) != 0
                : direct
                    ? window.TryApplyAcrylic(alpha)
                    : type == BackdropType.SolidDarken
                        ? window.TryApplyTransparentGradient(alpha)
                        : window.TryApplyAcrylic(alpha);
            var state = direct && flags == 2 ? 2 : type == BackdropType.SolidDarken ? 2 : 4;

            var ratioR = lifted.Centre.MeanR / baseline.Centre.MeanR;
            var ratioG = lifted.Centre.MeanG / baseline.Centre.MeanG;
            var ratioB = lifted.Centre.MeanB / baseline.Centre.MeanB;
            var ratioLuma = lifted.Centre.LumaMean / baseline.Centre.LumaMean;
            var stddevRatio = baseline.Centre.LumaStdDev < 0.05
                ? "n/a (a flat baseline)"
                : BackdropReadoutLines.Number(lifted.Centre.LumaStdDev / baseline.Centre.LumaStdDev);

            var log = new StringBuilder();
            log.AppendLine($"case: {caseNumber} ({label})");
            log.AppendLine($"kind: {kind}");
            log.AppendLine($"state: {state}");
            log.AppendLine($"flags: {flags}");
            log.AppendLine($"alpha: {BackdropReadoutLines.Number(alpha)}");
            log.AppendLine($"rectangles: {rectangles}");
            log.AppendLine($"accent: {(accent ? "True (accepted)" : "False (misread BOOL or refused)")}");
            log.AppendLine(
                $"CompositionTarget.BackgroundColor after Show: #{ct.R:X2}{ct.G:X2}{ct.B:X2}{ct.A:X2} (alpha should be 00)");
            log.AppendLine(BackdropCapture.EnvironmentState());
            log.AppendLine($"Baseline (affinity ON; the desktop behind): {BackdropCapture.Describe(baseline.Centre)}");
            log.AppendLine(
                $"Capture (affinity lifted, DwmFlush x2 + 150 ms): {BackdropCapture.Describe(lifted.Centre)}");
            log.AppendLine($"stddev-ratio: {stddevRatio}");
            log.AppendLine(
                $"black-delta: {BackdropReadoutLines.Number(lifted.Centre.BlackFraction - baseline.Centre.BlackFraction)}");
            log.AppendLine($"baseline-luma: {BackdropReadoutLines.Number(baseline.Centre.LumaMean)}");
            log.AppendLine($"ratio-r: {BackdropReadoutLines.Number(ratioR)}");
            log.AppendLine($"ratio-g: {BackdropReadoutLines.Number(ratioG)}");
            log.AppendLine($"ratio-b: {BackdropReadoutLines.Number(ratioB)}");
            log.AppendLine($"ratio-luma: {BackdropReadoutLines.Number(ratioLuma)}");
            log.AppendLine($"dim: {BackdropReadoutLines.Number(1.0 - ratioLuma)}");
            log.AppendLine(
                $"case-mean-rgb: {BackdropReadoutLines.Mean(lifted.Centre.MeanR, lifted.Centre.MeanG, lifted.Centre.MeanB)}");
            log.AppendLine($"case-stddev: {BackdropReadoutLines.Number(lifted.Centre.LumaStdDev)}");

            var verdict = BackdropClassification.Material(baseline, lifted, log);
            log.AppendLine($"Classification: {verdict}");

            BackdropCapture.WriteLog(caseNumber, tag, log.ToString());
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
    /// The test-only native accent call, so the flag-2 variant of the transparent gradient can be
    /// measured beside production's flag 0x20. Returns the API's BOOL (non-zero = accepted).
    /// </summary>
    private static int ApplyAccentPolicyNative(IntPtr hwnd, int state, int flags, int gradient)
    {
        var data = default(WindowCompositionAttributeData);
        try
        {
            var accent = new AccentPolicy { AccentState = state, AccentFlags = flags, GradientColor = gradient };
            data.SizeOfData = Marshal.SizeOf(accent);
            data.Attribute = 19;
            data.Data = Marshal.AllocHGlobal(data.SizeOfData);
            Marshal.StructureToPtr(accent, data.Data, false);
            return SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (data.Data != IntPtr.Zero)
                Marshal.FreeHGlobal(data.Data);
        }
    }

    /// <summary>The darken fallback's rectangle count on the backdrop canvas.</summary>
    private static int SolidRectangles(GlassBackdropWindow window) =>
        ((Canvas)window.FindName("BackdropCanvas")!).Children.OfType<Rectangle>().Count();

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
