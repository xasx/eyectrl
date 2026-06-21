namespace EyeCtrl.Core.Tests.Models;

using EyeCtrl.Core.Models;
using Xunit;

public class InteractionActionEnumTests
{
    [Fact]
    public void PinNote_HasValue_9()
    {
        Check.That((int)InteractionAction.PinNote).IsEqualTo(9);
    }

    [Fact]
    public void Enum_Contains_PinNote()
    {
        Check.That(Enum.GetValues<InteractionAction>()).Contains(InteractionAction.PinNote);
    }
}
