namespace EyeCtrl.Core.Tests.Settings;

using System.Collections.Generic;
using System.Linq;
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

    [Fact]
    public void EnsureDefaultsOnce_Migrates_ThenSetsTheFlag()
    {
        var settings = new ActionMenuSettings();
        settings.VisibleActions.Remove(InteractionAction.PinNote);

        settings.EnsureDefaultsOnce();

        Check.That(settings.VisibleActions).Contains(InteractionAction.PinNote);
        Check.That(settings.DefaultsMigrated).IsTrue();
    }

    // The whole point of the flag: once migrated, the list belongs to the user. A default
    // action the user removed (via the Settings toggles) must stay removed across restarts —
    // the per-startup EnsureDefaults would silently put it back.
    [Fact]
    public void EnsureDefaultsOnce_AfterMigration_DoesNotReAddRemovedActions()
    {
        var settings = new ActionMenuSettings();
        settings.EnsureDefaultsOnce();

        settings.VisibleActions.Remove(InteractionAction.PinNote);
        settings.EnsureDefaultsOnce();

        Check.That(settings.VisibleActions.Contains(InteractionAction.PinNote)).IsFalse();
    }
}

public class ActionMenuSettingsKeyboardActionTests
{
    [Fact]
    public void Defaults_ContainKeyboard_OnTheSecondLayer()
    {
        var s = new ActionMenuSettings();

        Check.That(s.VisibleActions).Contains(InteractionAction.Keyboard);
        Check.That(s.PrimaryActions).Not.Contains(InteractionAction.Keyboard);
    }

    [Fact]
    public void EnsureKeyboardActionOnce_AddsKeyboard_WhenAbsent_AndSetsFlag()
    {
        var s = new ActionMenuSettings
        {
            VisibleActions = new List<InteractionAction> { InteractionAction.LeftClick },
        };

        s.EnsureKeyboardActionOnce();

        Check.That(s.VisibleActions).Contains(InteractionAction.Keyboard);
        Check.That(s.KeyboardActionMigrated).IsTrue();
    }

    [Fact]
    public void EnsureKeyboardActionOnce_IsNoOp_WhenFlagAlreadySet()
    {
        // The user-owned-list rule: once migrated, the list is never touched again — even
        // if the user then removed Keyboard, a later call must not re-add it.
        var s = new ActionMenuSettings
        {
            VisibleActions = new List<InteractionAction> { InteractionAction.LeftClick },
            KeyboardActionMigrated = true,
        };

        s.EnsureKeyboardActionOnce();

        Check.That(s.VisibleActions).Not.Contains(InteractionAction.Keyboard);
    }

    [Fact]
    public void EnsureKeyboardActionOnce_DoesNotDuplicate_WhenAlreadyPresent()
    {
        var s = new ActionMenuSettings();

        s.EnsureKeyboardActionOnce();

        Check.That(s.VisibleActions.Count(a => a == InteractionAction.Keyboard)).IsEqualTo(1);
    }
}
