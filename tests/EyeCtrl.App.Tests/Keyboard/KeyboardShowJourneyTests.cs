namespace EyeCtrl.App.Tests.Keyboard;

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// The "one look, one key" journey, end to end on the REAL window (T1, restated as one journey
/// by the holistic review): <b>the keyboard is shown with the gaze already resting on a key —
/// nothing fires; the gaze looks away out of the window for at least 120 ms
/// (<see cref="KeyboardViewModel.RearmLeaveUs"/>) and returns — exactly one key fires, after the
/// full dwell; the gaze then keeps resting on that key for 3 s — still exactly one.</b>
///
/// <para>
/// Three mechanisms must hold AT ONCE for this to pass, which is why it is a journey and not
/// three units: the show's landing anchor (a key materializing under a resting gaze must not
/// type), the show anchor's off-key release (a look away proves the gaze has seen the shown
/// keyboard, so a return is a deliberate look), and the fired key's leave-before-re-arm latch
/// (a dwell that never left the key cannot start a second one). Each half of each mechanism has
/// its own unit test; the journey proves they compose on the shipped QWERTZ letters page at the
/// real DIP geometry.
/// </para>
///
/// <para>
/// Never calls <c>Window.Show()</c>: the shipped layout is hosted in an off-screen, never-visible
/// <see cref="HwndSource"/> and the show runs through <c>PlaceForShow(armLandingGuard: true)</c> —
/// the exact placement the coordinator performs for a real hidden→visible transition.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class KeyboardShowJourneyTests
{
    private static readonly Rect WorkArea = new(0, 0, 1920, 1040);
    private const double WidthDip = 1000;
    private const double HeightDip = 400;
    private const int KeyDwellMs = 450;
    private const int WsPopup = unchecked((int)0x80000000);

    private sealed record Probe(
        KeyboardWindow Window, KeyboardViewModel Vm, FrameworkElement Content, HwndSource Source)
        : IDisposable
    {
        public void Dispose() { Source.Dispose(); Window.Close(); }
    }

    private static Probe Open()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var layout = KeyboardLayoutLoader.Load(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Resources", "Layouts", "qwertz.json"));
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        var window = new KeyboardWindow();
        window.SetViewModel(vm);
        window.SetScreenEdge("bottom");
        window.LoadLayout(layout, 1.0, keyDwellMs: KeyDwellMs,
            predictionDwellMs: 800, cooldownMs: 0);

        var content = (FrameworkElement)window.Content;
        window.Content = null;

        var source = new HwndSource(new HwndSourceParameters("kb-show-journey-probe")
        {
            WindowStyle = WsPopup,
            PositionX = -30000,
            PositionY = -30000,
            Width = 1,
            Height = 1,
        });
        var factor = 1.0 / source.CompositionTarget!.TransformToDevice.M11;
        content.LayoutTransform = new ScaleTransform(factor, factor);
        source.RootVisual = content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();

        Check.WithCustomMessage(
                "No PresentationSource: PointToScreen would throw, no key centre would publish, "
                + "and every leg of the journey would pass vacuously.")
            .That(PresentationSource.FromVisual(content)).IsNotNull();

        window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);
        Check.That(Centres(window)).Not.IsEmpty();
        return new Probe(window, vm, content, source);
    }

    private static Dictionary<string, PhysicalPoint> Centres(KeyboardWindow w) =>
        (Dictionary<string, PhysicalPoint>)typeof(KeyboardWindow)
            .GetField("_keyScreenPositions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    /// <summary>A point far outside the whole window surface, so a sample there is gaze-off.</summary>
    private static PhysicalPoint OffWindow(Probe probe)
    {
        var topLeft = probe.Content.PointToScreen(new Point(0, 0));
        return new PhysicalPoint(topLeft.X - 500, topLeft.Y - 500);
    }

    [StaFact]
    public void Shown_UnderARestingGaze_TypesOnlyAfterALookAway_AndNeverTwice()
    {
        using var probe = Open();
        var window = probe.Window;

        // The real hidden→visible show, with the landing guard armed.
        window.PlaceForShow(WorkArea, WidthDip, HeightDip, armLandingGuard: true);

        var fired = new List<string>();
        probe.Vm.KeyDwellCompleted += id => fired.Add(id);
        var centre = Centres(window)["key_0_1"];   // Q, letters row 0
        var off = OffWindow(probe);

        // Leg 1 — rest on the key the keyboard appeared under: the show's landing anchor must
        // keep it dead for the whole rest (two shows typed a letter 432–453 ms after appearing).
        for (long t = 1_000_000; t <= 3_000_000; t += 30_000)
        {
            window.UpdateGaze(centre, t);
            Check.WithCustomMessage("a key fired under a gaze resting where the keyboard appeared")
                .That(fired).IsEmpty();
        }

        // Leg 2 — look AWAY, out of the window, for 180 ms of continuous samples: past
        // RearmLeaveUs (120 ms), so the show anchor's off-key release fires. No sample gap
        // exceeds the 150 ms continuity bound, so the off-run is one run.
        for (long t = 3_030_000; t <= 3_210_000; t += 30_000)
            window.UpdateGaze(off, t);

        // Leg 3 — back to the same key: a deliberate look now, so it types — exactly once,
        // after a full dwell (450 ms of continuous samples from the return).
        for (long t = 3_240_000; t <= 4_500_000 && fired.Count == 0; t += 30_000)
            window.UpdateGaze(centre, t);

        Check.WithCustomMessage(
                $"the return leg fired {fired.Count} times: [{string.Join(", ", fired)}] — the shown "
                + "keyboard either stayed dead after a deliberate look away and back, or fired twice")
            .That(fired).ContainsExactly("key_0_1");

        // Leg 4 — keep resting on the key for 3 s: leave-before-re-arm must hold the latch, so
        // the gaze that never left types nothing more.
        for (long t = 4_530_000; t <= 7_500_000; t += 30_000)
        {
            window.UpdateGaze(centre, t);
            Check.WithCustomMessage(
                    $"the key re-fired while the gaze never left it: [{string.Join(", ", fired)}]")
                .That(fired).ContainsExactly("key_0_1");
        }
    }
}
