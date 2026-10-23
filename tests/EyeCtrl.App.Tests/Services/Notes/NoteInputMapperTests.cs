namespace EyeCtrl.App.Tests.Services.Notes;

using EyeCtrl.App.Services.Notes;
using EyeCtrl.Core.Keyboard;

/// <summary>
/// The P1 D2 table: every dispatcher input row, its NoteEdit or its swallow. System modifiers
/// (Ctrl, Alt, Win) are the only swallow cause on keys; Shift and CapsLock are text modifiers and
/// are ignored on control keys.
/// </summary>
public class NoteInputMapperTests
{
    private static readonly IReadOnlySet<ModifierKey> None = new HashSet<ModifierKey>();

    // ── plain keys ──

    [Theory]
    [InlineData("a")]
    [InlineData("Q")]
    [InlineData("!")]
    [InlineData("é")]
    [InlineData("😀")]
    public void PrintableKey_NoModifiers_Appends(string key) =>
        Check.That(NoteInputMapper.FromKey(key, None)).IsEqualTo(new NoteAppend(key));

    [Fact]
    public void SpaceKey_AppendsASpace() =>
        Check.That(NoteInputMapper.FromKey(" ", None)).IsEqualTo(new NoteAppend(" "));

    [Fact]
    public void Backspace_NoModifiers_IsABackspace() =>
        Check.That(NoteInputMapper.FromKey("\b", None)).IsEqualTo(new NoteBackspace());

    [Fact]
    public void Newline_NoModifiers_IsANewline() =>
        Check.That(NoteInputMapper.FromKey("\n", None)).IsEqualTo(new NoteNewline());

    [Fact]
    public void Tab_IsAlwaysSwallowed() =>
        Check.That(NoteInputMapper.FromKey("\t", None)).IsNull();

    // ── system modifiers swallow (deleteWord excepted) ──

    [Fact]
    public void Backspace_WithCtrlOnly_IsDeleteWord() =>
        Check.That(NoteInputMapper.FromKey("\b", Mods(ModifierKey.Ctrl))).IsEqualTo(new NoteDeleteWord());

    [Fact]
    public void Backspace_WithCtrl_PlusTextModifiers_StillDeletesWord()
    {
        // HandleDeleteWord sends armed ∪ {Ctrl}: CapsLock and Shift ride along harmlessly.
        var mods = Mods(ModifierKey.CapsLock, ModifierKey.Ctrl);
        Check.That(NoteInputMapper.FromKey("\b", mods)).IsEqualTo(new NoteDeleteWord());
    }

    [Theory]
    [InlineData(ModifierKey.Alt)]
    [InlineData(ModifierKey.Win)]
    public void Backspace_WithAnyOtherSystemModifier_IsSwallowed(ModifierKey system) =>
        Check.That(NoteInputMapper.FromKey("\b", Mods(system))).IsNull();

    [Fact]
    public void Backspace_WithCtrlAndAlt_IsSwallowed_NotDeleteWord() =>
        Check.That(NoteInputMapper.FromKey("\b", Mods(ModifierKey.Ctrl, ModifierKey.Alt))).IsNull();

    [Theory]
    [InlineData("a")]
    [InlineData(" ")]
    [InlineData("\n")]
    [InlineData("\t")]
    public void AnyKey_WithASystemModifier_IsSwallowed(string key) =>
        Check.That(NoteInputMapper.FromKey(key, Mods(ModifierKey.Ctrl))).IsNull();

    // ── text modifiers never swallow (P1 D2: Caps on, for each control key) ──

    [Fact]
    public void CapsLockArmed_PrintableKey_Appends() =>
        Check.That(NoteInputMapper.FromKey("a", Mods(ModifierKey.CapsLock))).IsEqualTo(new NoteAppend("a"));

    [Fact]
    public void CapsLockArmed_Backspace_IsABackspace() =>
        Check.That(NoteInputMapper.FromKey("\b", Mods(ModifierKey.CapsLock))).IsEqualTo(new NoteBackspace());

