namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// The restart notice card must NEVER cover the gaze keyboard (holistic review F5). The caregiver
/// alert shares the bottom-centre spot and ignores the keyboard (click-through, no dwell target of
/// its own); this card has a dwell button, so over the keys it would hide them and be dwelled by a
/// gaze meant for a key. These tests build the REAL keyboard at the user's key sizes, dock it at the
/// bottom (and the top) of a 1920 x 1152 DIP work area (the user's 3840 x 2304 at 2.0), and check the
/// card, its dwell button and every key against each other.
/// </summary>
[Collection("WpfApplication")]
public class RestartNoticePlacementTests
{
    private const int WsPopup = unchecked((int)0x80000000);

    /// <summary>The user's screen at 2.0 less the taskbar, in DIP (the fit harness's reference).</summary>
    private static readonly Rect WorkArea = new(0, 0, 1920, 1152);

    internal sealed record KeyboardProbe(Rect Chrome, IReadOnlyDictionary<string, Rect> Keys);

    private static string EmojiJsonPath() =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Emojis", "emojis.json");

    /// <summary>
    /// The laid-out keyboard at a <c>KeySizeMultiplier</c>: its chrome rectangle's SIZE and every
    /// key's rectangle relative to the chrome's top-left, in DIP (the fixture hosts it with
    /// physical == DIP, as the keyboard tests do; the position is then supplied by the caller).
    /// </summary>
    internal static KeyboardProbe MeasureKeyboard(double keySizeMultiplier)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        var window = new KeyboardWindow();
        window.SetViewModel(vm);
        window.LoadLayout(
            KeyboardLayoutLoader.Load(Path.Combine(
                SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Resources", "Layouts", "qwertz.json")),
            keySizeMultiplier, keyDwellMs: 450, predictionDwellMs: 500, cooldownMs: 400);
        window.SetEmojiData(
            EmojiJsonPath(),
            Path.Combine(Path.GetTempPath(), "eyectrl_t15_place_" + Guid.NewGuid().ToString("N") + ".json"));

        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var source = new HwndSource(new HwndSourceParameters("t15-place-probe")
        {
            WindowStyle = WsPopup, PositionX = -30000, PositionY = -30000, Width = 1, Height = 1,
        });
        try
        {
            var factor = 1.0 / source.CompositionTarget!.TransformToDevice.M11;
            content.LayoutTransform = new ScaleTransform(factor, factor);
            source.RootVisual = content;
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            content.Arrange(new Rect(content.DesiredSize));
            content.UpdateLayout();

            Rect OnScreen(FrameworkElement element)
            {
                var topLeft = element.PointToScreen(new Point(0, 0));
                var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
                return new Rect(topLeft, bottomRight);
            }

            var chrome = OnScreen(content);
            var visuals = (Dictionary<string, (Border Container, TextBlock? Label)>)typeof(KeyboardWindow)
                .GetField("_keyVisuals", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            Check.WithCustomMessage("the keyboard built no keys, so every assertion would be vacuous")
                .That(visuals.Count).IsStrictlyGreaterThan(30);

            var keys = visuals.ToDictionary(
                pair => pair.Key,
                pair =>
                {
                    var r = OnScreen(pair.Value.Container);
                    return new Rect(r.X - chrome.X, r.Y - chrome.Y, r.Width, r.Height);
                });
            return new KeyboardProbe(new Rect(0, 0, chrome.Width, chrome.Height), keys);
        }
        finally
        {
            source.Dispose();
            window.Close();
        }
    }

    private static Rect ButtonRect(RestartNoticeWindow card)
    {
        var inWindow = card.CloseButton.TransformToAncestor((Visual)card.Content)
            .TransformBounds(new Rect(card.CloseButton.RenderSize));
        return new Rect(card.Left + inWindow.X, card.Top + inWindow.Y, inWindow.Width, inWindow.Height);
    }

    private const string GermanRefusal =
        "Neustart fehlgeschlagen: Das neue EyeCtrl ist nicht rechtzeitig gestartet. EyeCtrl läuft weiter.";

    private static readonly string GermanInProgress =
        "EyeCtrl wird neu gestartet.\n"
        + "Das neue EyeCtrl startet gerade; klappt das nicht, läuft dieses weiter.\n"
        + "Ausblenden hält den Neustart nicht an.";

    [StaTheory]
    [InlineData(0.5, "bottom")]
    [InlineData(1.0, "bottom")]
    [InlineData(1.5, "bottom")]
    [InlineData(2.0, "bottom")]
    [InlineData(1.5, "top")]
    [InlineData(2.0, "top")]
    public void WithTheKeyboardShown_TheCardAndItsButtonNeverTouchAKey(double multiplier, string edge)
    {
        var keyboard = MeasureKeyboard(multiplier);
        var (left, top) = OverlayPositioning.PlaceAtEdge(WorkArea, edge, keyboard.Chrome.Width, keyboard.Chrome.Height);
        var chrome = new Rect(left, top, keyboard.Chrome.Width, keyboard.Chrome.Height);

        foreach (var text in new[] { GermanRefusal, GermanInProgress })
        {
            var card = new RestartNoticeWindow(keepClear: () => [chrome]);
            try
            {
                card.SetText(text);
                card.Prepare(WorkArea);
                card.Measure(new Size(card.Width, card.Height));
                card.Arrange(new Rect(0, 0, card.Width, card.Height));
                card.UpdateLayout();

                var cardRect = new Rect(card.Left, card.Top, card.Width, card.Height);
                var button = ButtonRect(card);

                Check.WithCustomMessage($"the card lies inside the work area (keyboard {multiplier}x, {edge})")
                    .That(WorkArea.Contains(cardRect)).IsTrue();
                Check.WithCustomMessage(
                        $"the card {cardRect} covers the keyboard {chrome} (keyboard {multiplier}x, {edge})")
                    .That(cardRect.IntersectsWith(chrome)).IsFalse();

                // Key by key, as the user's gaze would meet them: the card and the button, against
                // every key's rectangle in screen position.
                var touched = keyboard.Keys
                    .Where(pair =>
                    {
                        var key = new Rect(left + pair.Value.X, top + pair.Value.Y, pair.Value.Width, pair.Value.Height);
                        return key.IntersectsWith(cardRect) || key.IntersectsWith(button);
                    })
                    .Select(pair => pair.Key)
                    .ToList();
                Check.WithCustomMessage($"the card or its button touches key(s): {string.Join(", ", touched)}")
                    .That(touched).IsEmpty();
            }
            finally
            {
                card.Close();
            }
        }
    }

    /// <summary>
    /// Review R2: a toolbar docked to the BOTTOM sits inside the work area, and the card's resting
    /// place (centred, 48 DIP up) overlapped it by 30-40 DIP, two dwell targets under one gaze. The
    /// zones the activation offer already keeps clear of include the toolbar, so the card does too.
    /// </summary>
    [StaTheory]
    [InlineData(112.0, 700.0)]
    [InlineData(112.0, 1400.0)]
    [InlineData(150.0, 900.0)]
    public void WithABottomDockedToolbar_TheCardAndItsButtonStayClearOfIt(double toolbarHeight, double toolbarWidth)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var (tl, tt) = OverlayPositioning.PlaceAtEdge(WorkArea, "bottom", toolbarWidth, toolbarHeight);
        var toolbar = new Rect(tl, tt, toolbarWidth, toolbarHeight);

        foreach (var text in new[] { GermanRefusal, GermanInProgress })
        {
            var card = new RestartNoticeWindow(keepClear: () => [toolbar]);
            try
            {
                card.SetText(text);
                card.Prepare(WorkArea);
                card.Measure(new Size(card.Width, card.Height));
                card.Arrange(new Rect(0, 0, card.Width, card.Height));
                card.UpdateLayout();

                var cardRect = new Rect(card.Left, card.Top, card.Width, card.Height);
                Check.WithCustomMessage($"the card {cardRect} covers the bottom-docked toolbar {toolbar}")
                    .That(cardRect.IntersectsWith(toolbar)).IsFalse();
                Check.WithCustomMessage($"the button {ButtonRect(card)} covers the bottom-docked toolbar {toolbar}")
                    .That(ButtonRect(card).IntersectsWith(toolbar)).IsFalse();
                Check.That(WorkArea.Contains(cardRect)).IsTrue();
            }
            finally
            {
                card.Close();
            }
        }
    }

    [StaFact]
    public void WithTheKeyboardAndABottomToolbarBoth_TheCardClearsBoth()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        // A keyboard above a bottom toolbar (the keyboard yields to the toolbar's extent).
        var toolbar = new Rect(610, 1040, 700, 112);
        var keyboard = new Rect(200, 560, 1520, 460);
        var card = new RestartNoticeWindow(keepClear: () => [toolbar, keyboard]);
        try
        {
            card.SetText(GermanRefusal);
            card.Prepare(WorkArea);
            var cardRect = new Rect(card.Left, card.Top, card.Width, card.Height);

            Check.That(cardRect.IntersectsWith(toolbar)).IsFalse();
            Check.That(cardRect.IntersectsWith(keyboard)).IsFalse();
            Check.That(WorkArea.Contains(cardRect)).IsTrue();
        }
        finally
        {
            card.Close();
        }
    }

