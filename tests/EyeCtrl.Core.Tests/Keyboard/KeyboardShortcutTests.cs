namespace EyeCtrl.Core.Tests.Keyboard;

using EyeCtrl.Core.Keyboard;

public class KeyboardShortcutTests
{
    [Fact]
    public void TryParse_SplitsModifiersFromKey()
    {
        Check.That(KeyboardShortcut.TryParse("Ctrl+Shift+S", out var modifiers, out var key)).IsTrue();

        Check.That(key).IsEqualTo("S");
        Check.That(modifiers).HasSize(2);
        Check.That(modifiers).Contains(ModifierKey.Ctrl);
        Check.That(modifiers).Contains(ModifierKey.Shift);
    }

    [Fact]
    public void TryParse_TrimsWhitespaceAroundTokens()
    {
        Check.That(KeyboardShortcut.TryParse("Ctrl + C", out var modifiers, out var key)).IsTrue();

        Check.That(key).IsEqualTo("C");
        Check.That(modifiers).Contains(ModifierKey.Ctrl);
    }

    [Fact]
    public void TryParse_DropsUnknownModifierTokens()
    {
        Check.That(KeyboardShortcut.TryParse("Ctrl+Meta+C", out var modifiers, out var key)).IsTrue();

        Check.That(key).IsEqualTo("C");
        Check.That(modifiers).HasSize(1);
        Check.That(modifiers).Contains(ModifierKey.Ctrl);
    }

    [Fact]
    public void TryParse_BareKey_HasNoModifiers()
    {
        Check.That(KeyboardShortcut.TryParse("F5", out var modifiers, out var key)).IsTrue();

        Check.That(key).IsEqualTo("F5");
        Check.That(modifiers).IsEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    public void TryParse_EmptyKeyPart_Fails(string chord)
    {
        // "Ctrl+" still parses its modifiers before failing — harmless, since the caller bails
        // on the false return and never reads them.
        Check.That(KeyboardShortcut.TryParse(chord, out _, out var key)).IsFalse();

        Check.That(key).IsEqualTo("");
    }
}
