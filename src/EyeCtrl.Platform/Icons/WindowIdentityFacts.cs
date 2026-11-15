namespace EyeCtrl.Platform.Icons;

using System.IO;

/// <summary>
/// The one place that answers "is this window a Store/UWP frame?" — the question both the packaged
/// source (who should read the AUMID) and the executable source (who must NOT read the frame host's
/// own exe icon) depend on.
///
/// <para>
/// The ticket's condition is "the process is <c>ApplicationFrameHost</c>". It is observed here from
/// the window, without opening a <c>Process</c>: the frame's class name
/// (<c>ApplicationFrameWindow</c>, stable and not localized) or the exe file name
/// (<c>ApplicationFrameHost.exe</c>, read through the same limited-rights path the executable source
/// uses, so elevated frames are recognised too).
/// </para>
/// </summary>
internal static class WindowIdentityFacts
{
    /// <summary>The top-level class of a Windows 10/11 UWP app frame.</summary>
    internal const string FrameHostClassName = "ApplicationFrameWindow";

    /// <summary>The frame host's executable file name.</summary>
    internal const string FrameHostExeName = "ApplicationFrameHost.exe";

    /// <summary>True when the window hosts a packaged app inside the ApplicationFrameHost frame.</summary>
    internal static bool IsFrameHost(IWindowIdentityProbe identity, IconRequest request)
        => IsFrameHost(identity.TryGetClassName(request.Hwnd), identity.TryGetExecutablePath(request.ProcessId));

    /// <summary>
    /// The same decision over facts that were already read, so a source that needs the exe path
    /// anyway pays for one read instead of two.
    /// </summary>
    internal static bool IsFrameHost(string? className, string? executablePath)
        => string.Equals(className, FrameHostClassName, StringComparison.OrdinalIgnoreCase)
           || string.Equals(ExecutableFileName(executablePath), FrameHostExeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The file name of a path (null/empty for no path).</summary>
    internal static string? ExecutableFileName(string? path)
        => string.IsNullOrEmpty(path) ? null : Path.GetFileName(path);
}
