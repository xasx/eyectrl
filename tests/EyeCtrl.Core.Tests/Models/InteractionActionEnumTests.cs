namespace EyeCtrl.Core.Tests.Models;

using EyeCtrl.Core.Models;
using Xunit;

public class InteractionActionEnumTests
{
    [Fact]
    public void PinNote_HasValue_9()
    {
        Assert.Equal(9, (int)InteractionAction.PinNote);
    }

    [Fact]
    public void Enum_Contains_PinNote()
    {
        Assert.Contains(InteractionAction.PinNote, Enum.GetValues<InteractionAction>());
    }
}
