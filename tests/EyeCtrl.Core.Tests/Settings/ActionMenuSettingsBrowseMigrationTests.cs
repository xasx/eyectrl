namespace EyeCtrl.Core.Tests.Settings;

using System.Collections.Generic;
using System.Linq;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using Xunit;

/// <summary>
/// The Browse action's one-shot settings migration — the <see cref="ActionMenuSettings.EnsureKeyboardActionOnce"/>
/// pattern cloned (action added by a later release, so a per-startup add would re-pollute a user
/// who removed it). Plus the no-default-arm pins for the new member across the icon and ring-label
/// maps: the maps throw or fall back on an unmapped action by design, so Browse must resolve.
/// </summary>
public class ActionMenuSettingsBrowseActionTests
{
    [Fact]
    public void Defaults_DoNotContainBrowse_TheOneShotOwnsTheFirstAdd()
    {
        // Unlike Keyboard, Browse ships only through the one-shot migration (which runs for every
        // file whose flag is absent — fresh installs included). Pinning the initializer here so a
        // silent default-list change cannot double-track the migration's job.
        var s = new ActionMenuSettings();

        Check.That(s.VisibleActions).Not.Contains(InteractionAction.Browse);
    }

    [Fact]
    public void EnsureBrowseActionOnce_AddsBrowse_WhenAbsent_AndSetsFlag()
    {
        var s = new ActionMenuSettings
        {
            VisibleActions = new List<InteractionAction> { InteractionAction.LeftClick },
        };

        s.EnsureBrowseActionOnce();

        Check.That(s.VisibleActions).Contains(InteractionAction.Browse);
        Check.That(s.BrowseActionMigrated).IsTrue();
    }

    [Fact]
    public void EnsureBrowseActionOnce_IsIdempotent_OnRepeat()
    {
        var s = new ActionMenuSettings();

        s.EnsureBrowseActionOnce();
        s.EnsureBrowseActionOnce();

        Check.That(s.VisibleActions.Count(a => a == InteractionAction.Browse)).IsEqualTo(1);
    }

    [Fact]
    public void EnsureBrowseActionOnce_IsNoOp_WhenFlagAlreadySet()
    {
        // The user-owned-list rule: once migrated, the list is never touched again — even
        // if the user then removed Browse, a later call must not re-add it.
        var s = new ActionMenuSettings
        {
            VisibleActions = new List<InteractionAction> { InteractionAction.LeftClick },
            BrowseActionMigrated = true,
        };

        s.EnsureBrowseActionOnce();

        Check.That(s.VisibleActions).Not.Contains(InteractionAction.Browse);
    }

    [Fact]
    public void EnsureBrowseActionOnce_DoesNotDuplicate_WhenAlreadyPresent()
    {
        var s = new ActionMenuSettings();

        s.EnsureBrowseActionOnce();

        Check.That(s.VisibleActions.Count(a => a == InteractionAction.Browse)).IsEqualTo(1);
    }
}

public class BrowseActionMapPins
{
    [Fact]
    public void Browse_ResolvesAnIcon_ThroughTheNoDefaultArm()
    {
        // ActionIconMap.For throws on an unmapped action (no default arm by design): this is the
        // parse-time pin that Browse got its case.
        Check.That(ActionIconMap.For(InteractionAction.Browse)).IsEqualTo(ActionIcon.Browse);
    }

    [Fact]
    public void Browse_ResolvesTheRingLabelKey_NotTheEnumNameFallback()
    {
        // RingLabelMap's default arm returns the enum name — the resolver then logs a missing
        // key and the ring renders it raw. The case must land before that fallback.
        Check.That(RingLabelMap.For(InteractionAction.Browse)).IsEqualTo("overlays.ring.browse");
        Check.That(StringTable.EmbeddedEnglish.T(RingLabelMap.For(InteractionAction.Browse)))
            .IsEqualTo("Browse");
    }
}
