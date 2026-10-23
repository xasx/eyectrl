namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using NFluent;
using Xunit;

/// <summary>
/// The CI work-area fix (#227/#231/#234): the App test assembly fixes the note manager's default
/// work area at the OWNER's 1920x1152 DIP in its module initializer, so placement-dependent tests
/// do not follow the machine's screen - CI's smaller one put a note on another side and turned two
/// tests red. A manager that injects its own provider keeps it, and production has no writer.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class NoteWorkAreaDefaultTests
{
    [Fact]
    public void TheDefaultRun_FixesTheNoteWorkAreaAtTheOwnerScreen()
    {
        Check.WithCustomMessage("the App test assembly sets the test default in its module initializer")
            .That(NoteManager.DefaultWorkAreaDipForTests).IsNotNull();
        Check.WithCustomMessage("the owner's screen, so local results stay identical")
            .That(NoteManager.DefaultWorkAreaDipForTests!()).IsEqualTo(new Rect(0, 0, 1920, 1152));
    }

    [Fact]
    public void AManagerWithoutAnInjectedWorkArea_UsesTheTestDefault()
    {
        using var manager = new NoteManager(new WeakReferenceMessenger());

        Check.WithCustomMessage("an absent workAreaDip means the test default, not the machine's screen")
            .That(manager.WorkAreaDipForTest).IsEqualTo(new Rect(0, 0, 1920, 1152));
    }

    [Fact]
    public void AManagersOwnWorkArea_StillWinsOverTheTestDefault()
    {
        using var manager = new NoteManager(new WeakReferenceMessenger(),
            workAreaDip: () => new Rect(0, 0, 1024, 768));

        Check.WithCustomMessage("an injected provider is never overridden")
            .That(manager.WorkAreaDipForTest).IsEqualTo(new Rect(0, 0, 1024, 768));
    }

    [Fact]
    public void Production_DoesNotWriteTheTestDefault()
    {
        var root = SourceScanner.RepoRoot().FullName;
        var writers = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains(nameof(NoteManager.DefaultWorkAreaDipForTests) + " ="))
            .Select(file => Path.GetRelativePath(root, file))
            .ToList();

        Check.WithCustomMessage($"no production file may write {nameof(NoteManager.DefaultWorkAreaDipForTests)}")
            .That(writers).IsEmpty();
    }
}
