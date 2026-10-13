namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using NFluent;
using Xunit;

/// <summary>
/// T13 fixup-05: every overlay window's CLIENT origin must equal its WINDOW origin. Several overlays
/// compute a target in physical screen coordinates and draw it back through the window origin
/// (<c>OverlayPositioning.CoverPrimaryScreen</c> sets <c>Left/Top</c> = the window origin,
/// <c>DrawHideButton</c> converts the physical centre to logical client coordinates), so a window
/// that carries a non-client frame — a resize border — draws its content that many DIP right and
/// below the place its target says.
///
/// <para>
/// The glass backdrop did exactly that: it is the only overlay with
/// <c>AllowsTransparency="False"</c> (needed for DWM Mica/Acrylic), and the default
/// <c>ResizeMode="CanResize"</c> gave it a 12 px (6 DIP at 2.0) resize border — its ✕, dark fill and
/// hint were all drawn 6 DIP right and below their places (found by the T13 live check, then by the
/// off-screen probe that measured this inset). The fix is <c>ResizeMode="NoResize"</c>, as
/// <c>EdgeButtonBarWindow</c> already had.
/// </para>
///
/// <para>
/// <b>Handle only, no <c>Show()</c>.</b> The styles that produce the frame exist as soon as the HWND
/// is created (<see cref="WindowInteropHelper.EnsureHandle"/>), so this runs in the headless suite
/// and paints nothing on the owner's screen. Every concrete overlay window with a parameterless
/// constructor is checked, and the set is pinned so a new overlay class must be considered here.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class OverlayClientOriginTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref Point32 point);

    /// <summary>
    /// The concrete overlay windows this gate checks, pinned so a new one cannot silently escape it.
    /// <see cref="OverlayWindow"/> itself is excluded: it is the base class (a bare window with the
    /// default caption), never shown as an overlay, and its own frame is the caption it carries.
    /// </summary>
    private static readonly string[] ExpectedOverlays =
    [
        "ActionMenuWindow",
        "ActivationButtonWindow",
        "CalibrationWindow",
        "CaregiverAlertWindow",
        "CueOverlayWindow",
        "CursorCircleWindow",
        "DragTrailOverlay",
        "DriftNoticeWindow",
        "DwellFeedbackOverlay",
        "EdgeButtonBarWindow",
        "EyeCareExerciseWindow",
        "GlassBackdropWindow",
        "ManualSelectionGazeIndicatorWindow",
        "ManualSelectionOverlayWindow",
        "ModesMenuWindow",
        "PauseIndicatorWindow",
        "QuickActionsGridWindow",
        "RepeatCursorWindow",
        "ScrollOverlayWindow",
        "SplashWindow",
        "TargetHighlightWindow",
        "TaskSwitcherWindow",
        "ToolbarWindow",
        "TrackStatusWindow",
    ];

    [StaFact]
    public void EveryOverlayWindow_StartsItsClientAtItsWindowOrigin()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var types = OverlayTypes();

        Check.WithCustomMessage(
                "The set of concrete overlay windows changed. Check the new window here (its client origin must "
                + "equal its window origin) and add it to ExpectedOverlays so this gate keeps covering every "
                + "overlay.")
            .That(types.Select(t => t.Name).ToList()).IsEqualTo(ExpectedOverlays.ToList());

        var problems = new List<string>();
        foreach (var type in types)
        {
            var window = (Window)Activator.CreateInstance(type)!;
            try
            {
                var hwnd = new WindowInteropHelper(window).EnsureHandle();
                var inset = ClientOriginMinusWindowOrigin(hwnd);
                if (inset.X != 0 || inset.Y != 0)
                {
                    problems.Add(
                        $"{type.Name}: the client origin is ({inset.X},{inset.Y}) px inside the window "
                        + $"(ResizeMode={window.ResizeMode}, AllowsTransparency={window.AllowsTransparency}); "
                        + "anything drawn in client coordinates lands that far off its target.");
                }
            }
            finally
            {
                try { window.Close(); } catch { /* never shown; nothing to clean beyond the handle */ }
            }
        }

        Check.WithCustomMessage(
                "An overlay window has a non-client frame, so its client origin does not equal its window origin "
                + "(set ResizeMode=\"NoResize\" on it, as EdgeButtonBarWindow and GlassBackdropWindow do):\n  "
                + string.Join("\n  ", problems))
            .That(problems).IsEmpty();
    }

    /// <summary>Every concrete <see cref="OverlayWindow"/> with a parameterless constructor, by name.</summary>
    private static List<Type> OverlayTypes() =>
        typeof(OverlayWindow).Assembly.GetTypes()
            .Where(t => typeof(OverlayWindow).IsAssignableFrom(t)
                        && !t.IsAbstract
                        && t != typeof(OverlayWindow)
                        && t.GetConstructor(BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes) is not null)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>The client area's top-left in screen pixels minus the window rect's top-left, both physical px.</summary>
    private static Point32 ClientOriginMinusWindowOrigin(IntPtr hwnd)
    {
        var origin = new Point32 { X = 0, Y = 0 };
        ClientToScreen(hwnd, ref origin);
        GetWindowRect(hwnd, out var rect);
        return new Point32 { X = origin.X - rect.Left, Y = origin.Y - rect.Top };
    }
}
