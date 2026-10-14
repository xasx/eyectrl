namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
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
/// Round 2 of the backdrop A/B (H3): WPF paints its OWN opaque background over the DWM material. A
/// non-layered <c>HwndSource</c>'s <c>CompositionTarget.BackgroundColor</c> is black and is never
/// made transparent — the window sets only <c>Background="Transparent"</c>, and nothing sets the
/// composition target — so every round-1 case read pure black (0, 0, 0) whatever DWM did. This
/// showcase tests exactly that: five cases, all NEVER-ACTIVATED as production shows the window (no
/// activation games in round 2), fixed order, about 8 s each:
/// <list type="number">
/// <item><c>NoResize</c> + transparent composition target, <b>Mica</b>;</item>
/// <item><c>CanResize</c> + transparent composition target, <b>Mica</b>;</item>
/// <item><c>NoResize</c> + transparent composition target, <b>Acrylic</b> (via <c>ApplyBackdrop</c>,
/// which reaches production's <c>TryApplyAcrylicViaDwm</c>);</item>
/// <item><c>CanResize</c> + transparent composition target, <b>Acrylic</b>;</item>
/// <item><b>the positive control (r5)</b>: <c>NoResize</c> + transparent composition target and
/// production's Phase-2 legacy accent blur (<c>TryApplyAcrylic</c> called directly — it needs
/// neither activation nor a frame). r5 varying means the composition-target fix works and a black
/// r1–r4 is DWM's system backdrop; r5 black means H3 is incomplete.</item>
/// </list>
///
/// <para>
/// The composition target is set BY THE TEST, after <c>Show()</c> (the HWND exists) and before the
/// material — no <c>src/</c> change. Its colour before and after is logged. The capture protocol is
/// round 1's, reused: a baseline with the display affinity still ON, then the lift, <c>DwmFlush</c>
/// ×2 and a 150 ms pump, then the capture; a lifted capture that matches its baseline is
/// UNREADABLE, not a verdict. The readout carries the same statistics (central fifth and the sparse
/// full-screen sample), the styles, whether the foreground window is the backdrop, and — after the
/// capture — the production material call's result and the raw <c>DwmSetWindowAttribute</c> HRESULT.
/// </para>
///
/// <para>
/// <b>The classification (per case).</b> <c>black</c>: nothing reaches the surface in this
/// configuration (the script's reading says which cause); a <c>flat</c> grey: neutral is the
/// expected never-activated fallback (review-02's advisory), a tinted flat is suspect;
/// <c>varying</c>: a live material in that configuration. The readout's machine lines
/// (<c>case-mean-rgb:</c>, <c>case-stddev:</c>, <c>baseline-mean-rgb:</c>) are invariant and
/// ';'-separated — this machine's culture writes decimal commas, and the scripts must read the same
/// numbers (<see cref="BackdropReadoutLines"/>). The script reads the five cases by review-02's
/// pairwise rule — two cases are the SAME when their centre means differ by at most 1.5 on every
/// channel and their stddevs by at most 0.5 — and prints the round-2 reading table, including
/// whether the frame matters; no absolute threshold decides the reading. The HKCU transparency
/// setting and the energy-saver state are logged per case, because either can suppress the material
/// for the session.
/// </para>
///
/// <para>
/// <b>Not a gate.</b> <c>Category=UI</c> and skipped unless <c>EYECTRL_LIVE_CHECK=1</c>; run
/// through <c>C:\t\livecheck-scripts\backdrop-ab-r2.ps1</c> only with the owner's go-ahead. Every
/// case proves its window visible and its hint on screen (<see cref="LiveCheck.AssertVisible"/> +
/// <see cref="LiveCheck.AssertShowsText"/>) before the readout; no input is simulated and no gaze
/// is fed; the window closes in a <c>finally</c>.
/// </para>
///
/// <para>
/// <b>Historical configuration (2026-10-14).</b> Production changed after this round:
/// <c>ApplyBackdrop</c> now takes the legacy accent blur for BOTH named types, and the composition
/// target is made transparent by the window itself. The cases below keep the round-2 shape that
/// produced that decision — including the DWM probes — so a re-run reproduces the record, not
/// today's production path; <see cref="BackdropLiveLookTests"/> is the look of today's.
/// </para>
/// </summary>
[Collection("WpfApplication")]
[Trait("Category", "UI")]
public class BackdropMaterialLiveCheckTests
{
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(8);

