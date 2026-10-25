namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Xunit;

/// <summary>
/// B2a, the sticky owner's condition 2 (gaze safety): the strip — and with it the Resume target —
/// stays ABOVE every NoteWindow, re-asserting topmost whenever it shows and whenever the band leaves
/// (the moment Resume appears), so a note card can never cover the way out of Pause.
/// <para>
/// Reads the real Win32 Z-order (EnumWindows, front to back) of two real windows, like
/// <see cref="EdgeButtonBarZOrderTests"/>. Both windows are cloaked by DWM and parked off-screen
/// before their first show, and the strip is click-through: nothing reaches the owner's screen, even
/// under a mutant. Skips when DWM cannot cloak here.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class ResumeStripZOrderTests
{
    [StaFact]
    public void TheStrip_ShowsAboveANoteShownBeforeIt_AndAgainAfterAResume()
    {
        using var rig = Rig.Create();

        rig.Note.Show();                     // a note on screen before the pause
        rig.Adapter.SetPauseIndicator(true); // paused
        Pump();
        Check.WithCustomMessage($"strip Z={ZIndexOf(rig.Strip)}, note Z={ZIndexOf(rig.Note)} (lower = in front)")
            .That(ZIndexOf(rig.Strip)).IsStrictlyLessThan(ZIndexOf(rig.Note));

        // Resume, a note re-raised while running, then pause again. A re-shown window keeps its old
        // Z slot (SW_SHOWNA), so only the explicit raise puts the strip back in front.
        rig.Adapter.SetPauseIndicator(false);
        rig.Note.RaiseToTop();
        rig.Adapter.SetPauseIndicator(true);
        Pump();
        Check.WithCustomMessage($"re-paused: strip Z={ZIndexOf(rig.Strip)}, note Z={ZIndexOf(rig.Note)} (lower = in front)")
            .That(ZIndexOf(rig.Strip)).IsStrictlyLessThan(ZIndexOf(rig.Note));
    }

    [StaFact]
    public void TheBandLeaving_RaisesTheStripAboveANoteRaisedMeanwhile()
    {
        using var rig = Rig.Create();

        rig.Adapter.SetPauseIndicator(true);
        Pump();
        rig.Note.Show();
        rig.Note.RaiseToTop(); // the note came up while the band (and its Play) was showing
        Check.That(ZIndexOf(rig.Note)).IsStrictlyLessThan(ZIndexOf(rig.Strip));

        rig.Adapter.HideToolbar(paused: true); // the band leaves: Resume appears, the strip is raised
        Pump();
        Check.WithCustomMessage($"strip Z={ZIndexOf(rig.Strip)}, note Z={ZIndexOf(rig.Note)} (lower = in front)")
            .That(ZIndexOf(rig.Strip)).IsStrictlyLessThan(ZIndexOf(rig.Note));
    }

    private sealed class Rig : IDisposable
    {
        public required PauseIndicatorWindow Strip { get; init; }
        public required NoteWindow Note { get; init; }
        public required ToolbarVisibilityAdapter Adapter { get; init; }

        public static Rig Create()
        {
            WpfApplicationGate.EnsureApplicationWithTokens();
            var settings = new EyeCtrlSettings();
            var messenger = new WeakReferenceMessenger();

            var strip = InvisibleTestWindow.PrepareCloaked(new PauseIndicatorWindow());
            var note = InvisibleTestWindow.PrepareCloaked(new NoteWindow("b2a-zorder"));
            if (InvisibleTestWindow.IsCloaked(strip) != true || InvisibleTestWindow.IsCloaked(note) != true)
            {
                strip.Close();
                note.Close();
                Assert.Skip("DWM cannot cloak a window here; this test never shows an uncloaked window.");
            }

            strip.SetViewModel(new ResumeStripViewModel(settings, messenger));
            // Off-screen as well as cloaked. The strip lays itself out only on SetEdge/SetBandBounds,
            // which this test never calls, so the position holds.
            strip.Left = -12000;
            strip.Top = -12000;
            note.Left = -12000;
            note.Top = -11000;

            var adapter = new ToolbarVisibilityAdapter(
                TestFakes.MakeOverlayManager(toolbar: new TestFakes.FakeToolbarOverlay(), pauseIndicator: strip),
                Dispatcher.CurrentDispatcher, new ToolbarViewModel(settings, messenger));
            return new Rig { Strip = strip, Note = note, Adapter = adapter };
        }

        public void Dispose()
        {
            Strip.Close();
            Note.Close();
        }
    }

    private static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    /// <summary>The window's position in the desktop Z-order: 0 is frontmost; int.MaxValue without an HWND.</summary>
    private static int ZIndexOf(Window window)
    {
        var target = new WindowInteropHelper(window).Handle;
        if (target == IntPtr.Zero) return int.MaxValue;

        var found = int.MaxValue;
        var index = 0;
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == target) { found = index; return false; }
            index++;
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
