namespace EyeCtrl.Platform.Tests.Services;

using System.IO;

/// <summary>
/// Fixup-01 F1, pinned at the source level: the window manager's set-minimise path must use the
/// non-blocking <c>ShowWindowAsync</c>. "Show desktop" runs on the UI thread, which is also the
/// gaze-dwell thread, so a synchronous <c>ShowWindow</c> on one hung window would freeze every
/// overlay (Pause included) and leave a gaze-only owner without control. The blocking
/// <c>Minimize</c> stays exactly as it is for the single-target Quick Actions path.
///
/// <para>
/// The P/Invoke layer is static, so this cannot be exercised by a fake at runtime; the call choice
/// is the whole fix, and a source pin is the honest seam. A runtime test that actually minimised a
/// window is forbidden here (no test may touch a real window).
/// </para>
/// </summary>
public class Win32WindowManagerMinimizeTests
{
    [Fact]
    public void MinimizeNonBlocking_UsesShowWindowAsync_AndTheBlockingMinimizeStaysUnchanged()
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

        // The existing single-target Quick Actions minimise keeps its blocking behaviour; this
        // ticket deliberately adds a separate seam method instead of changing it.
        var blocking = ExtractMethod(source, "public void Minimize(IntPtr hwnd)");
        Check.That(blocking.Contains("NativeMethods.ShowWindow(")).IsTrue();
        Check.That(blocking.Contains("ShowWindowAsync")).IsFalse();
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
