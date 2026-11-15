namespace EyeCtrl.Platform.Icons;

using System.Windows.Media;

/// <summary>
/// S3-B source 1: a Store/UWP window's OWN app icon. Every packaged app runs inside the shared
/// <c>ApplicationFrameHost</c> process, so a per-process lookup can only ever return the frame
/// host's icon (or, worse, the same icon for every Store app). The window's property store carries
/// the hosted app's <c>System.AppUserModel.ID</c>; that AUMID is what the shell resolves to the
/// app's multi-size icon.
///
/// <para>
/// Cached per AUMID for the app's lifetime — the second open of the same app costs a dictionary
/// hit. The key is the AUMID, never the process id: two frame windows of one process are two
/// different apps.
/// </para>
/// </summary>
internal sealed class PackagedAppIconSource : IAppIconSource
{
    /// <summary>The shell namespace prefix that resolves an AUMID to its app item.</summary>
    internal const string AppsFolderPrefix = "shell:AppsFolder\\";

    private readonly IWindowIdentityProbe _identity;
    private readonly IShellImageSource _shell;
    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    internal PackagedAppIconSource(IWindowIdentityProbe identity, IShellImageSource shell)
    {
        _identity = identity;
        _shell = shell;
    }

    public AppIconSourceKind Kind => AppIconSourceKind.PackagedApp;

    /// <summary>AUMIDs resolved by this source since it was created (tests pin the cache with it).</summary>
    internal int ExtractionCount { get; private set; }

    public ImageSource? TryGetIcon(IconRequest request)
    {
        if (!WindowIdentityFacts.IsFrameHost(_identity, request))
            return null;

        var aumid = _identity.TryGetAppUserModelId(request.Hwnd);
        if (string.IsNullOrWhiteSpace(aumid))
            return null;

        lock (_gate)
        {
            if (_cache.TryGetValue(aumid, out var cached))
                return cached;
        }

        var icon = _shell.TryGetImage(AppsFolderPrefix + aumid, ShellImageSource.RequestedSizePx);

        lock (_gate)
        {
            _cache[aumid] = icon;
            ExtractionCount++;
        }

        return icon;
    }
}
