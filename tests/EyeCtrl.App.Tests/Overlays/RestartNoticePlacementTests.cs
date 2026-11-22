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
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
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

    /// <summary>The full primary screen, in DIP: the card may use the taskbar strip, never the keys.</summary>
    private static readonly Rect ScreenArea = new(0, 0, 1920, 1200);

    internal sealed record KeyboardProbe(Rect Chrome, IReadOnlyDictionary<string, Rect> Keys);

    private static string EmojiJsonPath() =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Emojis", "emojis.json");

    /// <summary>
    /// The laid-out keyboard at a <c>KeySizeMultiplier</c> and sub-layout: its chrome rectangle's
    /// SIZE and every key's rectangle relative to the chrome's top-left, in DIP (the fixture hosts
    /// it with physical == DIP, as the keyboard tests do; the position is then supplied by the
    /// caller).
    /// </summary>
    internal static KeyboardProbe MeasureKeyboard(double keySizeMultiplier, string page = "letters")
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
        vm.SwitchSubLayout(page);

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

    public static IEnumerable<object[]> ShownKeyboardCases()
    {
        // The old letters-page cases, kept; then every multiplier/page/dock F17's fixup pins.
        yield return [0.5, "letters", "bottom"];
        yield return [1.0, "letters", "bottom"];
        foreach (var multiplier in new[] { 1.5, 1.75, 2.0 })
            foreach (var page in new[] { "letters", "function" })
                foreach (var dock in new[] { "bottom", "top" })
                    yield return [multiplier, page, dock];
    }

    [StaTheory]
    [MemberData(nameof(ShownKeyboardCases))]
    public void WithTheKeyboardShown_TheCardAndItsButtonNeverTouchAKey(
        double multiplier, string page, string edge)
    {
        var keyboard = MeasureKeyboard(multiplier, page);
        var (left, top) = OverlayPositioning.PlaceAtEdge(WorkArea, edge, keyboard.Chrome.Width, keyboard.Chrome.Height);
        var chrome = new Rect(left, top, keyboard.Chrome.Width, keyboard.Chrome.Height);
        // 2.0x on the Fn page already overlapped on main (978 DIP keyboard, 174 DIP left); the
        // fixup's wider-card and full-screen passes cannot fit the 283 DIP card either. It is
        // pinned explicitly below, never skipped silently.
        var mustClear = !(page == "function" && multiplier >= 2.0);

        foreach (var text in new[] { GermanRefusal, GermanInProgress })
        {
            var card = new RestartNoticeWindow(keepClear: () => [chrome]);
            try
            {
                card.SetText(text);
                card.Prepare(WorkArea, ScreenArea);
                card.Measure(new Size(card.Width, card.Height));
                card.Arrange(new Rect(0, 0, card.Width, card.Height));
                card.UpdateLayout();

                var cardRect = new Rect(card.Left, card.Top, card.Width, card.Height);
                var button = ButtonRect(card);
                var what = $"keyboard {multiplier}x/{page}/{edge}, card '{text.Split('\n')[0]}'";

                Check.WithCustomMessage($"the card lies inside the screen ({what})")
                    .That(ScreenArea.Contains(cardRect)).IsTrue();

                if (!mustClear)
                {
                    Check.WithCustomMessage(
                            $"2.0x Fn is the known remaining overlap on main too ({what}); the "
                            + "fixup cannot clear it without shrinking the card below the text floor.")
                        .That(cardRect.IntersectsWith(chrome)).IsTrue();
                    Check.WithCustomMessage($"the known 2.0x Fn case must use the old fallback ({what})")
                        .That(card.LastPlacementPass)
                        .IsEqualTo(RestartNoticeWindow.PlacementPass.Fallback);
                    continue;
                }

                // A work-area pass must stay inside it; the full-screen pass may use the taskbar
                // strip (screen containment is asserted above), never the keys.
                if (card.LastPlacementPass is RestartNoticeWindow.PlacementPass.DefaultWorkArea
                    or RestartNoticeWindow.PlacementPass.WiderCardWorkArea)
                {
                    Check.WithCustomMessage($"the work-area card lies inside the work area ({what})")
                        .That(WorkArea.Contains(cardRect)).IsTrue();
                }
                Check.WithCustomMessage($"the card fell back over the chrome ({what})")
                    .That(card.LastPlacementPass)
                    .Not.IsEqualTo(RestartNoticeWindow.PlacementPass.Fallback);
                Check.WithCustomMessage(
                        $"the card {cardRect} covers the keyboard {chrome} ({what})")
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
                Check.WithCustomMessage($"the card or its button touches key(s): {string.Join(", ", touched)} ({what})")
                    .That(touched).IsEmpty();
            }
            finally
            {
                card.Close();
            }
        }
    }

    /// <summary>
    /// The full-screen pass (b) needs its own case: a long body that still wraps to two lines at
    /// the widest card keeps the card at 315 DIP, which cannot fit the 289 DIP below a top-docked
    /// 1.75x Fn keyboard on the 1152 DIP work area, but does fit on the 1200 DIP screen (the
    /// taskbar strip). Without pass (b) the card falls back over the keys.
    /// </summary>
    [StaFact]
    public void WhenOnlyTheFullScreenFits_TheTaskbarStripIsUsed_NeverTheKeys()
    {
        var keyboard = MeasureKeyboard(1.75, "function");
        var (left, top) = OverlayPositioning.PlaceAtEdge(WorkArea, "top", keyboard.Chrome.Width, keyboard.Chrome.Height);
        var chrome = new Rect(left, top, keyboard.Chrome.Width, keyboard.Chrome.Height);
        var longBody = string.Concat(Enumerable.Repeat("Das neue EyeCtrl startet gerade; ", 6));

        var card = new RestartNoticeWindow(keepClear: () => [chrome]);
        try
        {
            card.SetText("EyeCtrl wird neu gestartet.\n" + longBody);
            card.Prepare(WorkArea, ScreenArea);
            card.Measure(new Size(card.Width, card.Height));
            card.Arrange(new Rect(0, 0, card.Width, card.Height));
            card.UpdateLayout();

            var cardRect = new Rect(card.Left, card.Top, card.Width, card.Height);
            Check.WithCustomMessage("the card must use the full-screen pass, not the old fallback")
                .That(card.LastPlacementPass)
                .IsEqualTo(RestartNoticeWindow.PlacementPass.WiderCardScreen);
            Check.WithCustomMessage($"the card {cardRect} covers the keyboard {chrome}")
                .That(cardRect.IntersectsWith(chrome)).IsFalse();
            Check.WithCustomMessage("the card only fits by covering part of the taskbar strip")
                .That(cardRect.Bottom).IsStrictlyGreaterThan(WorkArea.Bottom);
            Check.That(ScreenArea.Contains(cardRect)).IsTrue();
        }
        finally
        {
            card.Close();
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
                card.Prepare(WorkArea, ScreenArea);
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
            card.Prepare(WorkArea, ScreenArea);
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
            card.Prepare(WorkArea, ScreenArea);

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
            card.Prepare(WorkArea, ScreenArea);
            var bottomTop = card.Top;
            Check.WithCustomMessage("nothing moved, so nothing is re-placed")
                .That(card.ReplaceIfChromeMoved(WorkArea, ScreenArea)).IsFalse();

            // The keyboard opens at the bottom, under the card's resting place.
            chrome = [new Rect(300, 700, 1320, 420)];
            Check.That(card.ReplaceIfChromeMoved(WorkArea, ScreenArea)).IsTrue();
            Check.That(card.Top + card.Height).IsEqualTo(700 - RestartNoticePlacement.KeepClearGapDip);
            Check.That(card.ReplaceIfChromeMoved(WorkArea, ScreenArea)).IsFalse();

            // And closes again: the card goes back to the bottom.
            chrome = [];
            Check.That(card.ReplaceIfChromeMoved(WorkArea, ScreenArea)).IsTrue();
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

    [Fact]
    public void WhenTheFullGapCannotFit_TheCardStillStaysClearOfTheTallerKeyboard()
    {
        // F17's 2.0x top-docked keyboard is 834 DIP tall, leaving 318 DIP below it on the
        // taskbar-reduced work area: 9 short of the card plus the 24 DIP clear gap. The card must
        // take the minimal separation below the keyboard, not fall back over it.
        var card = new Size(1000, 315);
        var area = new Rect(0, 0, 1920, 1152);
        var keyboard = new Rect(27, 0, 1866, 834);

        var (left, top) = RestartNoticePlacement.Place(area, card, [keyboard]);
        var rect = new Rect(left, top, card.Width, card.Height);

        Check.WithCustomMessage($"the card {rect} covers the keyboard {keyboard}")
            .That(rect.IntersectsWith(keyboard)).IsFalse();
        Check.That(rect.Bottom).IsLessOrEqualThan(area.Bottom);
        Check.WithCustomMessage("the card must sit below the keyboard with the minimal separation")
            .That(top).IsEqualTo(keyboard.Bottom + RestartNoticePlacement.MinimumClearGapDip);
    }

    // B4: the bottom-centre banner slot's live occupant is part of the card's keep-clear set, so a
    // drift notice or caregiver alert sharing the resting spot never ends up underneath the card
    // (whose close button would then be dwelled over the banner, and hide it).
    [StaFact]
    public void WithALiveBannerSlotOccupant_TheCardAndItsButtonStayClearOfIt()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var banner = BannerAtRestingPlace();
        var slot = new BannerSlot();
        slot.Enqueue(new RestartBannerOccupant(Physical(banner)));
        var card = new RestartNoticeWindow(keepClear: () => RestartNoticePlacement.ToDip(slot.LiveBoundsOrEmpty()));
        try
        {
            card.SetText(GermanRefusal);
            card.Prepare(WorkArea, ScreenArea);
            card.Measure(new Size(card.Width, card.Height));
            card.Arrange(new Rect(0, 0, card.Width, card.Height));
            card.UpdateLayout();

            var cardRect = new Rect(card.Left, card.Top, card.Width, card.Height);
            Check.WithCustomMessage($"the card {cardRect} covers the live banner {banner}")
                .That(cardRect.IntersectsWith(banner)).IsFalse();
            Check.WithCustomMessage($"the button {ButtonRect(card)} covers the live banner {banner}")
                .That(ButtonRect(card).IntersectsWith(banner)).IsFalse();
            Check.That(WorkArea.Contains(cardRect)).IsTrue();
        }
        finally
        {
            card.Close();
        }
    }

    [StaFact]
    public void TheCardFollowsALiveBanner_WhenItTakesTheSlotAndWhenItLeaves()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var banner = BannerAtRestingPlace();
        var slot = new BannerSlot();
        var card = new RestartNoticeWindow(keepClear: () => RestartNoticePlacement.ToDip(slot.LiveBoundsOrEmpty()));
        try
        {
            card.SetText(GermanRefusal);
            card.Prepare(WorkArea, ScreenArea);
            var bottomTop = card.Top;

            var occupant = new RestartBannerOccupant(Physical(banner));
            slot.Enqueue(occupant);
            Check.WithCustomMessage("a banner taking the slot must move the card")
                .That(card.ReplaceIfChromeMoved(WorkArea, ScreenArea)).IsTrue();
            Check.WithCustomMessage("the card must rise clear of the live banner")
                .That(card.Top + card.Height).IsEqualTo(banner.Top - RestartNoticePlacement.KeepClearGapDip);
            Check.That(new Rect(card.Left, card.Top, card.Width, card.Height).IntersectsWith(banner)).IsFalse();

            occupant.Close();
            Check.WithCustomMessage("the card returns to the bottom when the banner is gone")
                .That(card.ReplaceIfChromeMoved(WorkArea, ScreenArea)).IsTrue();
            Check.That(card.Top).IsEqualTo(bottomTop);
        }
        finally
        {
            card.Close();
        }
    }

    /// <summary>The drift notice's resting place: 560 x 72 DIP, centred, 48 DIP above the bottom.</summary>
    private static Rect BannerAtRestingPlace() =>
        new((WorkArea.Width - 560) / 2, WorkArea.Bottom - 72 - RestartNoticePlacement.MarginDip, 560, 72);

    /// <summary>The work area is DIP; the slot publishes physical pixels, so convert at the live scale.</summary>
    private static PhysicalRect Physical(Rect dip) => new(
        EyeCtrl.Core.Services.DpiHelper.LogicalToPhysicalX(dip.X),
        EyeCtrl.Core.Services.DpiHelper.LogicalToPhysicalY(dip.Y),
        EyeCtrl.Core.Services.DpiHelper.LogicalToPhysicalX(dip.Width),
        EyeCtrl.Core.Services.DpiHelper.LogicalToPhysicalY(dip.Height));

    private sealed class RestartBannerOccupant(PhysicalRect bounds) : IBannerSlotOccupant
    {
        public event EventHandler? Closed;

        public void ShowInSlot() { }

        public void HideFromSlot() { }

        public bool TryGetVisibleBounds(out PhysicalRect actual)
        {
            actual = bounds;
            return true;
        }

        public void Close() => Closed?.Invoke(this, EventArgs.Empty);
    }
}
