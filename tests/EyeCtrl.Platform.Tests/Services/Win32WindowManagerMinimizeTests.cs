namespace EyeCtrl.Platform.Tests.Services;

using System.IO;

/// <summary>
/// S3-A fixup-01 F1 and S3-C fixup-01 (coordinator decision 1), pinned at the source level: both
/// the set-minimise path and the single-target Quick Actions minimise/maximise must post
/// <c>ShowWindowAsync</c>. They run on the UI thread, which is also the gaze-dwell thread, so a
/// synchronous <c>ShowWindow</c> on one hung window would freeze every overlay (Pause included) and
/// leave a gaze-only owner without control.
///
/// <para>
/// The production P/Invoke layer is static; the runtime behaviour is covered by
/// <see cref="Win32WindowManagerActionTests"/> through the <c>IWindowActionNative</c> fake, and the
/// static choice itself is pinned here: the production shim must forward the async call to
/// <c>NativeMethods.ShowWindowAsync</c>, never to the blocking twin. A runtime test that actually
/// minimised a window is forbidden here (no test may touch a real window).
/// </para>
/// </summary>
public class Win32WindowManagerMinimizeTests
{
    [Fact]
    public void MinimizePaths_UseShowWindowAsync_NeverTheBlockingCall()
    {
        var source = Source();

        var nonBlocking = ExtractMethod(source, "public void MinimizeNonBlocking(IntPtr hwnd)");
        Check.WithCustomMessage(
                "Show desktop's path calls the blocking ShowWindow again: one hung window would "
                + "freeze the UI/gaze thread and take the owner's control away.")
            .That(nonBlocking.Contains("NativeMethods.ShowWindowAsync")).IsTrue();
        Check.WithCustomMessage("the non-blocking path still calls the blocking ShowWindow.")
            .That(nonBlocking.Contains("NativeMethods.ShowWindow(")).IsFalse();
        Check.That(nonBlocking.Contains("SW_MINIMIZE")).IsTrue();
        Check.WithCustomMessage("the stale-handle re-check must survive the switch to the async call.")
            .That(nonBlocking.Contains("IsWindow")).IsTrue();

        // Fixup-01 (F2/F4): the single-target Quick Actions minimise posts asynchronously too.
        var minimize = ExtractMethod(source, "public void Minimize(IntPtr hwnd)");
        Check.WithCustomMessage(
                "Quick Actions Minimize calls the blocking ShowWindow again: the caller is the "
                + "gaze thread, so one hung window freezes the owner's control.")
            .That(minimize.Contains("_native.ShowWindowAsync")).IsTrue();
        Check.WithCustomMessage("the single-target minimise still calls the blocking ShowWindow.")
            .That(minimize.Contains("_native.ShowWindow(")).IsFalse();
        Check.That(minimize.Contains("SW_MINIMIZE")).IsTrue();
        Check.That(minimize.Contains("IsWindow")).IsTrue();

        var maximize = ExtractMethod(source, "public void MaximizeOrRestore(IntPtr hwnd)");
        Check.WithCustomMessage(
                "MaximizeOrRestore calls the blocking ShowWindow again: the caller is the gaze "
                + "thread, so one hung window freezes the owner's control.")
            .That(maximize.Contains("_native.ShowWindowAsync")).IsTrue();
        Check.WithCustomMessage("the maximise/restore path still calls the blocking ShowWindow.")
            .That(maximize.Contains("_native.ShowWindow(")).IsFalse();
        Check.That(maximize.Contains("DecideWindowOp")).IsTrue();
    }

    /// <summary>
    /// The seam is only honest if its production implementation reaches the real P/Invoke: the
    /// async forwarding must call <c>NativeMethods.ShowWindowAsync</c>, never <c>ShowWindow</c>.
    /// </summary>
    [Fact]
    public void Win32WindowActionNative_ForwardsTheAsyncShowToThePInvoke()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "EyeCtrl.Platform", "Services", "Win32WindowActionNative.cs"));
        const string signature = "public bool ShowWindowAsync(IntPtr hwnd, int command)";
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Check.WithCustomMessage($"The repository source no longer contains '{signature}'.")
            .That(start).IsStrictlyGreaterThan(-1);
        // Expression-bodied member: read to its terminating semicolon.
        var method = source[start..source.IndexOf(';', start)];

        Check.That(method.Contains("NativeMethods.ShowWindowAsync")).IsTrue();
        Check.WithCustomMessage(
                "the async seam is wired to the blocking ShowWindow: every action that uses it "
                + "would wait on a hung target again.")
            .That(method.Contains("NativeMethods.ShowWindow(")).IsFalse();
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
