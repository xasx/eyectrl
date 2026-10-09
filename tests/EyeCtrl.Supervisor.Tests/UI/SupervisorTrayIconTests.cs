namespace EyeCtrl.Supervisor.Tests.UI;

using System.ComponentModel;
using System.Reflection;
using EyeCtrl.Supervisor.UI;

/// <summary>
/// The tray menu re-asks its item texts every time it opens (T05b), so the items follow a language
/// change without a Supervisor restart. The icon is constructed invisible — nothing appears in the
/// tray — and the menu's Opening is raised through its own <c>OnOpening</c>, without showing it.
/// </summary>
public class SupervisorTrayIconTests
{
    private static void RaiseOpening(SupervisorTrayIcon tray)
    {
        var onOpening = tray.Menu.GetType().GetMethod(
            "OnOpening", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(CancelEventArgs)])!;
        onOpening.Invoke(tray.Menu, [new CancelEventArgs()]);
    }

    [Fact]
    public void TheMenuTexts_AreReadAgainEveryTimeTheMenuOpens()
    {
        var texts = new SupervisorTrayTexts("settings 1", "launch 1", "exit 1");
        using var tray = new SupervisorTrayIcon(() => { }, () => { }, () => { }, () => texts, visible: false);
        Check.That(tray.ItemTexts).IsEqualTo(("settings 1", "launch 1", "exit 1"));

        texts = new SupervisorTrayTexts("Einstellungen", "Jetzt starten", "Beenden");   // the language changed
        Check.That(tray.ItemTexts).IsEqualTo(("settings 1", "launch 1", "exit 1"));      // not before it opens

        RaiseOpening(tray);

        Check.That(tray.ItemTexts).IsEqualTo(("Einstellungen", "Jetzt starten", "Beenden"));
    }

    [Fact]
    public void AFailingTextProvider_KeepsTheTextsTheMenuShows()
    {
        var fail = false;
        using var tray = new SupervisorTrayIcon(
            () => { }, () => { }, () => { },
            () => fail ? throw new InvalidOperationException("table unreadable") : new SupervisorTrayTexts("a", "b", "c"),
            visible: false);

        fail = true;
        RaiseOpening(tray);

        Check.That(tray.ItemTexts).IsEqualTo(("a", "b", "c"));
    }

    [Fact]
    public void TheProductionTexts_ResolveTheEnglishItemsFromTheTable()
    {
        // English literals on the expected side by design (T06): resolving both sides from the
        // keys would let a missing or mis-pasted key pass as its own raw text.
        Check.That(SupervisorTrayTexts.For(EyeCtrl.Core.Localization.StringTable.EmbeddedEnglish))
            .IsEqualTo(new SupervisorTrayTexts("EyeCtrl Supervisor settings", "Launch EyeCtrl now", "Exit supervisor"));
    }

    // ── T06 fixup-01 F2: the tooltip refreshes at hover (throttled) and menu open, never ──────
    // ── from the supervision loop ─────────────────────────────────────────────────────────────

    [Fact]
    public void HoverRefresh_IsThrottled_AndShowsTheNewText()
    {
        var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        var calls = 0;
        string? status = "one";
        using var tray = new SupervisorTrayIcon(
            () => { }, () => { }, () => { },
            () => new SupervisorTrayTexts("a", "b", "c"),
            visible: false,
            refreshStatus: () => { calls++; return status; },
            utcNow: () => now);

        Check.That(tray.ApplyHoverRefresh()).IsEqualTo("one");
        Check.That(calls).IsEqualTo(1);
        Check.That(tray.StatusText).IsEqualTo("one");

        // MouseMove repeats while the pointer moves: within 2 s the throttle swallows it.
        now = now.AddSeconds(1);
        Check.That(tray.ApplyHoverRefresh()).IsNull();
        Check.That(calls).IsEqualTo(1);

        // The language changed on disk (the provider now answers "two"); the next hover past the
        // throttle shows it, without any state change.
        status = "two";
        now = now.AddSeconds(1);
        Check.That(tray.ApplyHoverRefresh()).IsEqualTo("two");
        Check.That(calls).IsEqualTo(2);
        Check.That(tray.StatusText).IsEqualTo("two");
    }

    [Fact]
    public void MenuOpening_RefreshesTheStatus_Unthrottled()
    {
        var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        var calls = 0;
        using var tray = new SupervisorTrayIcon(
            () => { }, () => { }, () => { },
            () => new SupervisorTrayTexts("a", "b", "c"),
            visible: false,
            refreshStatus: () => { calls++; return "status " + calls; },
            utcNow: () => now);

        tray.ApplyHoverRefresh();
        Check.That(calls).IsEqualTo(1);

        RaiseOpening(tray);   // the menu is a display moment; not swallowed by the hover throttle
        Check.That(calls).IsEqualTo(2);
        Check.That(tray.StatusText).IsEqualTo("status 2");
    }

    [Fact]
    public void AFailingStatusProvider_KeepsTheCurrentTooltip()
    {
        using var tray = new SupervisorTrayIcon(
            () => { }, () => { }, () => { },
            () => new SupervisorTrayTexts("a", "b", "c"),
            visible: false,
            refreshStatus: () => throw new InvalidOperationException("table unreadable"),
            utcNow: () => DateTime.UtcNow);

        Check.That(tray.ApplyStatusRefresh()).IsNull();
        Check.That(tray.StatusText).IsEqualTo("EyeCtrl Supervisor");   // the pre-status default
    }
}
