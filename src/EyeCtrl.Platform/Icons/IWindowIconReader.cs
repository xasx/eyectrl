namespace EyeCtrl.Platform.Icons;

using System.Windows.Media;

/// <summary>
/// Reads a window's OWN icon (S3-B source 3) behind a seam, so the fallback — and its time bound —
/// is testable. This source is deliberately last before the default: a window icon is usually only
/// 32–48 px, so it is a recognisability fallback, never the preferred source.
/// </summary>
internal interface IWindowIconReader
{
    /// <summary>The window's large icon, or null when the window has none or cannot answer.</summary>
    ImageSource? TryReadIcon(IntPtr hwnd);
}
