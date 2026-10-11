namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Linq;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using NFluent;
using Xunit;

/// <summary>
/// T06 fixup-01 F1: the App tray's moved English needs literal pins at its site — key coverage
/// and the unused-key gate cannot see a key used in the wrong slot. English literals on the
/// expected side by design: resolving both sides from the keys would let a missing key pass as
/// its own raw text.
/// </summary>
public class AppTrayTextTests
{
    [Fact]
    public void TheProductionTexts_ResolveTheEnglishItemsFromTheTable()
    {
        Check.That(AppTrayTexts.For(StringTable.EmbeddedEnglish))
            .IsEqualTo(new AppTrayTexts(
                "Pause", "Resume", "Settings", "Reset adaptation", "Exit", "Show toolbar"));
    }

    [Fact]
    public void ThePauseFace_IsResumeOnlyWhilePaused()
    {
        var texts = AppTrayTexts.For(StringTable.EmbeddedEnglish);

        Check.That(texts.PauseItemText(InteractionState.Paused)).IsEqualTo("Resume");
        foreach (var state in Enum.GetValues<InteractionState>().Where(s => s != InteractionState.Paused))
        {
            Check.WithCustomMessage($"{state} shows the wrong pause face")
                .That(texts.PauseItemText(state)).IsEqualTo("Pause");
        }
    }
}
