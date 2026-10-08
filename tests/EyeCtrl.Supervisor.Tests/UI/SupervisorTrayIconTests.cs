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
    public void TheProductionTexts_AreTheEnglishItems_UntilT06KeysThem()
    {
        Check.That(SupervisorTrayTexts.For(EyeCtrl.Core.Localization.StringTable.EmbeddedEnglish))
            .IsEqualTo(new SupervisorTrayTexts("EyeCtrl Supervisor settings", "Launch EyeCtrl now", "Exit supervisor"));
    }
}
