namespace EyeCtrl.Core.Tests.Models;

using EyeCtrl.Core.Models;
using Xunit;

public class InteractionActionEnumTests
{
    [Fact]
    public void PinNote_HasValue_9()
    {
        // After Drag replaced DragStart and DragEnd was removed (−1), with ScrollMode present:
        // LeftClick0, RightClick1, DoubleClick2, Drag3, ScrollUp4, ScrollDown5, ScrollLeft6,
        // ScrollRight7, ScrollMode8, PinNote9.
        Check.That((int)InteractionAction.PinNote).IsEqualTo(9);
    }

    [Fact]
    public void Enum_Contains_PinNote()
    {
        Check.That(Enum.GetValues<InteractionAction>()).Contains(InteractionAction.PinNote);
    }
}
