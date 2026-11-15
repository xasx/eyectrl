namespace EyeCtrl.Platform.Icons;

using System.Windows.Media;

/// <summary>
/// S3-B source 2: the window's executable icon, read through
/// <c>QueryFullProcessImageName</c> with <c>PROCESS_QUERY_LIMITED_INFORMATION</c>. That right is
/// granted for elevated and other-user processes (Task Manager included), which is why this source
/// works where <c>Process.MainModule</c> threw Access Denied; the shell then extracts a real
/// 64–96 px image from the file, so the icon is sharp at 200 % instead of a 32 px upscale.
///
/// <para>
/// Frame-host windows are SKIPPED: their exe is <c>ApplicationFrameHost.exe</c>, so this source
/// would hand every Store app the frame host's icon — exactly the wrongness S3-B removes. The
/// packaged source owns those windows; if it finds no AUMID the window's own icon/default wins.
/// </para>
///
/// <para>
/// Cached per executable path for the app's lifetime; a failed extraction is cached too (a negative
/// entry), so a window whose icon cannot be read does not re-pay the lookup on every open.
/// </para>
/// </summary>
internal sealed class ExecutableIconSource : IAppIconSource
{
    private readonly IWindowIdentityProbe _identity;
    private readonly IShellImageSource _shell;
    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    internal ExecutableIconSource(IWindowIdentityProbe identity, IShellImageSource shell)
    {
        _identity = identity;
        _shell = shell;
    }

    public AppIconSourceKind Kind => AppIconSourceKind.Executable;

    /// <summary>Distinct exe paths extracted by this source (a cache hit does not count).</summary>
    internal int ExtractionCount { get; private set; }

    public ImageSource? TryGetIcon(IconRequest request)
    {
        var className = _identity.TryGetClassName(request.Hwnd);
        var path = _identity.TryGetExecutablePath(request.ProcessId);
        if (WindowIdentityFacts.IsFrameHost(className, path))
            return null;

        if (string.IsNullOrWhiteSpace(path))
            return null;

        lock (_gate)
        {
            if (_cache.TryGetValue(path, out var cached))
                return cached;
        }

        var icon = _shell.TryGetImage(path, ShellImageSource.RequestedSizePx);

        lock (_gate)
        {
            _cache[path] = icon;
            ExtractionCount++;
        }

        return icon;
    }
}
