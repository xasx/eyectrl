namespace EyeCtrl.Core.Tests.Keyboard;

using EyeCtrl.Core.Keyboard;

/// <summary>E1's key classes: a closed set, and never a character.</summary>
public class KeyboardE1ClassifierTests
{
    private static KeyDefinition Plain(string output) => new() { Label = output, Output = output };
    private static KeyDefinition Special(string action) => new() { Label = "?", SpecialAction = action };

    [Theory]
    [InlineData("a", KeyE1.Letter)]
    [InlineData("ß", KeyE1.Letter)]
    [InlineData("ä", KeyE1.Letter)]
    [InlineData("7", KeyE1.Digit)]
    [InlineData(".", KeyE1.Other)]
    [InlineData(",", KeyE1.Other)]
    [InlineData("€", KeyE1.Other)]
    public void PlainKeys_AreClassedByTheirOutput_AndCarryNoId(string output, string expectedClass)
    {
        var (cls, action) = KeyboardE1Classifier.Classify(Plain(output), emojiMode: false);

        Check.That(cls).IsEqualTo(expectedClass);
        Check.That(action).IsEqualTo(KeyboardE1Classifier.None);
    }

    [Theory]
    [InlineData("space", KeyE1.Space)]
    [InlineData("backspace", KeyE1.Backspace)]
    [InlineData("deleteWord", KeyE1.DeleteWord)]
    [InlineData("enter", KeyE1.Enter)]
    [InlineData("shift", KeyE1.Shift)]
    [InlineData("modifier:shift", KeyE1.Shift)]
    [InlineData("toggle:capslock", KeyE1.Caps)]
    [InlineData("modifier:ctrl", KeyE1.Mode)]
    [InlineData("modifier:alt", KeyE1.Mode)]
    [InlineData("modifier:win", KeyE1.Mode)]
    [InlineData("switchEmoji", KeyE1.Mode)]
    [InlineData("switchSub:numbers", KeyE1.SwitchSub)]
    [InlineData("tab", KeyE1.Other)]
    [InlineData("addWord", KeyE1.Other)]
    [InlineData("shortcut:Ctrl+C", KeyE1.Other)]
    [InlineData("key:F5", KeyE1.Other)]
    public void SpecialKeys_AreClassedByTheirAction_AndCarryTheLayoutsOwnId(string action, string expectedClass)
    {
        var (cls, id) = KeyboardE1Classifier.Classify(Special(action), emojiMode: false);

        Check.That(cls).IsEqualTo(expectedClass);
        Check.That(id).IsEqualTo(action);
    }

    [Fact]
    public void EveryKeyOnTheEmojiSurface_IsEmoji_AndCarriesNoId()
    {
        // An emoji key's output (and its dwell-target id) can BE the glyph: neither may surface.
        var (cls, id) = KeyboardE1Classifier.Classify(Plain("😀"), emojiMode: true);

        Check.That(cls).IsEqualTo(KeyE1.Emoji);
        Check.That(id).IsEqualTo(KeyboardE1Classifier.None);
    }

    [Fact]
    public void AKeyNotInTheLayout_IsOther()
    {
        var (cls, id) = KeyboardE1Classifier.Classify(null, emojiMode: false);

        Check.That(cls).IsEqualTo(KeyE1.Other);
        Check.That(id).IsEqualTo(KeyboardE1Classifier.None);
    }

    [Fact]
    public void AMultiCharacterOutput_IsOther_NotALetter()
    {
        var (cls, _) = KeyboardE1Classifier.Classify(Plain("qu"), emojiMode: false);

        Check.That(cls).IsEqualTo(KeyE1.Other);
    }

    [Theory]
    [InlineData(ModifierKey.Shift, "shift")]
    [InlineData(ModifierKey.CapsLock, "caps")]
    [InlineData(ModifierKey.Ctrl, "ctrl")]
    [InlineData(ModifierKey.Alt, "alt")]
    [InlineData(ModifierKey.Win, "win")]
    public void EveryModifier_HasItsOwnClosedModeKind(ModifierKey modifier, string expected)
    {
        Check.That(KeyboardE1Classifier.ModifierKind(modifier)).IsEqualTo(expected);
    }

    [Fact]
    public void NoModifier_FallsIntoTheOtherBucket()
    {
        foreach (var modifier in Enum.GetValues<ModifierKey>())
            Check.WithCustomMessage($"{modifier} has no E1 mode kind of its own")
                .That(KeyboardE1Classifier.ModifierKind(modifier)).IsNotEqualTo("other");
    }
}
