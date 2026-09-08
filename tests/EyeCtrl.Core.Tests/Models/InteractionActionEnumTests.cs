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
}
