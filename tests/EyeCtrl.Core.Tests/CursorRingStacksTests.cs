namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public class CursorRingStacksTests
{
    [Fact]
    public void DefaultRingStyle_IsWeightedSandwich()
    {
        Check.That(new CursorSettings().RingStyle).IsEqualTo(CursorRingStyle.WeightedSandwich);
    }
}
