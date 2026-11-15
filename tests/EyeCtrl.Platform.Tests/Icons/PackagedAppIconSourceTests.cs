namespace EyeCtrl.Platform.Tests.Icons;

using EyeCtrl.Platform.Icons;

/// <summary>
/// S3-B source 1, the Store/UWP fix: the icon must come from the WINDOW's AUMID, never from the
/// shared <c>ApplicationFrameHost</c> process. The pivotal case is two frame windows of one process:
/// they host two different apps, so they must resolve two different icons — a per-process lookup
/// (or a per-process cache) is the wrongness this ticket exists to remove.
/// </summary>
public class PackagedAppIconSourceTests
{
    private const string FrameAumidOne = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
    private const string FrameAumidTwo = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App";

    private static readonly IntPtr FrameOne = new(0x1001);
    private static readonly IntPtr FrameTwo = new(0x1002);
    private const uint FrameHostProcess = 4242;

    private static (PackagedAppIconSource Source, FakeIdentity Identity, FakeShellImages Shell) New()
    {
        var identity = new FakeIdentity();
        var shell = new FakeShellImages();
        return (new PackagedAppIconSource(identity, shell), identity, shell);
    }

    private static void FrameHost(FakeIdentity identity, IntPtr hwnd, string aumid)
    {
        identity.ClassNames[hwnd] = WindowIdentityFacts.FrameHostClassName;
        identity.ExecutablePaths[FrameHostProcess] = @"C:\Windows\System32\ApplicationFrameHost.exe";
        identity.AppUserModelIds[hwnd] = aumid;
    }

    [Fact]
    public void AFrameHostWindow_ResolvesItsOwnAppsIconFromItsAumid()
    {
        var (source, identity, shell) = New();
        FrameHost(identity, FrameOne, FrameAumidOne);

        var icon = source.TryGetIcon(new IconRequest(FrameOne, FrameHostProcess));

        Check.That(icon).IsNotNull();
        Check.That(shell.Requests).HasSize(1);
        Check.That(shell.Requests[0].ParsingName).IsEqualTo("shell:AppsFolder\\" + FrameAumidOne);
        // 40 DIP at the owner's 200 % is 80 physical px: the shell is asked for a real 96 px source.
        Check.That(shell.Requests[0].SizePx).IsEqualTo(96);
    }

    [Fact]
    public void TwoFrameHostWindowsOfOneProcess_GetTheirOwnAppsIcon()
    {
        var (source, identity, shell) = New();
        FrameHost(identity, FrameOne, FrameAumidOne);
        FrameHost(identity, FrameTwo, FrameAumidTwo);

        var first = source.TryGetIcon(new IconRequest(FrameOne, FrameHostProcess));
        var second = source.TryGetIcon(new IconRequest(FrameTwo, FrameHostProcess));

        Check.WithCustomMessage(
                "Two Store apps share ApplicationFrameHost; a per-PROCESS icon lookup gives both the "
                + "same (wrong) icon, which is the bug S3-B removes.")
            .That(ReferenceEquals(second, first)).IsFalse();
        Check.That(shell.Requests.Select(r => r.ParsingName)).ContainsExactly(
            "shell:AppsFolder\\" + FrameAumidOne, "shell:AppsFolder\\" + FrameAumidTwo);
    }

    [Fact]
    public void AnOrdinaryWindow_IsNeitherAskedForAnAumid_NorGivenOne()
    {
        var (source, identity, shell) = New();
        identity.ClassNames[FrameOne] = "Notepad";
        identity.ExecutablePaths[FrameHostProcess] = @"C:\Windows\System32\notepad.exe";

        var icon = source.TryGetIcon(new IconRequest(FrameOne, 999));

        Check.That(icon).IsNull();
        Check.WithCustomMessage("An ordinary Win32 window must not pay for a property-store read.")
            .That(identity.AumidReads).IsEqualTo(0);
        Check.That(shell.Requests).IsEmpty();
    }

    [Fact]
    public void TheSecondOpenOfTheSameApp_CostsNoShellCall()
    {
        var (source, identity, shell) = New();
        FrameHost(identity, FrameOne, FrameAumidOne);

        var first = source.TryGetIcon(new IconRequest(FrameOne, FrameHostProcess));
        // A NEW window of the same app (a second Calculator window) exists next open.
        identity.AppUserModelIds[FrameTwo] = FrameAumidOne;
        var second = source.TryGetIcon(new IconRequest(FrameTwo, FrameHostProcess));

        Check.That(second).IsSameReferenceAs(first);
        Check.That(shell.Requests).HasSize(1);
        Check.That(source.ExtractionCount).IsEqualTo(1);
    }

    [Fact]
    public void AFrameHostWindowWithoutAnAumid_DeclinesSoTheNextSourceCanTry()
    {
        var (source, identity, shell) = New();
        identity.ClassNames[FrameOne] = WindowIdentityFacts.FrameHostClassName;
        identity.ExecutablePaths[FrameHostProcess] = @"C:\Windows\System32\ApplicationFrameHost.exe";

        var icon = source.TryGetIcon(new IconRequest(FrameOne, FrameHostProcess));

        Check.That(icon).IsNull();
        Check.That(shell.Requests).IsEmpty();
    }

    [Fact]
    public void TheFrameHostIsRecognisedByItsExeName_WhenItsClassNameCannotBeRead()
    {
        // Elevated/other-desktop windows can hide their class name; the exe file name is read through
        // PROCESS_QUERY_LIMITED_INFORMATION and identifies the frame host just as well.
        var (source, identity, shell) = New();
        identity.ExecutablePaths[FrameHostProcess] = @"C:\Windows\System32\ApplicationFrameHost.exe";
        identity.AppUserModelIds[FrameOne] = FrameAumidOne;

        var icon = source.TryGetIcon(new IconRequest(FrameOne, FrameHostProcess));

        Check.That(icon).IsNotNull();
        Check.That(shell.Requests).HasSize(1);
    }

    [Fact]
    public void AnAumidThatTheShellCannotResolve_IsCached_SoTheNextOpenDoesNotRetry()
    {
        var (source, identity, shell) = New();
        FrameHost(identity, FrameOne, FrameAumidOne);
        shell.Unavailable.Add("shell:AppsFolder\\" + FrameAumidOne);

        Check.That(source.TryGetIcon(new IconRequest(FrameOne, FrameHostProcess))).IsNull();
        Check.That(source.TryGetIcon(new IconRequest(FrameOne, FrameHostProcess))).IsNull();

        Check.That(shell.Requests).HasSize(1);
    }
}
