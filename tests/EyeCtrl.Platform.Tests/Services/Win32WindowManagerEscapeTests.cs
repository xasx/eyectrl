namespace EyeCtrl.Platform.Tests.Services;

using System.IO;

/// <summary>
/// C10's R4 path, pinned at the source level: the escape method must bring a window forward
/// WITHOUT <c>SendInput</c>. That is the whole point — the ALT-tap grant the task switcher uses is
/// itself injected input and is dropped in exactly the state this path exists to escape, so a
/// regression that delegates to <c>BringToFront</c> would leave the owner stuck again.
/// </summary>
public class Win32WindowManagerEscapeTests
{
    [Fact]
    public void TryBringToFront_UsesSetForegroundWindowAndTheShowWindowFallback_ButNeverSendInput()
    {
        var body = ExtractMethod(Source(), "public EscapeWindowResult TryBringToFront(IntPtr hwnd)");

        Check.WithCustomMessage("the escape path no longer calls SetForegroundWindow.")
            .That(body.Contains("NativeMethods.SetForegroundWindow")).IsTrue();
        Check.WithCustomMessage("the escape path lost its non-injecting minimize/restore fallback.")
            .That(body.Contains("SW_MINIMIZE") && body.Contains("SW_RESTORE")).IsTrue();
        Check.WithCustomMessage(
                "the escape path must not inject ANY input: the injecting BringToFront path is "
                + "silently dropped by the very UIPI block this path exists to get out of.")
            .That(body.Contains("SendInput")).IsFalse();
        Check.WithCustomMessage(
                "the escape path delegates to the injecting task-switcher path (it must not: its ALT "
                + "grant is dropped under the block).")
            .That(body.Contains("BringToFront(")).IsFalse();
    }

    [Fact]
    public void TryGetEscapeTarget_NeverTargetsAWindowAboveEyeCtrlsOwnIntegrity()
    {
        var body = ExtractMethod(Source(), "public IntPtr? TryGetEscapeTarget()");

        Check.WithCustomMessage("the escape target is chosen without the R1 integrity comparison: an "
                + "elevated window would be a second blocked attempt.")
            .That(body.Contains("BlocksInjectedInput")).IsTrue();
        Check.WithCustomMessage("the desktop/shell is no longer the last-resort target.")
            .That(body.Contains("GetShellWindow")).IsTrue();
    }

    private static string Source() => File.ReadAllText(Path.Combine(
        RepoRoot(), "src", "EyeCtrl.Platform", "Services", "Win32WindowManager.cs"));

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Could not find the repository root (EyeCtrl.slnx).");
    }

    /// <summary>Returns one method's source text by brace matching from its signature.</summary>
    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Check.WithCustomMessage($"The repository source no longer contains '{signature}'.")
            .That(start).IsStrictlyGreaterThan(-1);

        var open = source.IndexOf('{', start);
        Check.That(open).IsStrictlyGreaterThan(-1);

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[open..(i + 1)];
        }

        throw new InvalidOperationException($"Unbalanced braces after '{signature}'.");
    }
}
