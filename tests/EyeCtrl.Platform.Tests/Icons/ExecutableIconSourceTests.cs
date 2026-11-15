namespace EyeCtrl.Platform.Tests.Icons;

using EyeCtrl.Platform.Icons;

/// <summary>
/// S3-B source 2: the exe path through <c>QueryFullProcessImageName</c> + the shell's large image.
/// This is the source that works for elevated and other-user processes (Task Manager) and produces
/// the 64–96 px image the 40 DIP draw needs. The cache is per PATH for the app's lifetime, including
/// negative entries, so the second open never re-extracts.
/// </summary>
public class ExecutableIconSourceTests
{
    private static readonly IntPtr Window = new(0x2001);
    private const uint FirstPid = 501;
    private const uint SecondPid = 502;
    private const string Notepad = @"C:\Windows\System32\notepad.exe";
    private const string Taskmgr = @"C:\Windows\System32\Taskmgr.exe";

    private static (ExecutableIconSource Source, FakeIdentity Identity, FakeShellImages Shell) New()
    {
        var identity = new FakeIdentity();
        var shell = new FakeShellImages();
        return (new ExecutableIconSource(identity, shell), identity, shell);
    }

    [Fact]
    public void TheExePath_IsExtractedThroughTheShellAt96Px()
    {
        var (source, identity, shell) = New();
        identity.ExecutablePaths[FirstPid] = Notepad;

        var icon = source.TryGetIcon(new IconRequest(Window, FirstPid));

        Check.That(icon).IsNotNull();
        Check.That(shell.Requests).HasSize(1);
        Check.That(shell.Requests[0].ParsingName).IsEqualTo(Notepad);
        Check.That(shell.Requests[0].SizePx).IsEqualTo(96);
    }

    [Fact]
    public void TheSecondWindowOfOneExe_DoesNotReExtract()
    {
        var (source, identity, shell) = New();
        identity.ExecutablePaths[FirstPid] = Notepad;
        identity.ExecutablePaths[SecondPid] = Notepad;

        var first = source.TryGetIcon(new IconRequest(Window, FirstPid));
        var second = source.TryGetIcon(new IconRequest(new IntPtr(0x2002), SecondPid));

        Check.That(second).IsSameReferenceAs(first);
        Check.That(shell.Requests).HasSize(1);
        Check.That(source.ExtractionCount).IsEqualTo(1);
    }

    [Fact]
    public void DifferentExecutables_AreSeparateCacheEntries_AndSeparateIcons()
    {
        var (source, identity, shell) = New();
        identity.ExecutablePaths[FirstPid] = Notepad;
        identity.ExecutablePaths[SecondPid] = Taskmgr;

        var notepad = source.TryGetIcon(new IconRequest(Window, FirstPid));
        var taskManager = source.TryGetIcon(new IconRequest(new IntPtr(0x2002), SecondPid));

        Check.WithCustomMessage("two executables must not share one cached icon").That(ReferenceEquals(taskManager, notepad)).IsFalse();
        Check.That(shell.Requests.Select(r => r.ParsingName)).ContainsExactly(Notepad, Taskmgr);
        Check.That(source.ExtractionCount).IsEqualTo(2);
    }

    [Fact]
    public void AnElevatedProcessStillResolves_ThroughTheLimitedQueryPath()
    {
        // The identity probe is the limited-rights read (the fake stands in for it): the source never
        // touches Process.MainModule, so an elevated/exited module cannot throw here.
        var (source, identity, shell) = New();
        identity.ExecutablePaths[FirstPid] = Taskmgr;

        var icon = source.TryGetIcon(new IconRequest(Window, FirstPid));

        Check.That(icon).IsNotNull();
        Check.That(identity.PathReads).IsEqualTo(1);
        Check.That(shell.Requests).HasSize(1);
    }

    [Fact]
    public void AFrameHostWindow_IsNeverGivenTheFrameHostsOwnExeIcon()
    {
        // ApplicationFrameHost.exe is not the app. Handing its icon to every Store app is exactly the
        // "wrong app's icon" the ticket forbids, so this source declines and the packaged source
        // (or the window/default) owns the window.
        var (source, identity, shell) = New();
        identity.ClassNames[Window] = WindowIdentityFacts.FrameHostClassName;
        identity.ExecutablePaths[FirstPid] = @"C:\Windows\System32\ApplicationFrameHost.exe";

        var icon = source.TryGetIcon(new IconRequest(Window, FirstPid));

        Check.That(icon).IsNull();
        Check.That(shell.Requests).IsEmpty();
    }

    [Fact]
    public void AnUnreadablePath_DeclinesWithoutTouchingTheShell()
    {
        var (source, identity, shell) = New();

        var icon = source.TryGetIcon(new IconRequest(Window, FirstPid));

        Check.That(icon).IsNull();
        Check.That(shell.Requests).IsEmpty();
    }

    [Fact]
    public void AFailedExtractionIsCached_SoAWindowWithoutAnIconDoesNotRePayTheLookup()
    {
        var (source, identity, shell) = New();
        identity.ExecutablePaths[FirstPid] = Notepad;
        shell.Unavailable.Add(Notepad);

        Check.That(source.TryGetIcon(new IconRequest(Window, FirstPid))).IsNull();
        Check.That(source.TryGetIcon(new IconRequest(Window, FirstPid))).IsNull();

        Check.That(shell.Requests).HasSize(1);
    }
}