    [Fact]
    public void CapsLockArmed_Enter_IsANewline() =>
        Check.That(NoteInputMapper.FromKey("\n", Mods(ModifierKey.CapsLock))).IsEqualTo(new NoteNewline());

    [Fact]
    public void CapsLockArmed_Space_AppendsASpace() =>
        Check.That(NoteInputMapper.FromKey(" ", Mods(ModifierKey.CapsLock))).IsEqualTo(new NoteAppend(" "));

    [Fact]
    public void ShiftArmed_Space_BackspaceAndEnter_StayTextEdits()
    {
        Check.That(NoteInputMapper.FromKey(" ", Mods(ModifierKey.Shift))).IsEqualTo(new NoteAppend(" "));
        Check.That(NoteInputMapper.FromKey("\b", Mods(ModifierKey.Shift))).IsEqualTo(new NoteBackspace());
        Check.That(NoteInputMapper.FromKey("\n", Mods(ModifierKey.Shift))).IsEqualTo(new NoteNewline());
    }

    [Fact]
    public void ShiftArmed_PrintableKey_AppendsTheShiftedOutput() =>
        Check.That(NoteInputMapper.FromKey("A", Mods(ModifierKey.Shift))).IsEqualTo(new NoteAppend("A"));

    // ── predictions ──

    [Fact]
    public void Prediction_DeletesTheComposingInput_AndAppendsWordPlusSpace() =>
        Check.That(NoteInputMapper.FromPrediction(4, "hello")).IsEqualTo(new NotePrediction(4, "hello "));

    [Fact]
    public void Prediction_EmptyComposingInput_DeletesNothing() =>
        Check.That(NoteInputMapper.FromPrediction(0, "word")).IsEqualTo(new NotePrediction(0, "word "));

    [Fact]
    public void Prediction_NegativeCount_IsClampedToZero() =>
        Check.That(NoteInputMapper.FromPrediction(-2, "word")).IsEqualTo(new NotePrediction(0, "word "));

    // ── special keys ──

    [Theory]
    [InlineData("Backspace")]
    [InlineData("backspace")]
    [InlineData("back")]
    public void SpecialBackspace_IsABackspace(string keyName) =>
        Check.That(NoteInputMapper.FromSpecialKey(keyName, None)).IsEqualTo(new NoteBackspace());

    [Theory]
    [InlineData("Enter")]
    [InlineData("enter")]
    [InlineData("return")]
    public void SpecialEnter_IsANewline(string keyName) =>
        Check.That(NoteInputMapper.FromSpecialKey(keyName, None)).IsEqualTo(new NoteNewline());

    [Fact]
    public void SpecialSpace_AppendsASpace() =>
        Check.That(NoteInputMapper.FromSpecialKey("space", None)).IsEqualTo(new NoteAppend(" "));

    [Theory]
    [InlineData("Escape")]
    [InlineData("Delete")]
    [InlineData("Tab")]
    [InlineData("F5")]
    [InlineData("Left")]
    [InlineData("Home")]
    public void AnyOtherSpecialKey_IsSwallowed(string keyName) =>
        Check.That(NoteInputMapper.FromSpecialKey(keyName, None)).IsNull();

    [Theory]
    [InlineData("Backspace")]
    [InlineData("Enter")]
    [InlineData("Space")]
    public void SpecialKey_WithASystemModifier_IsSwallowed(string keyName) =>
        Check.That(NoteInputMapper.FromSpecialKey(keyName, Mods(ModifierKey.Win))).IsNull();

    [Fact]
    public void SpecialKey_WithTextModifiersOnly_StillApplies() =>
        Check.That(NoteInputMapper.FromSpecialKey("backspace", Mods(ModifierKey.CapsLock, ModifierKey.Shift)))
            .IsEqualTo(new NoteBackspace());

    // ── canned text ──

    [Fact]
    public void CannedText_AppendsVerbatim_IncludingNewlines() =>
        Check.That(NoteInputMapper.FromCannedText("line1\nline2")).IsEqualTo(new NoteAppend("line1\nline2"));

    private static IReadOnlySet<ModifierKey> Mods(params ModifierKey[] keys) => new HashSet<ModifierKey>(keys);
}
