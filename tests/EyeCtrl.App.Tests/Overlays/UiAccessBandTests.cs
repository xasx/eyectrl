namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using NFluent;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

/// <summary>
/// C9 F2: every overlay must reach ZBID_UIACCESS (band 2), the band Start, Search and the
/// notification centre cannot cover. Under UIAccess an overlay with <c>ShowInTaskbar="False"</c>
/// starts in ZBID_DESKTOP (band 1) despite being <c>WS_EX_TOPMOST</c>; the one
/// <c>SetWindowPos(HWND_TOPMOST, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)</c> after its HWND
/// exists is what moves it (S8 A2: <c>C:\t\s8\a2-bands.csv</c>).
///
/// <para>
/// These gates are window-free in the sense that matters: each window only gets an HWND through
/// <c>EnsureHandle</c> — nothing is shown, nothing is under UIAccess, and the native raise is an
/// injected recorder. The three mutants for this ticket all land here: removing the raise (the
/// count becomes zero), raising per position update (the count grows with the ring's frames), and
/// dropping <c>SWP_NOACTIVATE</c> (the exact-flags assertion).
/// </para>
///
/// <para>
/// The z-order inside the band is pinned by <see cref="EdgeButtonBarZOrderTests"/> (bar above the
/// backdrop, gaze dot above the bar) and <see cref="ResumeStripZOrderTests"/> (the Resume strip
/// above every note, re-raised on show and on band leave), both reading the real Win32 z-order of
/// cloaked windows. This class pins the OTHER half of the rule: the band raise happens once per
/// overlay and never from a position update, so those explicit ordering raises remain the only
/// ones a frame can trigger.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class UiAccessBandTests
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const long HwndTopmost = -1;

    private const long GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;

    /// <summary>
    /// Every overlay the A2 evidence found in band 1, plus the band-2 strip as a control: each one
    /// raises itself into the UIAccess band exactly once, when its HWND is created, and a repeat of
    /// the one-time path cannot raise again.
    /// </summary>
    [StaFact]
    public void EveryOverlay_RaisesIntoTheUiAccessBand_ExactlyOnce_OnHandleCreation()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        foreach (var (name, window) in CreateOverlays())
        {
            try
            {
                var calls = new List<RaiseCall>();
                window.SetWindowPosForBand =
                    (hwnd, insertAfter, flags) => calls.Add(new RaiseCall(hwnd, insertAfter, flags));

                var hwnd = new WindowInteropHelper(window).EnsureHandle();

                Check.WithCustomMessage(
                        $"{name}: the band raise must run as soon as its HWND exists (one per window lifetime)")
                    .That(calls).HasSize(1);

                var call = calls[0];
                Check.WithCustomMessage($"{name}: raised a different HWND").That(call.Hwnd).IsEqualTo(hwnd);
                Check.WithCustomMessage($"{name}: the raise must insert at HWND_TOPMOST")
                    .That(call.InsertAfter.ToInt64()).IsEqualTo(HwndTopmost);
                Check.WithCustomMessage(
                        $"{name}: flags must be SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE — no activation, and no "
                        + "position change in the raise")
                    .That(call.Flags).IsEqualTo(SwpNoMove | SwpNoSize | SwpNoActivate);
                Check.WithCustomMessage($"{name}: one raise, not one per show").That(window.BandRaiseCount).IsEqualTo(1);

                // A second first-show path (a re-entrant OnSourceInitialized, or a future
                // ContentRendered hook) must find the flag set and do nothing.
                window.RaiseIntoUiAccessBandOnce();
                Check.WithCustomMessage($"{name}: the one-time band raise must be idempotent").That(calls).HasSize(1);
                Check.That(window.BandRaiseCount).IsEqualTo(1);
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>
    /// Ring team constraint 1: the ring moves on every gaze frame, and a topmost re-insert from a
    /// position update is the stutter / z-order-thrash bug. The injected native side makes the
    /// absence of a raise observable; a real ordering raise afterwards proves the seam sees one, so
    /// the count has teeth.
    /// </summary>
    [StaFact]
    public void PositionUpdates_NeverRaiseTheBand()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var ring = InvisibleTestWindow.Prepare(new CursorCircleWindow());
        var indicator = InvisibleTestWindow.Prepare(new ManualSelectionGazeIndicatorWindow());
        try
        {
            var calls = new List<RaiseCall>();
            ring.SetWindowPosForBand = (hwnd, insertAfter, flags) => calls.Add(new RaiseCall(hwnd, insertAfter, flags));
            indicator.SetWindowPosForBand =
                (hwnd, insertAfter, flags) => calls.Add(new RaiseCall(hwnd, insertAfter, flags));

            _ = new WindowInteropHelper(ring).EnsureHandle();
            _ = new WindowInteropHelper(indicator).EnsureHandle();
            Check.That(calls).HasSize(2); // one per window, at HWND creation

            for (var i = 0; i < 120; i++)
            {
                ring.UpdatePosition(new PhysicalPoint(100 + i * 3, 200 + i * 2));
                indicator.UpdatePosition(400 + i, 500 + i);
            }

            Check.WithCustomMessage(
                    "a position update raised the band: the ring does this on every gaze frame, which is "
                    + "exactly the stutter/z-order-thrash bug the ring constraint forbids")
                .That(calls).HasSize(2);

            // Teeth: an explicit ordering raise is visible to the same seam.
            ring.RaiseToTop();
            Check.That(calls).HasSize(3);
            Check.That(ring.BandRaiseCount).IsEqualTo(2);
        }
        finally
        {
            ring.Close();
            indicator.Close();
        }
    }

    /// <summary>
    /// Style constraint: the band raise must not alter the overlay's extended styles. It inserts
    /// at the top of the band; the never-activate, tool-window and topmost bits stay as they were.
    /// </summary>
    [StaFact]
    public void TheBandRaise_LeavesTheExtendedWindowStylesUnchanged()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var window = InvisibleTestWindow.Prepare(new ToolbarWindow());
        try
        {
            window.SetWindowPosForBand = (_, _, _) => { };
            var hwnd = new WindowInteropHelper(window).EnsureHandle();

            var before = (long)GetWindowLong(hwnd, (int)GwlExStyle);
            window.RaiseToTop();
            var after = (long)GetWindowLong(hwnd, (int)GwlExStyle);

            Check.WithCustomMessage($"styles changed from 0x{before:X} to 0x{after:X}")
                .That(after).IsEqualTo(before);
            Check.WithCustomMessage("the overlay must stay WS_EX_NOACTIVATE").That(after & WsExNoActivate).IsNotEqualTo(0);
            Check.WithCustomMessage("the overlay must stay WS_EX_TOOLWINDOW").That(after & WsExToolWindow).IsNotEqualTo(0);
            Check.WithCustomMessage("the overlay must stay WS_EX_TOPMOST").That(after & WsExTopmost).IsNotEqualTo(0);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The per-overlay band line: one INFO line, with the overlay, its HWND and the band the
    /// read-only probe saw. Running the one-time path again must not repeat it.
    /// </summary>
    [StaFact]
    public void EachOverlay_LogsItsBandOnce_WhenTheBandRaiseRuns()
    {
        var window = InvisibleTestWindow.Prepare(new OverlayWindow());
        var sink = new CapturingSink();
        var previousLogger = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(sink).CreateLogger();
            window.SetWindowPosForBand = (_, _, _) => { };

            var hwnd = new WindowInteropHelper(window).EnsureHandle();

            var line = sink.Messages.SingleOrDefault(
                m => m.StartsWith("Overlay band: ", StringComparison.Ordinal) && m.Contains("OverlayWindow"));
            Check.WithCustomMessage($"no band line; saw: {string.Join(" | ", sink.Messages)}").That(line).IsNotNull();
            Check.WithCustomMessage($"the band line names another window: {line}")
                .That(line!).Contains("OverlayWindow");
            Check.WithCustomMessage($"the band line lacks this HWND: {line}")
                .That(line!).Contains($"hwnd=0x{hwnd.ToInt64():X}");
            Check.WithCustomMessage($"the band line has no band value: {line}")
                .That(line!).Contains("band=");

            window.RaiseIntoUiAccessBandOnce();
            Check.WithCustomMessage("the per-overlay band line must be written once, not per raise")
                .That(sink.Messages.Count(
                    m => m.StartsWith("Overlay band: ", StringComparison.Ordinal)
                         && m.Contains("OverlayWindow"))).IsEqualTo(1);
        }
        finally
        {
            Log.Logger = previousLogger;
            window.Close();
        }
    }

    /// <summary>
    /// The settings window is an ordinary window (Q13) and C9 F2 leaves it out of the overlay band
    /// fix; a future change must not quietly pull it in.
    /// </summary>
    [Fact]
    public void TheSettingsWindow_OptsOutOfTheOverlayBandFix()
    {
        var source = File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Views", "SettingsWindow.xaml.cs"));

        Check.WithCustomMessage(
                "SettingsWindow must keep overriding EntersUiAccessBand to false: it is an ordinary window, "
                + "outside the C9 F2 overlay-band scope.")
            .That(source).Contains("protected override bool EntersUiAccessBand => false;");
    }

    /// <summary>
    /// The band-1 overlays (A2 evidence) plus the pause strip, which already had an explicit raise
    /// and must still get the base's one raise rather than lose it.
    /// </summary>
    private static IEnumerable<(string Name, OverlayWindow Window)> CreateOverlays()
    {
        yield return (nameof(ToolbarWindow), InvisibleTestWindow.Prepare(new ToolbarWindow()));
        yield return (nameof(CursorCircleWindow), InvisibleTestWindow.Prepare(new CursorCircleWindow()));
        yield return (nameof(ManualSelectionOverlayWindow),
            InvisibleTestWindow.Prepare(new ManualSelectionOverlayWindow()));
        yield return (nameof(ActionMenuWindow), InvisibleTestWindow.Prepare(new ActionMenuWindow()));
        yield return (nameof(KeyboardWindow), InvisibleTestWindow.Prepare(new KeyboardWindow()));
        yield return (nameof(ActivationButtonWindow), InvisibleTestWindow.Prepare(new ActivationButtonWindow()));
        yield return (nameof(GlassBackdropWindow),
            InvisibleTestWindow.PrepareOffScreen(new GlassBackdropWindow()));
        yield return (nameof(PauseIndicatorWindow), InvisibleTestWindow.Prepare(new PauseIndicatorWindow()));
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    private readonly record struct RaiseCall(IntPtr Hwnd, IntPtr InsertAfter, uint Flags);

    private sealed class CapturingSink : ILogEventSink
    {
        public List<string> Messages { get; } = new();

        public void Emit(LogEvent logEvent) => Messages.Add(logEvent.RenderMessage());
    }
}
