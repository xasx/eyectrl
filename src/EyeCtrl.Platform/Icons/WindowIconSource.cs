namespace EyeCtrl.Platform.Icons;

using System.Windows.Media;

/// <summary>
/// S3-B source 3: the window's own icon (<c>WM_GETICON</c> / class icon) through
/// <see cref="IWindowIconReader"/>. Not cached: it is a per-window fallback, and a window handle
/// must never be a long-lived cache key (handles are recycled).
/// </summary>
internal sealed class WindowIconSource : IAppIconSource
{
    private readonly IWindowIconReader _reader;

    internal WindowIconSource(IWindowIconReader reader) => _reader = reader;

    public AppIconSourceKind Kind => AppIconSourceKind.Window;

    public ImageSource? TryGetIcon(IconRequest request) => _reader.TryReadIcon(request.Hwnd);
}
