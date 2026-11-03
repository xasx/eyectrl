namespace EyeCtrl.Core.Tests.Input;

using EyeCtrl.Core.Input;

/// <summary>
/// C10 fixup-01 F6: R4's target selection, pure. The reviewer's two surviving mutants were both in
/// this decision — "ignore the integrity check" (R2) and "own-window filter disabled" (R2b) — so the
/// rules are pinned here instead of by substring checks: Z-order, own process skipped, superior
/// tokens skipped, shell only as the last resort.
/// </summary>
public class EscapeTargetRulesTests
{
    private static readonly SelfTokenFacts Self = new(0x2000, Elevated: false, HasUIAccess: false);
    private const uint OwnPid = 100;

    [Fact]
    public void TheTopmostEligibleApplicationWins()
    {
        var candidates = new[]
        {
            App(hwnd: 1, pid: 200, level: 0x2000),
            App(hwnd: 2, pid: 201, level: 0x2000),
        };

        Check.That(EscapeTargetRules.SelectTopmost(candidates, Self, OwnPid)).IsEqualTo(new IntPtr(1));
    }

    [Fact]
    public void EyeCtrlsOwnWindow_IsNeverTheTarget()
    {
        // R2b: the own-process filter disabled would pick hwnd 1 here.
        var candidates = new[]
        {
            App(hwnd: 1, pid: OwnPid, level: 0x2000),
            App(hwnd: 2, pid: 201, level: 0x2000),
        };

        Check.That(EscapeTargetRules.SelectTopmost(candidates, Self, OwnPid)).IsEqualTo(new IntPtr(2));
    }

    [Fact]
    public void AWindowAboveOurIntegrity_IsSkipped()
    {
        // R2: the IL check disabled would bring the elevated window forward — a second blocked click.
        var candidates = new[]
        {
            App(hwnd: 1, pid: 200, level: 0x3000, elevated: true),
            App(hwnd: 2, pid: 201, level: 0x2000),
        };

        Check.That(EscapeTargetRules.SelectTopmost(candidates, Self, OwnPid)).IsEqualTo(new IntPtr(2));
    }

    [Fact]
    public void AnAccessDeniedTarget_IsSkipped()
    {
        var candidates = new[]
        {
            new EscapeCandidate(new IntPtr(1), 200, IsShell: false,
                new ProcessTokenFacts(null, null, null, null, AccessDenied: true)),
            App(hwnd: 2, pid: 201, level: 0x2000),
        };

        Check.That(EscapeTargetRules.SelectTopmost(candidates, Self, OwnPid)).IsEqualTo(new IntPtr(2));
    }

    [Fact]
    public void TheShell_IsOnlyTheLastResort()
    {
        var appBelow = new[]
        {
            Shell(hwnd: 1),
            App(hwnd: 2, pid: 200, level: 0x2000),
        };
        Check.That(EscapeTargetRules.SelectTopmost(appBelow, Self, OwnPid)).IsEqualTo(new IntPtr(2));

        var onlyShell = new[] { Shell(hwnd: 1) };
        Check.That(EscapeTargetRules.SelectTopmost(onlyShell, Self, OwnPid)).IsEqualTo(new IntPtr(1));
    }

    [Fact]
    public void NoEligibleWindow_ReturnsNull()
    {
        var allElevated = new[]
        {
            App(hwnd: 1, pid: 200, level: 0x3000, elevated: true),
            App(hwnd: 2, pid: OwnPid, level: 0x2000),
        };

        Check.That(EscapeTargetRules.SelectTopmost(allElevated, Self, OwnPid)).IsNull();
        Check.That(EscapeTargetRules.SelectTopmost(Array.Empty<EscapeCandidate>(), Self, OwnPid)).IsNull();
    }

    [Fact]
    public void AZeroHandle_IsNeverChosen()
    {
        var candidates = new[]
        {
            new EscapeCandidate(IntPtr.Zero, 200, IsShell: false,
                new ProcessTokenFacts("empty.exe", 0x2000, false, false, false)),
            App(hwnd: 2, pid: 201, level: 0x2000),
        };

        Check.That(EscapeTargetRules.SelectTopmost(candidates, Self, OwnPid)).IsEqualTo(new IntPtr(2));
    }

    private static EscapeCandidate App(int hwnd, uint pid, int level, bool elevated = false) =>
        new(new IntPtr(hwnd), pid, IsShell: false,
            new ProcessTokenFacts($"app{hwnd}.exe", level, elevated, HasUIAccess: false, AccessDenied: false));

    private static EscapeCandidate Shell(int hwnd) =>
        new(new IntPtr(hwnd), 300, IsShell: true, ProcessTokenFacts.Unknown);
}
