namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// B4's composition pins: both banners reach the screen ONLY through the one bottom-centre slot,
/// a caregiver alert goes through PRE-EMPT (never the FIFO), the windows' Hidden events are what
/// advance the slot, and the restart notice's keep-clear reads the slot's live bounds.
///
/// <para>
/// The routing tests drive the production presenters over a recording slot, so no window is shown
/// (the recording slot never calls ShowInSlot). The windows are constructed only for their Hidden
/// event and are prepared invisible first; the composition-root checks are source pins and one
/// real-container test, the A3/A7 pattern for App wiring that cannot be composed headlessly.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class BannerSlotWiringTests
{
    private static readonly string AppData = TestAppDataDir.Create("banner-slot-wiring");

    private static ServiceProvider BuildProvider() =>
        AppBootstrapper.BuildServiceProvider(
            new EyeCtrlSettings(),
            settingsFilePath: Path.Combine(AppData, "settings.json"),
            dispatcher: Dispatcher.CurrentDispatcher,
            isTestMode: true,
            appDataDir: AppData);

    /// <summary>A slot that records the calls, without showing anything.</summary>
    private sealed class RecordingSlot : IBannerSlot
    {
        public List<IBannerSlotOccupant> Enqueued { get; } = new();
        public List<IBannerSlotOccupant> Preempted { get; } = new();
        public List<IBannerSlotOccupant> Withdrawn { get; } = new();
        public IBannerSlotOccupant? Live { get; set; }

        public void Enqueue(IBannerSlotOccupant banner)
        {
            Enqueued.Add(banner);
            Live ??= banner;
        }

        public void Preempt(IBannerSlotOccupant banner)
        {
            Preempted.Add(banner);
            Live = banner;
        }

        public void Withdraw(IBannerSlotOccupant banner)
        {
            Withdrawn.Add(banner);
            if (ReferenceEquals(Live, banner))
                Live = null;
        }

        public bool IsLive(IBannerSlotOccupant banner) => ReferenceEquals(Live, banner);

        public bool TryGetVisibleBounds(out PhysicalRect bounds)
        {
            bounds = default;
            return false;
        }

        public IReadOnlyList<PhysicalRect> LiveBoundsOrEmpty() => Array.Empty<PhysicalRect>();
    }

    [StaFact]
    public void TheDriftNotice_IsEnqueuedThroughTheSlot_AndNeverShownDirectly()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var slot = new RecordingSlot();
        var window = InvisibleTestWindow.Prepare(new DriftNoticeWindow());
        var presenter = new SlotDriftNoticePresenter(slot, window, canShow: static () => true);
        try
        {
            presenter.ShowDriftNotice();

            Check.WithCustomMessage("the drift notice must queue in the slot (FIFO), not show itself")
                .That(slot.Enqueued).ContainsExactly(presenter);
            Check.WithCustomMessage("the drift notice is not the pre-emptor")
                .That(slot.Preempted).IsEmpty();
            Check.WithCustomMessage("the presenter must not have shown a window outside the slot")
                .That(window.IsVisible).IsFalse();
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void TheCaregiverAlert_PreemptsThroughTheSlot_AndIsNeverQueuedBehindABanner()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var slot = new RecordingSlot();
        var window = InvisibleTestWindow.Prepare(new CaregiverAlertWindow());
        var presenter = new SlotCaregiverAlertPresenter(slot, window);
        try
        {
            presenter.ShowCaregiverAlert(CaregiverAlertCondition.SustainedStruggle);

            Check.WithCustomMessage("a caregiver alert takes the slot at once (pre-empt)")
                .That(slot.Preempted).ContainsExactly(presenter);
            Check.WithCustomMessage("a caregiver alert must never join the FIFO line")
                .That(slot.Enqueued).IsEmpty();
            Check.WithCustomMessage("the presenter must not have shown a window outside the slot")
                .That(window.IsVisible).IsFalse();
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void TheDriftWindowsHiddenEvent_ReachesTheSlotAsClosed()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = InvisibleTestWindow.Prepare(new DriftNoticeWindow());
        var presenter = new SlotDriftNoticePresenter(new BannerSlot(), window, canShow: static () => true);
        var closed = 0;
        presenter.Closed += (_, _) => closed++;
        try
        {
            window.HideDriftNotice();

            Check.WithCustomMessage("the slot advances on the window's Hidden event")
                .That(closed).IsEqualTo(1);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void TheCaregiverWindowsHiddenEvent_ReachesTheSlotAsClosed()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = InvisibleTestWindow.Prepare(new CaregiverAlertWindow());
        var presenter = new SlotCaregiverAlertPresenter(new BannerSlot(), window);
        var closed = 0;
        presenter.Closed += (_, _) => closed++;
        try
        {
            window.HideCaregiverAlert();

            Check.WithCustomMessage("the slot advances on the window's Hidden event")
                .That(closed).IsEqualTo(1);
        }
        finally { window.Close(); }
    }

    // B4 fixup-01 M1 (kills K9): the drift banner's ONLY natural closure is the fade completing.
    // Without its Hidden raise the slot keeps an invisible notice as live, and the next caregiver
    // alert displaces and re-shows that stale notice afterwards.
    [StaFact]
    public void TheDriftWindowsFadeCompletion_RaisesHidden()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = InvisibleTestWindow.Prepare(new DriftNoticeWindow());
        var hidden = 0;
        window.Hidden += (_, _) => hidden++;
        try
        {
            window.OnFadeCompleted(null, EventArgs.Empty);

            Check.WithCustomMessage("the fade completing must take the notice out of the slot")
                .That(hidden).IsEqualTo(1);
            Check.That(window.IsVisible).IsFalse();
        }
        finally { window.Close(); }
    }

    // B4 fixup-01 M1 (kills K10): without the caregiver fade's Hidden raise the slot keeps an
    // invisible alert as live, and every later drift notice queues behind it forever.
    [StaFact]
    public void TheCaregiverWindowsFadeCompletion_RaisesHidden()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = InvisibleTestWindow.Prepare(new CaregiverAlertWindow());
        var hidden = 0;
        window.Hidden += (_, _) => hidden++;
        try
        {
            window.OnFadeCompleted(null, EventArgs.Empty);

            Check.WithCustomMessage("the fade completing must take the alert out of the slot")
                .That(hidden).IsEqualTo(1);
            Check.That(window.IsVisible).IsFalse();
        }
        finally { window.Close(); }
    }

    // B4 fixup-01 L2: condition (a) now holds by construction — the windows no longer implement the
    // presenter interfaces, so nothing can resolve one as its presenter and bypass the slot.
    [Fact]
    public void TheBannerWindows_DoNotImplementThePresenterInterfaces()
    {
        Check.WithCustomMessage("DriftNoticeWindow must not be usable as IDriftNoticePresenter")
            .That(typeof(IDriftNoticePresenter).IsAssignableFrom(typeof(DriftNoticeWindow))).IsFalse();
        Check.WithCustomMessage("CaregiverAlertWindow must not be usable as ICaregiverAlertPresenter")
            .That(typeof(ICaregiverAlertPresenter).IsAssignableFrom(typeof(CaregiverAlertWindow))).IsFalse();
        Check.WithCustomMessage("the slot-routed wrapper is the presenter")
            .That(typeof(IDriftNoticePresenter).IsAssignableFrom(typeof(SlotDriftNoticePresenter))).IsTrue();
        Check.WithCustomMessage("the slot-routed wrapper is the presenter")
            .That(typeof(ICaregiverAlertPresenter).IsAssignableFrom(typeof(SlotCaregiverAlertPresenter))).IsTrue();
    }

    // B4 fixup-02 M1 (kills N10): the PRODUCTION canShow lambda, on the real container. With no
    // drift episode raised, ShowInSlot must raise Closed and leave the window hidden. A
    // `canShow: () => true` regression is review 01's H1 again; the prepared window would become
    // visible while the slot believed nothing was up.
    [StaFact]
    public void TheContainersPresenter_RefusesAnEpisodeLessNotice_WithoutShowingTheWindow()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var sp = BuildProvider();

        // Prepare the container's singleton window BEFORE the presenter is resolved.
        var window = InvisibleTestWindow.Prepare(sp.GetRequiredService<DriftNoticeWindow>());
        var occupant = (IBannerSlotOccupant)sp.GetRequiredService<IDriftNoticePresenter>();
        var closed = 0;
        occupant.Closed += (_, _) => closed++;
        try
        {
            occupant.ShowInSlot(); // no drift episode has been raised

            Check.WithCustomMessage("the production canShow must refuse a notice with no current episode")
                .That(closed).IsEqualTo(1);
            Check.WithCustomMessage("the refused notice must not have shown the prepared window")
                .That(window.IsVisible).IsFalse();
        }
        finally
        {
            window.Close();
        }
    }

    // B4 fixup-02 L1 (kills K9b/K10b): the handlers themselves are pinned above, but only the
    // SUBSCRIPTION wires a real fade to them. Dropping it strands an invisible occupant as live.
    [Fact]
    public void BothWindows_SubscribeTheirFadeCompletion_ToTheNamedHandler()
    {
        var drift = Source("src/EyeCtrl.App/Overlays/DriftNoticeWindow.xaml.cs");
        Check.WithCustomMessage(
                "DriftNoticeWindow's fade no longer subscribes OnFadeCompleted: the slot would keep " +
                "an invisible notice live and the next alert would re-show it")
            .That(Regex.IsMatch(drift, @"fade\s*\.\s*Completed\s*\+=\s*OnFadeCompleted\s*;")).IsTrue();

        var caregiver = Source("src/EyeCtrl.App/Overlays/CaregiverAlertWindow.xaml.cs");
        Check.WithCustomMessage(
                "CaregiverAlertWindow's fade no longer subscribes OnFadeCompleted: every later drift " +
                "notice would queue behind an invisible alert forever")
            .That(Regex.IsMatch(caregiver, @"fade\s*\.\s*Completed\s*\+=\s*OnFadeCompleted\s*;")).IsTrue();
    }

    [Fact]
    public void TheCompositionRoot_RoutesBothBannersThroughTheSlot()
    {
        var boot = Source("src/EyeCtrl.App/Services/AppBootstrapper.cs");

        Check.WithCustomMessage("the banner slot is not registered in the container")
            .That(Regex.IsMatch(boot, @"AddSingleton<BannerSlot>\s*\(\s*\)")).IsTrue();
        Check.WithCustomMessage("the drift presenter is no longer the slot-routed wrapper")
            .That(Regex.IsMatch(boot,
                @"AddSingleton<IDriftNoticePresenter>\s*\(\s*sp\s*=>\s*new\s+SlotDriftNoticePresenter\s*\(")).IsTrue();
        Check.WithCustomMessage("the caregiver presenter is no longer the slot-routed wrapper")
            .That(Regex.IsMatch(boot,
                @"AddSingleton<ICaregiverAlertPresenter>\s*\(\s*sp\s*=>\s*new\s+SlotCaregiverAlertPresenter\s*\(")).IsTrue();
        Check.WithCustomMessage(
                "a banner window is registered AS its presenter again: that show path bypasses the slot (B4)")
            .That(Regex.IsMatch(boot,
                @"AddSingleton<I(?:DriftNotice|CaregiverAlert)Presenter>\s*\(\s*sp\s*=>\s*sp\s*\.\s*GetRequiredService<\w+Window>")).IsFalse();
    }

    [Fact]
    public void TheRestartNotice_KeepsClearOfTheLiveBanner()
    {
        var app = Source("src/EyeCtrl.App/App.xaml.cs");
        var start = app.IndexOf("_restartNoticeHost = new RestartNoticeHost(", StringComparison.Ordinal);
        Check.WithCustomMessage("App.xaml.cs no longer builds the restart notice host").That(start).IsStrictlyGreaterThan(-1);

        var block = app[start..Math.Min(app.Length, start + 1600)];
        Check.WithCustomMessage(
                "the restart card's keepClear no longer includes the banner slot's live occupant (B4): " +
                "the card would sit over a drift notice or caregiver alert")
            .That(Regex.IsMatch(block,
                @"keepClear\s*:\s*\(\s*\)\s*=>\s*RestartNoticePlacement\s*\.\s*ToDip\([\s\S]{0,400}LiveBoundsOrEmpty\s*\(")).IsTrue();
    }

    private static string Source(string relative) =>
        File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
}
