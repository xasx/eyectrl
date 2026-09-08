namespace EyeCtrl.Core.Tests.Models;

using EyeCtrl.Core.Models;
using Xunit;

public class InteractionActionEnumTests
{
    [Fact]
    public void PinNote_HasValue_10()
    {
        // After Drag replaced DragStart and DragEnd was removed (−1), with ScrollMode and
        // MiddleClick present: LeftClick0, RightClick1, DoubleClick2, MiddleClick3, Drag4,
        // ScrollUp5, ScrollDown6, ScrollLeft7, ScrollRight8, ScrollMode9, PinNote10.
        Check.That((int)InteractionAction.PinNote).IsEqualTo(10);
    }

    [Fact]
    public void Enum_Contains_PinNote()
    {
        Check.That(Enum.GetValues<InteractionAction>()).Contains(InteractionAction.PinNote);
    }

    [Fact]
    public void RepeatMembers_HavePinnedValues()
    {
        // Appended 2026-09-08 after PinNote: RepeatLastAction11, RepeatMode12. PinNote stays 10.
        Check.That((int)InteractionAction.RepeatLastAction).IsEqualTo(11);
        Check.That((int)InteractionAction.RepeatMode).IsEqualTo(12);
        Check.That((int)InteractionAction.PinNote).IsEqualTo(10);
    }

    [Theory]
    [InlineData(InteractionAction.LeftClick, true)]
    [InlineData(InteractionAction.RightClick, true)]
    [InlineData(InteractionAction.DoubleClick, true)]
    [InlineData(InteractionAction.MiddleClick, true)]
    [InlineData(InteractionAction.Drag, false)]
    [InlineData(InteractionAction.ScrollUp, false)]
    [InlineData(InteractionAction.ScrollDown, false)]
    [InlineData(InteractionAction.ScrollLeft, false)]
    [InlineData(InteractionAction.ScrollRight, false)]
    [InlineData(InteractionAction.ScrollMode, false)]
    [InlineData(InteractionAction.PinNote, false)]
    [InlineData(InteractionAction.RepeatLastAction, false)]
    [InlineData(InteractionAction.RepeatMode, false)]
    public void IsRepeatableClick_ClassifiesOnlyTheClickFamily(InteractionAction action, bool expected)
    {
        Check.That(action.IsRepeatableClick()).IsEqualTo(expected);
    }
}
