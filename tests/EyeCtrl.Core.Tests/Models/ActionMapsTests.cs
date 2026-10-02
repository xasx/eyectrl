namespace EyeCtrl.Core.Tests.Models;

using System.Collections.Generic;
using System.Linq;
using EyeCtrl.Core.Models;
using NFluent;
using Xunit;

/// <summary>
/// The icon and ring-name maps that moved from the runtime VM into Core (2026-09-30) so the
/// settings preview reads the same vocabulary the ring draws.
///
/// The icon property is the one that mattered before the move and matters more after it: every
/// <see cref="InteractionAction"/> must map to a DISTINCT icon, and none may fall through to a
/// default. <see cref="ActionIconMap.For"/> throws on an unmapped action instead of rendering
/// the enum name — a parse-time-safe failure that the runtime's crash-rather-than-limp policy
/// turns into a logged crash rather than an empty ring.
/// </summary>
public class ActionMapsTests
{
    [Fact]
    public void EveryAction_MapsToADistinctIcon()
    {
        var expected = new Dictionary<InteractionAction, ActionIcon>
        {
            [InteractionAction.LeftClick] = ActionIcon.LeftClick,
            [InteractionAction.RightClick] = ActionIcon.RightClick,
            [InteractionAction.DoubleClick] = ActionIcon.DoubleClick,
            [InteractionAction.MiddleClick] = ActionIcon.MiddleClick,
            [InteractionAction.Drag] = ActionIcon.Drag,
            [InteractionAction.ScrollUp] = ActionIcon.ScrollUp,
            [InteractionAction.ScrollDown] = ActionIcon.ScrollDown,
            [InteractionAction.ScrollLeft] = ActionIcon.ScrollLeft,
            [InteractionAction.ScrollRight] = ActionIcon.ScrollRight,
            [InteractionAction.ScrollMode] = ActionIcon.ScrollMode,
            [InteractionAction.PinNote] = ActionIcon.PinNote,
            [InteractionAction.Keyboard] = ActionIcon.Keyboard,
        };

        // Fails if a new InteractionAction is added without an icon.
        var allActions = Enum.GetValues<InteractionAction>();
        Check.That(expected.Keys).IsEquivalentTo(allActions);

        // Two actions sharing an icon would render two identical targets.
        Check.That(expected.Values.Distinct().Count()).IsEqualTo(expected.Count);

        foreach (var action in allActions)
            Check.WithCustomMessage($"{action} icon").That(ActionIconMap.For(action)).IsEqualTo(expected[action]);
    }

    [Fact]
    public void ActionIconMap_ThrowsOnAnUnmappedAction()
    {
        ArgumentOutOfRangeException? ex = null;
        try
        {
            ActionIconMap.For((InteractionAction)999);
        }
        catch (ArgumentOutOfRangeException e)
        {
            ex = e;
        }

        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("action");
    }

    [Fact]
    public void RingLabels_UseTheRingVocabulary_NotTheSettingsRowNames()
    {
        var expected = new Dictionary<InteractionAction, string>
        {
            [InteractionAction.LeftClick] = "Left click",
            [InteractionAction.RightClick] = "Right click",
            [InteractionAction.DoubleClick] = "Double click",
            [InteractionAction.MiddleClick] = "Middle click",
            [InteractionAction.Drag] = "Drag",
            [InteractionAction.ScrollUp] = "Scroll up",
            [InteractionAction.ScrollDown] = "Scroll down",
            [InteractionAction.ScrollLeft] = "Scroll left",
            [InteractionAction.ScrollRight] = "Scroll right",
            [InteractionAction.ScrollMode] = "Scroll",   // the settings row says "Scroll Mode"
            [InteractionAction.PinNote] = "Pin note",
            [InteractionAction.Keyboard] = "Keyboard",
        };

        var allActions = Enum.GetValues<InteractionAction>();
        Check.That(expected.Keys).IsEquivalentTo(allActions);

        foreach (var action in allActions)
            Check.WithCustomMessage($"{action} label").That(RingLabelMap.For(action)).IsEqualTo(expected[action]);
    }

    [Fact]
    public void RingLabels_NullIsBack()
    {
        // Parity with the map this replaced: the VM asked for a null action to name the Back.
        Check.That(RingLabelMap.For(null)).IsEqualTo("Back");
    }

    [Fact]
    public void RingLabels_UnknownAction_FallsBackToTheEnumName()
    {
        Check.That(RingLabelMap.For((InteractionAction)999)).IsEqualTo("999");
    }
}