    /// <summary>r1 — NoResize (as shipped) + transparent composition target, Mica.</summary>
    [StaFact]
    public void TheBackdrop_WithNoResize_Mica_ThroughATransparentComposition() =>
        RunCase(1, "noresize-mica-ct", "NoResize + transparent CT, Mica", ResizeMode.NoResize, BackdropType.Mica);

    /// <summary>r2 — the pre-T13 frame + transparent composition target, Mica.</summary>
    [StaFact]
    public void TheBackdrop_WithCanResize_Mica_ThroughATransparentComposition() =>
        RunCase(2, "canresize-mica-ct", "CanResize + transparent CT, Mica", ResizeMode.CanResize, BackdropType.Mica);

    /// <summary>r3 — NoResize + transparent composition target, Acrylic through production's DWM path.</summary>
    [StaFact]
    public void TheBackdrop_WithNoResize_Acrylic_ThroughATransparentComposition() =>
        RunCase(3, "noresize-acrylic-ct", "NoResize + transparent CT, Acrylic", ResizeMode.NoResize, BackdropType.Acrylic);

    /// <summary>r4 — the pre-T13 frame + transparent composition target, Acrylic.</summary>
    [StaFact]
    public void TheBackdrop_WithCanResize_Acrylic_ThroughATransparentComposition() =>
        RunCase(4, "canresize-acrylic-ct", "CanResize + transparent CT, Acrylic", ResizeMode.CanResize, BackdropType.Acrylic);

    /// <summary>
    /// r5 — the POSITIVE CONTROL (review-03 finding 2): NoResize + transparent composition target
    /// and production's Phase-2 legacy accent blur (<c>TryApplyAcrylic</c> called directly, because
    /// <c>ApplyBackdrop(Acrylic)</c> only reaches it when the DWM call fails). It needs neither
    /// activation nor a frame, so it separates "the client is transparent now" from "DWM draws no
    /// system backdrop for this window": r5 varying means the CT fix works and a black r1–r4 is
    /// DWM's system backdrop; r5 black means H3 is incomplete. It is also a production option as is.
    /// </summary>
    [StaFact]
    public void TheBackdrop_WithNoResize_LegacyAccentThroughATransparentComposition() =>
        RunCase(5, "noresize-legacy-accent-ct", "NoResize + transparent CT, legacy accent (control)",
            ResizeMode.NoResize, BackdropType.Acrylic, legacyAccent: true);

    private static void RunCase(
        int caseNumber, string tag, string label, ResizeMode resizeMode, BackdropType material,
        bool legacyAccent = false)
    {
        Step3LiveCheckTests.BeginShowcaseCase();
        GlassBackdropWindow? window = null;
        try
        {
            var settings = Step3LiveCheckTests.BackdropSettingsOfTheOwner();
            settings.Enabled = true; // the showcase shows the backdrop itself; production skips only when disabled
            settings.Type = material; // in-memory only: the owner's file is never written
            var screen = System.Windows.Forms.Screen.PrimaryScreen!;

            // Production's own hide-button layout, restated from ShowBackdropIfEnabled (:107-111).
            var dipRight = screen.Bounds.Width / DpiHelper.ScaleX - UIConstants.BackdropHideButtonOffset;
            var hideButtonCenter = new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(dipRight),
                DpiHelper.LogicalToPhysicalY(UIConstants.BackdropHideButtonOffset));

            window = new GlassBackdropWindow { ResizeMode = resizeMode };
            window.Show();                        // the HWND exists after this

            // H3, the test-only fix: WPF's own composition background is opaque black on a
            // non-layered window and paints over the DWM material. Set after Show (the source
            // exists) and before the material, so DWM's frame is the only thing behind the canvas.
            var source = (HwndSource)PresentationSource.FromVisual(window)!;
            var ctBefore = source.CompositionTarget!.BackgroundColor;
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            var ctAfter = source.CompositionTarget.BackgroundColor;

            string applied;
            bool legacyApplied = false;
            if (legacyAccent)
            {
                // Production's Phase-2 path, called directly: ApplyBackdrop(Acrylic) only reaches it
                // when the DWM call fails, and this control needs it regardless. Production arrives
                // after ClearContent -> ClearDwmBackdrop (type NONE, margins 0); this fresh window's
                // type is DWM's default Auto, so clear it first for exact parity (review-04 finding 5).
                ClearDwmBackdrop(window);
                legacyApplied = TryApplyAcrylic(window, settings.Opacity);
                applied = $"legacy accent blur (SetWindowCompositionAttribute ACCENT_ENABLE_ACRYLICBLURBEHIND); "
                    + $"TryApplyAcrylic returned {legacyApplied}";
            }
            else
            {
                window.ApplyBackdrop(settings);   // the material, through production's own dispatch
                applied = $"production ApplyBackdrop({material})";
            }

            window.DrawHideButton(hideButtonCenter);
            window.ShowEscapeHint();

            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(250));
            var name = $"Backdrop A/B round 2, case {caseNumber} ({label})";
            LiveCheck.AssertShowsText(window, name, "Mit Esc beenden");
            LiveCheck.AssertVisible(window, name);

