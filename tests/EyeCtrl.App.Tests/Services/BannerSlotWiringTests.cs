namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Text.RegularExpressions;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;

/// <summary>
/// B4's composition pins: both banners reach the screen ONLY through the one bottom-centre slot,
/// a caregiver alert goes through PRE-EMPT (never the FIFO), the windows' Hidden events are what
/// advance the slot, and the restart notice's keep-clear reads the slot's live bounds.
///
/// <para>
/// The routing tests drive the production presenters over a recording slot, so no window is shown
/// (the recording slot never calls ShowInSlot). The windows are constructed only for their Hidden
/// event and are prepared invisible first; the composition-root checks are source pins, the
/// A3/A7 pattern for App wiring that cannot be composed headlessly.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class BannerSlotWiringTests
{
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
        var presenter = new SlotDriftNoticePresenter(slot, window);
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
        var presenter = new SlotDriftNoticePresenter(new BannerSlot(), window);
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
