namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Linq;
using System.Windows;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Input;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// C10's banner, headless: the window is built with the <c>present:</c> seam and never shown, so
/// the text and the two dwell targets are exercised without painting anything on the owner's
/// screen. The German assertions pin the wording the ticket sends for review.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class BlockedInputBannerWindowTests
{
    private static BlockedInputBannerWindow Headless(int dwellMs = 25) =>
        new(dwellMs: dwellMs, present: _ => { });

    private static BlockedInputState Blocked(
        BlockedInputReason reason, string? process = "Taskmgr.exe", bool elevated = true) =>
        new(
            IsBlocked: true,
            reason,
            Trigger: "test",
            TargetPoint: new PhysicalPoint(10, 10),
            CursorBefore: new PhysicalPoint(300, 300),
            CursorAfter: new PhysicalPoint(300, 300),
            Foreground: new ProcessTokenFacts(process, 0x3000, elevated, false, false),
            Target: ProcessTokenFacts.Unknown);

    [StaFact]
    public void ShowBlocked_WithAnElevatedForeground_NamesItInBothTexts()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(2.0, 2.0);
        var banner = Headless();

        banner.ShowBlocked(Blocked(BlockedInputReason.HigherIntegrityForeground));

        Check.That(banner.MessageText.Text).Contains("administrator");
        Check.That(banner.DetailText.Text).Contains("Taskmgr.exe");
        Check.That(banner.IsShowingForTest).IsTrue();
    }

    [Fact]
    public void TheGermanWording_IsTheTicketsDraft()
    {
        Check.That(GermanDraft.TextOf("overlays.blocked.elevated", string.Empty)).IsEqualTo(
            "Klicks werden blockiert: Ein Fenster mit Administratorrechten (z. B. Task-Manager) ist im Vordergrund.");
        Check.That(GermanDraft.TextOf("overlays.blocked.escape", string.Empty))
            .IsEqualTo("Zum Desktop / vorheriges Fenster");
        Check.That(GermanDraft.TextOf("overlays.blocked.dismiss", string.Empty)).IsEqualTo("Ausblenden");
    }

    [StaFact]
    public void ShowBlocked_WithoutIntegrityEvidence_SaysOnlyThatInputIsDiscarded()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(2.0, 2.0);
        var banner = Headless();

        banner.ShowBlocked(Blocked(BlockedInputReason.Unknown, process: null, elevated: false));

        Check.That(banner.MessageText.Text).Contains("discarding");
        Check.That(banner.DetailText.Text).IsEmpty();
    }

    [StaFact]
    public void ShowBlocked_RegistersTwoRectangularTargets_InsideTheWindow()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(2.0, 2.0);
        var banner = Headless();

        banner.ShowBlocked(Blocked(BlockedInputReason.HigherIntegrityForeground));

        var targets = banner.DwellForTest.Targets;
        Check.That(targets.Select(t => t.Id).OrderBy(id => id, StringComparer.Ordinal)).ContainsExactly(
            BlockedInputBannerWindow.DismissTargetId, BlockedInputBannerWindow.EscapeTargetId);
        Check.That(targets).ContainsOnlyElementsThatMatch(t => t.Bounds is not null);
        Check.That(targets).ContainsOnlyElementsThatMatch(t =>
            t.Bounds!.Value.Width > 0 && t.Bounds.Value.Height > 0);
        Check.That(targets).ContainsOnlyElementsThatMatch(t =>
            t.Bounds!.Value.X >= 0 && t.Bounds.Value.Y >= 0 &&
            t.Bounds.Value.X + t.Bounds.Value.Width <= banner.Width * 2.0);
    }

    [StaFact]
    public void DwellingTheEscapeButton_RaisesEscapeRequested_AndKeepsTheBannerUp()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(2.0, 2.0);
        var banner = Headless();
        var escapes = 0;
        banner.EscapeRequested += (_, _) => escapes++;
        banner.ShowBlocked(Blocked(BlockedInputReason.HigherIntegrityForeground));

        var target = banner.DwellForTest.Targets
            .Single(t => t.Id == BlockedInputBannerWindow.EscapeTargetId);
        FeedDwell(banner, target);

        Check.That(escapes).IsEqualTo(1);
        Check.That(banner.IsShowingForTest).IsTrue();
    }

    [StaFact]
    public void DwellingDismiss_HidesTheBannerAndRaisesDismissRequested()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(2.0, 2.0);
        var banner = Headless();
        var dismissals = 0;
        banner.DismissRequested += (_, _) => dismissals++;
        banner.ShowBlocked(Blocked(BlockedInputReason.Unknown, process: null, elevated: false));

        var target = banner.DwellForTest.Targets
            .Single(t => t.Id == BlockedInputBannerWindow.DismissTargetId);
        FeedDwell(banner, target);

        Check.That(dismissals).IsEqualTo(1);
        Check.That(banner.IsShowingForTest).IsFalse();
        Check.That(banner.DwellForTest.Targets).IsEmpty();
    }

    [StaFact]
    public void HideBlocked_DropsTheTargets_SoAHiddenBannerCannotCatchADwell()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(2.0, 2.0);
        var banner = Headless();
        banner.ShowBlocked(Blocked(BlockedInputReason.HigherIntegrityForeground));

        banner.HideBlocked();

        Check.That(banner.IsShowingForTest).IsFalse();
        Check.That(banner.DwellForTest.Targets).IsEmpty();
        Check.ThatCode(banner.HideBlocked).DoesNotThrow();
    }

    private static void FeedDwell(BlockedInputBannerWindow banner, DwellTarget target)
    {
        var center = target.Center;
        for (var ms = 0; ms <= 200; ms += 10)
            banner.OnGaze(center, ms * 1000L);
    }
}