            var hwnd = new WindowInteropHelper(window).Handle;
            var baseline = BackdropAbLiveCheckTests.Grab();   // affinity still ON: the desktop behind
            SetWindowDisplayAffinity(hwnd, WdaNone);          // lift, in the test only
            BackdropAbLiveCheckTests.ScreenSample lifted;
            var flush1 = 0;
            var flush2 = 0;
            try
            {
                flush1 = DwmFlush();
                flush2 = DwmFlush();
                LiveCheck.PumpFor(TimeSpan.FromMilliseconds(150));
                lifted = BackdropAbLiveCheckTests.Grab();
            }
            finally
            {
                SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture); // restore for every later capture
            }

            var log = new StringBuilder();
            log.AppendLine($"case: {caseNumber} ({label})");
            log.AppendLine($"Material requested: {material}; never activated (production's style)");
            log.AppendLine($"Material applied: {applied}");
            log.AppendLine($"CompositionTarget.BackgroundColor before the fix: #{ctBefore.R:X2}{ctBefore.G:X2}{ctBefore.B:X2}{ctBefore.A:X2}; "
                + $"after: #{ctAfter.R:X2}{ctAfter.G:X2}{ctAfter.B:X2}{ctAfter.A:X2}");
            log.AppendLine($"{TransparencyState()}; {EnergySaverState()}");

            var style = GetWindowLong(hwnd, GwlStyle);
            var exStyle = GetWindowLong(hwnd, GwlExStyle);
            log.AppendLine($"Window styles: style 0x{style:X8}, exstyle 0x{exStyle:X8}");
            log.AppendLine(
                $"  WS_THICKFRAME (the resize frame): {(style & WsThickFrame) != 0}; "
                + $"WS_EX_NOACTIVATE: {(exStyle & WsExNoActivate) != 0}; "
                + $"WS_EX_TRANSPARENT (click-through): {(exStyle & WsExTransparent) != 0}");
            log.AppendLine($"GetForegroundWindow() == backdrop hwnd: {GetForegroundWindow() == hwnd}");

            log.AppendLine($"Baseline (affinity ON; the desktop behind): {BackdropAbLiveCheckTests.Describe(baseline.Centre)}");
            log.AppendLine($"Baseline full screen (1/16): {BackdropAbLiveCheckTests.Describe(baseline.Full)}");
            log.AppendLine(
                $"Capture (affinity lifted, DwmFlush x2 (0x{flush1:X8}, 0x{flush2:X8}) + 150 ms): "
                + BackdropAbLiveCheckTests.Describe(lifted.Centre));
            log.AppendLine($"Capture full screen (1/16): {BackdropAbLiveCheckTests.Describe(lifted.Full)}");
            log.AppendLine(
                $"case-mean-rgb: {BackdropReadoutLines.Mean(lifted.Centre.MeanR, lifted.Centre.MeanG, lifted.Centre.MeanB)}");
            log.AppendLine($"case-stddev: {BackdropReadoutLines.Number(lifted.Centre.LumaStdDev)}");
            log.AppendLine(
                $"baseline-mean-rgb: {BackdropReadoutLines.Mean(baseline.Centre.MeanR, baseline.Centre.MeanG, baseline.Centre.MeanB)}");

            var verdict = Classify(baseline, lifted, log);

            // The material probes run AFTER the capture: a re-apply restarts DWM's material
            // transition, and the capture must see what the hold shows.
            if (legacyAccent)
            {
                log.AppendLine(
                    $"TryApplyAcrylic (re-applied after the capture): {TryApplyAcrylic(window, settings.Opacity)}; "
                    + "no DWMWA_SYSTEMBACKDROP_TYPE in this path");
            }
            else
            {
                var probe = material == BackdropType.Mica ? "TryApplyMica" : "TryApplyAcrylicViaDwm";
                bool probeResult;
                try
                {
                    probeResult = (bool)typeof(GlassBackdropWindow)
                        .GetMethod(probe, BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(window, null)!;
                }
                catch (TargetInvocationException)
                {
                    probeResult = false;
                }

                var backdropType = material == BackdropType.Mica ? DwmBackdropMica : DwmBackdropAcrylic;
                var hresult = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdropType, sizeof(int));
                log.AppendLine(
                    $"{probe} (re-applied after the capture): {probeResult}; raw "
                    + $"DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE, {(material == BackdropType.Mica ? "Mica" : "Acrylic")}) "
                    + $"HRESULT: 0x{hresult:X8} (0 = accepted)");
            }
            log.AppendLine($"Classification: {verdict}");

            BackdropAbLiveCheckTests.WriteLog(caseNumber, tag, log.ToString());
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
    /// The round-2 verdict. UNREADABLE first (round 1's rule); then black, a flat grey — neutral
    /// (the expected inactive fallback) or tinted (suspect, review-02's advisory) — or varying,
    /// which is a live material. The pairwise rule that turns four verdicts into a reading lives in
    /// the script, where all four readouts meet.
    /// </summary>
    internal static string Classify(
        BackdropAbLiveCheckTests.ScreenSample baseline,
        BackdropAbLiveCheckTests.ScreenSample lifted,
        StringBuilder log)
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
            + $"{baseline.Centre.MeanB:0.0}) to ({centre.MeanR:0.0}, {centre.MeanG:0.0}, {centre.MeanB:0.0}); "
            + $"luma stddev {baseline.Centre.LumaStdDev:0.00} -> {centre.LumaStdDev:0.00}.");

        if (centre.BlackFraction >= 0.98)
            return "black (nothing reaches the surface in this configuration; the script's reading says why)";

        var neutrality = Math.Max(
            Math.Abs(centre.MeanR - centre.MeanG),
            Math.Max(Math.Abs(centre.MeanR - centre.MeanB), Math.Abs(centre.MeanG - centre.MeanB)));
        if (centre.LumaStdDev < 1.0)
        {
            return neutrality <= 3.0
                ? "flat neutral grey (the composition-target fix worked; the never-activated surface's fallback — a design question if the material must be live)"
                : $"flat but tinted (suspect: the inactive fallback is neutral; channel spread {neutrality:0.0})";
        }

        return "varying (a live material in this configuration)";
    }

    /// <summary>
    /// Production's Phase-2 legacy accent blur, reached by reflection (test-only call; now with the
    /// settings' opacity, which the accent's tint carries). The window arrives after
    /// <see cref="ClearDwmBackdrop"/>, exactly as production's <c>ApplyBackdrop</c> reaches it
    /// (review-04 finding 5).
    /// </summary>
    private static bool TryApplyAcrylic(GlassBackdropWindow window, double opacity)
    {
        try
        {
            return (bool)typeof(GlassBackdropWindow)
                .GetMethod("TryApplyAcrylic", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object?[] { opacity })!;
        }
        catch (TargetInvocationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Production's <c>ClearDwmBackdrop</c> (private), so r5 starts where Phase 2 finds the window:
    /// system-backdrop type NONE and the frame extension collapsed.
    /// </summary>
    private static void ClearDwmBackdrop(GlassBackdropWindow window)
    {
        try
        {
            typeof(GlassBackdropWindow)
                .GetMethod("ClearDwmBackdrop", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, null);
        }
        catch (TargetInvocationException)
        {
            // The classification shows whether the material landed; a failed clear must not abort
            // the case.
        }
    }

    /// <summary>
    /// The HKCU transparency-effects setting. A material that is unavailable for the session would
    /// otherwise read as black or flat too (review-03 finding 2).
    /// </summary>
    private static string TransparencyState()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return $"EnableTransparency (HKCU Personalize): {key?.GetValue("EnableTransparency") ?? "missing"}";
        }
        catch
        {
            return "EnableTransparency (HKCU Personalize): unreadable";
        }
    }

    /// <summary>Energy saver forces the materials off, so its state belongs beside the readout.</summary>
    private static string EnergySaverState()
    {
        var status = default(SystemPowerStatus);
        if (!GetSystemPowerStatus(ref status)) return "power state unreadable";
        var saver = status.SystemStatusFlag == 1 ? "energy saver ON" : "energy saver off";
        var line = status.ACLineStatus == 1 ? "AC" : "battery";
        var battery = status.BatteryLifePercent <= 100 ? $"battery {status.BatteryLifePercent}%" : "battery ?";
        return $"{saver}, {line}, {battery}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsThickFrame = 0x00040000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmBackdropMica = 2;
    private const int DwmBackdropAcrylic = 3;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(ref SystemPowerStatus status);
}
