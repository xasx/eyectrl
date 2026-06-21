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
        Check.That(settings.VisibleActions.Contains(InteractionAction.PinNote)).IsFalse();

        settings.EnsureDefaults();

        Check.That(settings.VisibleActions).Contains(InteractionAction.PinNote);
    }

    [Fact]
    public void EnsureDefaults_DoesNotDuplicate_WhenAlreadyPresent()
    {
        var settings = new ActionMenuSettings();
        var count = settings.VisibleActions.Count;

        settings.EnsureDefaults();

        Check.That(settings.VisibleActions.Count).IsEqualTo(count);
    }
}
