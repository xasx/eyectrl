namespace EyeCtrl.Core.Tests.Keyboard;

using EyeCtrl.Core.Keyboard;

public class KeyNameMapperTests
{
    [Theory]
    [InlineData("Enter", 0x0D, false)]
    [InlineData("return", 0x0D, false)]
    [InlineData("F5", 0x74, false)]
    [InlineData("Escape", 0x1B, false)]
    [InlineData("esc", 0x1B, false)]
    [InlineData("Tab", 0x09, false)]
    [InlineData("Space", 0x20, false)]
    [InlineData("Backspace", 0x08, false)]
    [InlineData("back", 0x08, false)]
    public void KnownNonExtendedKeys_MapCorrectly(string name, ushort expectedVk, bool expectedExtended)
    {
        var ok = KeyNameMapper.TryMap(name, out var vk, out var isExtended);

        Check.That(ok).IsTrue();
        Check.That(vk).IsEqualTo(expectedVk);
        Check.That(isExtended).IsEqualTo(expectedExtended);
    }

    [Theory]
    [InlineData("Left", 0x25)]
    [InlineData("arrowleft", 0x25)]
    [InlineData("Delete", 0x2E)]
    [InlineData("del", 0x2E)]
    [InlineData("PageUp", 0x21)]
    [InlineData("pgup", 0x21)]
    [InlineData("pagedown", 0x22)]
    [InlineData("Insert", 0x2D)]
    [InlineData("Home", 0x24)]
    [InlineData("End", 0x23)]
    [InlineData("arrowright", 0x27)]
    [InlineData("arrowup", 0x26)]
    [InlineData("arrowdown", 0x28)]
    public void KnownExtendedKeys_MapCorrectlyWithExtendedFlag(string name, ushort expectedVk)
    {
        var ok = KeyNameMapper.TryMap(name, out var vk, out var isExtended);

        Check.That(ok).IsTrue();
        Check.That(vk).IsEqualTo(expectedVk);
        Check.That(isExtended).IsTrue();
    }

    [Fact]
    public void PrintScreen_MapsToSnapshotWithoutExtendedFlag()
    {
        var ok = KeyNameMapper.TryMap("PrintScreen", out var vk, out var isExtended);

        Check.That(ok).IsTrue();
        Check.That(vk).IsEqualTo((ushort)0x2C);
        Check.That(isExtended).IsFalse();
    }

    [Theory]
    [InlineData("f1", 0x70)]
    [InlineData("f2", 0x71)]
    [InlineData("f3", 0x72)]
    [InlineData("f4", 0x73)]
    [InlineData("f5", 0x74)]
    [InlineData("f6", 0x75)]
    [InlineData("f7", 0x76)]
    [InlineData("f8", 0x77)]
    [InlineData("f9", 0x78)]
    [InlineData("f10", 0x79)]
    [InlineData("f11", 0x7A)]
    [InlineData("f12", 0x7B)]
    public void FunctionKeys_MapCorrectly(string name, ushort expectedVk)
    {
        var ok = KeyNameMapper.TryMap(name, out var vk, out var isExtended);

        Check.That(ok).IsTrue();
        Check.That(vk).IsEqualTo(expectedVk);
        Check.That(isExtended).IsFalse();
    }

    [Theory]
    [InlineData("ENTER", 0x0D)]
    [InlineData("enter", 0x0D)]
    [InlineData("Enter", 0x0D)]
    [InlineData("eNtEr", 0x0D)]
    public void Mapping_IsCaseInsensitive(string name, ushort expectedVk)
    {
        var ok = KeyNameMapper.TryMap(name, out var vk, out _);

        Check.That(ok).IsTrue();
        Check.That(vk).IsEqualTo(expectedVk);
    }

    [Theory]
    [InlineData("")]
    [InlineData("xyzzy")]
    [InlineData("control")]
    [InlineData("ctrl")]
    public void UnknownKey_ReturnsFalseWithZeroVk(string name)
    {
        var ok = KeyNameMapper.TryMap(name, out var vk, out var isExtended);

        Check.That(ok).IsFalse();
        Check.That(vk).IsEqualTo((ushort)0);
        Check.That(isExtended).IsFalse();
    }

    [Fact]
    public void TryMap_OnSuccess_SetsBothOutParameters()
    {
        // Sanity check that both out parameters are populated on a successful map.
        var ok = KeyNameMapper.TryMap("Delete", out var vk, out var isExtended);

        Check.That(ok).IsTrue();
        Check.That(vk).IsEqualTo((ushort)0x2E);
        Check.That(isExtended).IsTrue();
    }
}
