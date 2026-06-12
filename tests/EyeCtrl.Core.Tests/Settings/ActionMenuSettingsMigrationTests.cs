namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using Xunit;

public class ActionMenuSettingsMigrationTests
{
    [Fact]
    public void EnsureDefaults_AddsPinNote_WhenMissing()
    {
        var settings = new ActionMenuSettings();
        settings.VisibleActions.Remove(InteractionAction.PinNote);
        Assert.DoesNotContain(InteractionAction.PinNote, settings.VisibleActions);

        settings.EnsureDefaults();

        Assert.Contains(InteractionAction.PinNote, settings.VisibleActions);
    }

    [Fact]
    public void EnsureDefaults_DoesNotDuplicate_WhenAlreadyPresent()
    {
        var settings = new ActionMenuSettings();
        var count = settings.VisibleActions.Count;

        settings.EnsureDefaults();

        Assert.Equal(count, settings.VisibleActions.Count);
    }
}
