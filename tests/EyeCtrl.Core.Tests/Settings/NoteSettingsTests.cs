namespace EyeCtrl.Core.Tests.Settings;

using System.Reflection;
using EyeCtrl.Core.Settings;
using Xunit;

/// <summary>
/// NoteSettings (P4 D1): the throwing guard, the non-throwing aggregate, and the registration
/// in the settings tree. The guard throws outside 18-48 - the UI never offers an illegal value,
/// so out-of-range means a bug or a hand-edited write. Validate is the defense-in-depth twin:
/// it can only ever SEE an out-of-range value through the backing field (the generated setter
/// throws first), which is exactly how these tests plant one.
/// </summary>
public class NoteSettingsTests
{
    [Theory]
    [InlineData(17)]
    [InlineData(49)]
    [InlineData(0)]
    public void Guard_ThrowsOutOfRange(int value)
    {
        var s = new NoteSettings();

        ArgumentException? ex = null;
        try { s.TextSizeDip = value; }
        catch (ArgumentException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("value");
    }

    [Theory]
    [InlineData(18)]
    [InlineData(28)]
    [InlineData(48)]
    public void Guard_AcceptsTheInclusiveRange(int value)
    {
        var s = new NoteSettings();
        s.TextSizeDip = value;
        Check.That(s.TextSizeDip).IsEqualTo(value);
    }

    [Fact]
    public void Default_IsTheProvisional28()
    {
        Check.That(new NoteSettings().TextSizeDip).IsEqualTo(28);
    }

    [Theory]
    [InlineData(17)]
    [InlineData(49)]
    public void Validate_FlagsOutOfRange_Backdoor(int value)
    {
        var s = new NoteSettings();
        // The generated setters guard throws on assignment; plant the value in the backing
        // field the way a future deserializer regression would.
        typeof(NoteSettings).GetField("_textSizeDip", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(s, value);

        Check.That(s.Validate()).Not.IsEmpty();
        Check.That(s.Validate()[0]).Contains(nameof(NoteSettings.TextSizeDip));
    }

    [Fact]
    public void Validate_InRange_IsEmpty()
    {
        Check.That(new NoteSettings().Validate()).IsEmpty();
    }

    [Fact]
    public void RoundTrip_KeepsTheSize()
    {
        var s = new NoteSettings { TextSizeDip = 40 };
        Check.That(s.TextSizeDip).IsEqualTo(40);
        Check.That(s.Validate()).IsEmpty();
    }

    [Fact]
    public void IsRegistered_InTheSettingsTree()
    {
        var root = new EyeCtrlSettings();
        Check.That(root.Note).IsNotNull();
        var names = root.EnumerateSubSettings().Select(t => t.Name).ToList();
        Check.That(names).Contains("Note");
        Check.That(root.Validate()).IsEmpty();
    }
}
