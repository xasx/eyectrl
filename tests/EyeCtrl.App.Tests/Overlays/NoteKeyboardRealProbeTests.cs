namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// K1 (sitting 10): the note's Edit card must clear the keyboard when the keyboard is ALREADY up
/// at creation, and it must follow the keyboard's FINAL rect after a flip and a re-show.
/// <para>
/// The headless tests used a fake probe: they set the rects and call <c>RaiseChanged()</c> after
/// <c>CreateNoteAt</c>, an event production never sends when the keyboard was already visible
/// (the editor's hand-over keeps it up, so <c>BeginEditing</c> requests no show). These tests use
/// the production pair — the real <see cref="KeyboardProbe"/> over a real <see cref="KeyboardWindow"/>
/// with the shipped qwertz layout — and never a fake.
/// </para>
/// <para>
/// The keyboard window is SHOWN only through <see cref="InvisibleTestWindow.Prepare"/> (opacity 0,
/// non-activating): layout, the window's on-screen rect and LocationChanged/SizeChanged run exactly
/// as in production, and nothing is painted.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class NoteKeyboardRealProbeTests
{
    /// <summary>The owner's work area in DIP (1920x1152 at 2.0), kept as the virtual stage: the
    /// real keyboard window is positioned on it, and the note manager derives against it.</summary>
    private static readonly Rect WorkArea = new(0, 0, 1920, 1152);

    /// <summary>The owner's keyboard x (the log's 278 physical px at 2.0). The width/height come
    /// from the shipped qwertz layout at key size 1.5, read back from the real window.</summary>
    private const double KeyboardXDip = 139;

    private const int TextSizeDip = 36;

    /// <summary>An anchor under the top keyboard. Once the card is placed clear (below the top
    /// keyboard) that spot lies INSIDE the bottom keyboard's band, so a flip has to move the card
    /// again rather than leave it where it was. The sitting's b-bottom anchor was (1440, 586).</summary>
    private static readonly Point AnchorDip = new(960, 300);

    [StaFact]
    public void CreateWhileTheKeyboardIsUp_PlacesTheEditCardClearOfIt()
    {
        // The sitting's first failure, all three notes: the keyboard was already visible when the
        // note was created (the previous editor handed it over), so no probe raise followed and
        // the empty-avoid first placement stayed under the keyboard for 17-31 s.
        using var rig = new Rig();
        rig.Manager.CreateNoteAt(rig.Phys(AnchorDip));

        var placement = rig.Placement;
        var keyboard = rig.KeyboardRectDip;
        Check.WithCustomMessage($"the keyboard probe reported no rect; the assertion would pass vacuously")
            .That(keyboard.Width).IsStrictlyGreaterThan(0);

        AssertClearOfTheKeyboard(placement, keyboard, "created while the keyboard was up");
    }

    [StaFact]
    public void FlippingTheKeyboardMidEdit_RePlacesTheCardClearOfTheNewRect()
    {
        // The production order of the edge-bar Flip, which is what the owner used (the findings'
        // b-bottom, 21:46:07): the dwell sends KeyboardEdgeToggledMessage; the probe queues its
        // raise at Background, the coordinator queues SetScreenEdge at Normal. The re-place must
        // read the keyboard's FINAL rect, not the edge it is leaving.
        using var rig = new Rig();
        rig.Manager.CreateNoteAt(rig.Phys(AnchorDip));
        var before = rig.Placement;
        AssertClearOfTheKeyboard(before, rig.KeyboardRectDip, "before the flip");

        rig.FlipTo("Bottom");
        var after = rig.Placement;
        var bottom = rig.KeyboardRectDip;
        Check.WithCustomMessage($"the probe still reports the top rect after the flip: {bottom}")
            .That(bottom.Top).IsStrictlyGreaterThan(WorkArea.Height / 2);
        Check.WithCustomMessage($"the card {before.Window} did not move off the new keyboard {bottom}")
            .That(after.Window).IsNotEqualTo(before.Window);
        AssertClearOfTheKeyboard(after, bottom, "after the flip");
    }

    [StaFact]
    public void ReShowingTheKeyboardAtTheSamePlaceMidEdit_RePlacesTheCardClearOfIt()
    {
        // A re-show at the same size and place fires no SizeChanged/Loaded/LocationChanged; only
        // the VM's IsVisible flip reaches the probe (KeyboardProbe.cs:78-85). The card must be
        // clear again once the keyboard is back.
        using var rig = new Rig();
        rig.Manager.CreateNoteAt(rig.Phys(AnchorDip));
        var top = rig.KeyboardRectDip;
        AssertClearOfTheKeyboard(rig.Placement, top, "before the hide");

        rig.HideKeyboard();
        var hidden = rig.Placement;
        Check.WithCustomMessage("with the keyboard hidden the card must return next to its anchor")
            .That(hidden.Window.IntersectsWith(top)).IsTrue();

        rig.ShowKeyboard();
        var keyboard = rig.KeyboardRectDip;
        AssertClearOfTheKeyboard(rig.Placement, keyboard, "after the re-show");
    }

    /// <summary>The sweep's three rules, on the real probe's rect: the card, every Edit hit cell
    /// and the number tag keep the 24 DIP clearance.</summary>
    private static void AssertClearOfTheKeyboard(NotePlacement placement, Rect keyboard, string when)
    {
        Check.WithCustomMessage($"{when}: the card {placement.Card} must clear the keyboard {keyboard}")
            .That(GapFrom(placement.Card, keyboard)).IsGreaterOrEqualThan(NoteLayout.Constants.AvoidGapDip);

        for (var slot = 0; slot < 4; slot++)
        {
            var (cx, cy) = NoteLayout.ButtonCenter(placement, slot);
            var cell = new Rect(
                placement.Window.X + cx - (LayoutConstants.NoteCellDip / 2),
                placement.Window.Y + cy - (LayoutConstants.NoteCellDip / 2),
                LayoutConstants.NoteCellDip, LayoutConstants.NoteCellDip);
            Check.WithCustomMessage($"{when}: cell {slot} {cell} must clear the keyboard {keyboard}")
                .That(GapFrom(cell, keyboard)).IsGreaterOrEqualThan(NoteLayout.Constants.AvoidGapDip);
        }

        var tag = NoteLayout.TagRect(placement, TextSizeDip);
        var tagRect = new Rect(placement.Window.X + tag.X, placement.Window.Y + tag.Y, tag.Width, tag.Height);
        Check.WithCustomMessage($"{when}: the number tag {tagRect} must clear the keyboard {keyboard}")
            .That(GapFrom(tagRect, keyboard)).IsGreaterOrEqualThan(NoteLayout.Constants.AvoidGapDip);
    }

    /// <summary>The production pair plus the note manager, on a virtual stage: a real keyboard
    /// window shown invisibly, positioned at the owner's x/edge, and a real probe reading it.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly double _savedScaleX;
        private readonly double _savedScaleY;
        private readonly KeyboardWindow _window;
        private readonly KeyboardViewModel _vm;
        private readonly IMessenger _messenger;
        private readonly KeyboardProbe _probe;

        public Rig()
        {
            WpfApplicationGate.EnsureApplicationWithTokens();
            _dispatcher = Dispatcher.CurrentDispatcher;
            _savedScaleX = DpiHelper.ScaleX;
            _savedScaleY = DpiHelper.ScaleY;
            _messenger = new WeakReferenceMessenger();

            var layout = KeyboardLayoutLoader.Load(Path.Combine(SourceScanner.RepoRoot().FullName,
                "src", "EyeCtrl.App", "Resources", "Layouts", "qwertz.json"));
            var settings = new EyeCtrlSettings();
            settings.Keyboard.ScreenEdge = "Top";
            _vm = new KeyboardViewModel(settings, _messenger);

            _window = new KeyboardWindow();
            _window.SetViewModel(_vm);
            _window.SetScreenEdge("Top");
            _window.LoadLayout(layout, 1.5, keyDwellMs: 450, predictionDwellMs: 800, cooldownMs: 0);
            InvisibleTestWindow.Prepare(_window);
            _window.Show();
            Pump();

            var dpi = VisualTreeHelper.GetDpi(_window);
            DpiHelper.Initialize(dpi.DpiScaleX, dpi.DpiScaleY);
            Scale = dpi.DpiScaleX;

            _window.Left = KeyboardXDip;
            _window.Top = 0;
            Pump();

            // Constructed BEFORE the show flip, as production constructs it once at startup.
            _probe = new KeyboardProbe(_vm, _dispatcher, _messenger, () => _window, () => null);

            Manager = new NoteManager(_messenger, hitTester: InactiveNoteHitTester.Instance,
                workAreaDip: () => WorkArea, noteSettings: new NoteSettings { TextSizeDip = TextSizeDip });
            Manager.SetKeyboardProbe(_probe);

            ShowKeyboard();
        }

        public double Scale { get; }

        public NoteManager Manager { get; }

        public PhysicalPoint Phys(Point dip) => new(dip.X * Scale, dip.Y * Scale);

        public NotePlacement Placement => Manager.PlacementForTest(Manager.ActiveNotes[0].Id)!;

        /// <summary>The keyboard's on-screen rect in DIP, through the SAME physical-to-DIP scale
        /// the note manager uses — the rect it derived against, not a hand-copied one.</summary>
        public Rect KeyboardRectDip
        {
            get
            {
                var rects = _probe.Rects();
                Check.WithCustomMessage("the real keyboard must be visible AND sourced for the probe")
                    .That(rects).Not.IsEmpty();
                var r = rects[0];
                return new Rect(
                    DpiHelper.PhysicalToLogicalX(r.X), DpiHelper.PhysicalToLogicalY(r.Y),
                    DpiHelper.PhysicalToLogicalX(r.X + r.Width) - DpiHelper.PhysicalToLogicalX(r.X),
                    DpiHelper.PhysicalToLogicalY(r.Y + r.Height) - DpiHelper.PhysicalToLogicalY(r.Y));
            }
        }

        /// <summary>The production show sequence (KeyboardCoordinator.SetKeyboardVisible): the VM
        /// flip, Show(), then the explicit show placement.</summary>
        public void ShowKeyboard()
        {
            _vm.IsVisible = true;
            _window.Show();
            _window.PlaceForShow(WorkArea, _window.ActualWidth, _window.ActualHeight);
            Pump();
        }

        /// <summary>The hide half: the VM flip and the window hide, as the coordinator does.</summary>
        public void HideKeyboard()
        {
            _vm.IsVisible = false;
            _window.Hide();
            Pump();
        }

        /// <summary>The production Flip half: the dwell's message, then the coordinator's
        /// SetScreenEdge on the Normal queue — the order the probe's Background raise runs after.</summary>
        public void FlipTo(string edge)
        {
            _messenger.Send(new KeyboardEdgeToggledMessage());
            _dispatcher.BeginInvoke(() => _window.SetScreenEdge(edge));
            Pump();
        }

        /// <summary>Drains the probe's Background raise and the window's layout/render work.</summary>
        private void Pump() => _dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

        public void Dispose()
        {
            Manager.Dispose();
            _probe.Dispose();
            _vm.IsVisible = false;
            _window.Hide();
            _window.Close();
            DpiHelper.Initialize(_savedScaleX, _savedScaleY);
        }
    }

    /// <summary>The distance between a window and a rect: zero when they touch or overlap.</summary>
    private static double GapFrom(Rect window, Rect rect)
    {
        double dx = Math.Max(rect.Left - window.Right, 0) + Math.Max(window.Left - rect.Right, 0);
        double dy = Math.Max(rect.Top - window.Bottom, 0) + Math.Max(window.Top - rect.Bottom, 0);
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
