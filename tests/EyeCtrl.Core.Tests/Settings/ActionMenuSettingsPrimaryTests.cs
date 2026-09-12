namespace EyeCtrl.Core.Tests.Settings;

using System.Collections.Generic;
using System.Linq;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// The primary/secondary split, and the migration that has to leave an existing settings file
/// working.
///
/// The migration is the part with teeth. <c>VisibleActions</c> is user-owned — the Settings
/// Action Menu tab edits it, and <c>EnsureDefaultsOnce</c> exists precisely because a
/// per-startup default re-add made that tab lie across restarts. The primary list inherits that
/// hazard exactly: a migration that ran every startup would re-promote an action the user
/// demoted, every time they relaunched.
/// </summary>
public class ActionMenuSettingsPrimaryTests
{
    [Fact]
    public void AFreshSettings_HasFourPrimaryActions()
    {
        var settings = new ActionMenuSettings();

        Check.That(settings.PrimaryActions).ContainsExactly(
            InteractionAction.LeftClick,
            InteractionAction.RightClick,
            InteractionAction.DoubleClick,
            InteractionAction.ScrollMode);
    }

    [Fact]
    public void EveryPrimaryAction_IsAlsoVisible()
    {
        var settings = new ActionMenuSettings();

        Check.That(settings.VisibleActions).Contains(settings.PrimaryActions);
    }

    [Fact]
    public void SecondaryActions_AreTheVisibleOnesThatAreNotPrimary()
    {
        var settings = new ActionMenuSettings();

        Check.That(settings.SecondaryActions).ContainsExactly(
            InteractionAction.Drag,
            InteractionAction.ScrollUp,
            InteractionAction.ScrollDown,
            InteractionAction.PinNote,
            InteractionAction.Keyboard);
    }

    [Fact]
    public void SecondaryActions_NeverIncludeAHiddenAction()
    {
        var settings = new ActionMenuSettings
        {
            VisibleActions = new List<InteractionAction> { InteractionAction.LeftClick },
            PrimaryActions = new List<InteractionAction> { InteractionAction.LeftClick },
        };

        // ScrollLeft/ScrollRight ship OFF. Turning an action off in the Settings tab must remove
        // it from the ring entirely, not push it onto the second layer.
        Check.That(settings.SecondaryActions).IsEmpty();
    }

    [Fact]
    public void APrimaryActionThatIsNotVisible_IsDroppedFromTheRing()
    {
        var settings = new ActionMenuSettings
        {
            VisibleActions = new List<InteractionAction> { InteractionAction.LeftClick },
            PrimaryActions = new List<InteractionAction>
            {
                InteractionAction.LeftClick,
                InteractionAction.PinNote,   // primary, but switched off
            },
        };

        Check.That(settings.EffectivePrimaryActions).ContainsExactly(InteractionAction.LeftClick);
    }

    [Fact]
    public void MigratePrimaryOnce_PopulatesAnOldFile()
    {
        // An existing settings file has VisibleActions but no PrimaryActions.
        var settings = new ActionMenuSettings
        {
            PrimaryActions = new List<InteractionAction>(),
            PrimaryMigrated = false,
        };

        settings.MigratePrimaryOnce();

        Check.That(settings.PrimaryActions).ContainsExactly(
            InteractionAction.LeftClick,
            InteractionAction.RightClick,
            InteractionAction.DoubleClick,
            InteractionAction.ScrollMode);
        Check.That(settings.PrimaryMigrated).IsTrue();
    }

    [Fact]
    public void MigratePrimaryOnce_LeavesADeliberatelyEmptiedListAlone()
    {
        // This is the whole reason the flag exists: a user who demoted every action to the second
        // layer must not have four re-promoted on the next launch.
        var settings = new ActionMenuSettings
        {
            PrimaryActions = new List<InteractionAction>(),
            PrimaryMigrated = true,
        };

        settings.MigratePrimaryOnce();

        Check.That(settings.PrimaryActions).IsEmpty();
    }

    [Fact]
    public void Validate_RejectsMoreThanFourPrimaryActions()
    {
        var settings = new ActionMenuSettings
        {
            PrimaryActions = new List<InteractionAction>
            {
                InteractionAction.LeftClick, InteractionAction.RightClick,
                InteractionAction.DoubleClick, InteractionAction.ScrollMode,
                InteractionAction.Drag,
            },
        };

        // Four plus More fills the five slots. A fifth primary makes six, which drops the
        // neighbour gap from 160 DIP to 115 at the default CircleSize — still clear of each
        // other, but past the point where the ring can be aimed at rather than read.
        Check.That(settings.Validate()).Not.IsEmpty();
    }
}
