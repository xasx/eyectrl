namespace EyeCtrl.Platform.Tests.Services;

using EyeCtrl.Platform.Services;

public class StartupCommandValueTests
{
    [Fact]
    public void BuildCommandValue_WrapsPathInQuotes()
    {
        var result = StartupCommandValue.BuildCommandValue(@"C:\Apps\EyeCtrl.exe");
        Check.That(result).IsEqualTo(@"""C:\Apps\EyeCtrl.exe""");
    }

    [Fact]
    public void BuildCommandValue_HandlesPathWithSpaces()
    {
        var result = StartupCommandValue.BuildCommandValue(@"C:\Program Files\Eye Ctrl\EyeCtrl.exe");
        Check.That(result).IsEqualTo(@"""C:\Program Files\Eye Ctrl\EyeCtrl.exe""");
    }

    [Fact]
    public void BuildCommandValue_AlreadyQuoted_DoesNotDoubleQuote()
    {
        var result = StartupCommandValue.BuildCommandValue(@"""C:\Apps\EyeCtrl.exe""");
        Check.That(result).IsEqualTo(@"""C:\Apps\EyeCtrl.exe""");
    }

    [Fact]
    public void BuildCommandValue_EmptyOrWhitespace_ReturnsEmpty()
    {
        Check.That(StartupCommandValue.BuildCommandValue("")).IsEmpty();
        Check.That(StartupCommandValue.BuildCommandValue("   ")).IsEmpty();
    }
}