    [StaFact]
    public void WithNoChrome_TheCardSitsAtTheBottomCentre()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var card = new RestartNoticeWindow(keepClear: () => []);
        try
        {
            card.SetText(GermanRefusal);
            card.Prepare(WorkArea);

            Check.That(card.Left).IsEqualTo((1920 - card.Width) / 2);
            Check.That(card.Top + card.Height).IsEqualTo(1152 - RestartNoticePlacement.MarginDip);
        }
        finally
        {
            card.Close();
        }
    }

    [Fact]
    public void ToDip_ConvertsThePublishedPhysicalZones_AndDropsEmptyOnes()
    {
        var scale = (EyeCtrl.Core.Services.DpiHelper.ScaleX, EyeCtrl.Core.Services.DpiHelper.ScaleY);
        try
        {
            EyeCtrl.Core.Services.DpiHelper.Update(2.0, 2.0);
            var dip = RestartNoticePlacement.ToDip(
            [
                new EyeCtrl.Core.Models.PhysicalRect(400, 200, 1000, 600),
                default,
            ]);

            Check.That(dip).HasSize(1);
            Check.That(dip[0]).IsEqualTo(new Rect(200, 100, 500, 300));
        }
        finally
        {
            EyeCtrl.Core.Services.DpiHelper.Update(scale.ScaleX, scale.ScaleY);
        }
    }

    [StaFact]
    public void TheCardFollowsTheChrome_WhenItAppearsOrGoesWhileTheCardIsUp()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        IReadOnlyList<Rect> chrome = [];
        var card = new RestartNoticeWindow(keepClear: () => chrome);
        try
        {
            card.SetText(GermanRefusal);
            card.Prepare(WorkArea);
            var bottomTop = card.Top;
            Check.WithCustomMessage("nothing moved, so nothing is re-placed")
                .That(card.ReplaceIfChromeMoved(WorkArea)).IsFalse();

            // The keyboard opens at the bottom, under the card's resting place.
            chrome = [new Rect(300, 700, 1320, 420)];
            Check.That(card.ReplaceIfChromeMoved(WorkArea)).IsTrue();
            Check.That(card.Top + card.Height).IsEqualTo(700 - RestartNoticePlacement.KeepClearGapDip);
            Check.That(card.ReplaceIfChromeMoved(WorkArea)).IsFalse();

            // And closes again: the card goes back to the bottom.
            chrome = [];
            Check.That(card.ReplaceIfChromeMoved(WorkArea)).IsTrue();
            Check.That(card.Top).IsEqualTo(bottomTop);
        }
        finally
        {
            card.Close();
        }
    }

    [Fact]
    public void Place_PrefersTheBottom_ThenTheNearestSpotAboveOrBelowTheChrome()
    {
        var card = new Size(920, 300);
        var area = new Rect(0, 0, 1920, 1152);
        var bottomTop = 1152 - 300 - RestartNoticePlacement.MarginDip;
        var gap = RestartNoticePlacement.KeepClearGapDip;

        Check.That(RestartNoticePlacement.Place(area, card, []).Top).IsEqualTo(bottomTop);
        // Chrome far from the card's resting place changes nothing.
        Check.That(RestartNoticePlacement.Place(area, card, [new Rect(0, 0, 1920, 200)]).Top).IsEqualTo(bottomTop);
        // Docked at the bottom: just above it, a gap clear.
        Check.That(RestartNoticePlacement.Place(area, card, [new Rect(200, 700, 1520, 452)]).Top)
            .IsEqualTo(700 - gap - 300);
        // Docked at the top, and tall: the bottom spot is clear, so the card stays there.
        Check.That(RestartNoticePlacement.Place(area, card, [new Rect(200, 0, 1520, 500)]).Top).IsEqualTo(bottomTop);
        // Tall enough that the bottom spot is blocked and there is no room above: below it.
        Check.That(RestartNoticePlacement.Place(area, card, [new Rect(200, 300, 1520, 500)]).Top)
            .IsEqualTo(800 + gap);
        // A toolbar along the bottom edge: the card rises above it.
        Check.That(RestartNoticePlacement.Place(area, card, [new Rect(610, 1040, 700, 112)]).Top)
            .IsEqualTo(1040 - gap - 300);
        // Chrome that fills the screen leaves nowhere clear: the card falls back to the top.
        Check.That(RestartNoticePlacement.Place(area, card, [new Rect(0, 0, 1920, 1152)]).Top).IsEqualTo(0);
    }
}
