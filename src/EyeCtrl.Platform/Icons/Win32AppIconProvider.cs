namespace EyeCtrl.Platform.Icons;

using System.Windows.Media;
using Microsoft.Extensions.Logging;

/// <summary>
/// The Task Switcher's icon provider (S3-B): composes the four sources in
/// <see cref="AppIconResolver.Order"/> and guarantees a drawn image for every window.
///
/// <para>
/// <b>Never blocks the gaze path for long.</b> Every source is read-only and non-throwing; the only
/// cross-process wait in the whole path is the bounded <c>SendMessageTimeout</c> in the window-icon
/// source (100 ms, <c>SMTO_ABORTIFHUNG</c>), and it runs last before the default, after the exe path
/// and AUMID sources have answered for ordinary and Store apps.
/// </para>
///
/// <para>
/// <b>Privacy.</b> The only log line names the closed <see cref="AppIconSourceKind"/> — never a path,
/// an AUMID or an app name.
/// </para>
/// </summary>
public sealed class Win32AppIconProvider : IAppIconProvider
{
    private readonly IReadOnlyList<IAppIconSource> _sources;
    private readonly ILogger<Win32AppIconProvider>? _logger;

    /// <summary>Creates the provider with the real Win32/COM sources (the DI singleton).</summary>
    /// <param name="logger">Optional: a Debug line naming the resolved source kind only.</param>
    public Win32AppIconProvider(ILogger<Win32AppIconProvider>? logger = null)
        : this(CreateSources(), logger)
    {
    }

    /// <summary>Test seam: any source list; the resolver still walks the canonical order.</summary>
    internal Win32AppIconProvider(IReadOnlyList<IAppIconSource> sources, ILogger<Win32AppIconProvider>? logger = null)
    {
        _sources = sources;
        _logger = logger;
    }

    /// <inheritdoc />
    public ImageSource GetIcon(IntPtr hwnd, uint processId)
    {
        var (kind, icon) = AppIconResolver.Resolve(_sources, new IconRequest(hwnd, processId));
        _logger?.LogDebug("Task Switcher icon resolved from {Source}.", kind);
        return icon;
    }

    private static IReadOnlyList<IAppIconSource> CreateSources()
    {
        var identity = new Win32WindowIdentityProbe();
        var shell = new ShellImageSource();
        return
        [
            new PackagedAppIconSource(identity, shell),
            new ExecutableIconSource(identity, shell),
            new WindowIconSource(new WindowMessageIconReader()),
            new DefaultGlyphIconSource(),
        ];
    }
}
