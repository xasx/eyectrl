namespace EyeCtrl.Platform.Tests.Icons;

using System.Windows;
using System.Windows.Media;
using EyeCtrl.Platform.Icons;

/// <summary>
/// Shared doubles for the S3-B icon tests: the two reads the sources depend on
/// (<see cref="EyeCtrl.Platform.Icons.IWindowIdentityProbe"/>, <see cref="EyeCtrl.Platform.Icons.IShellImageSource"/>)
/// plus small image factories. No COM, no window, no shell — the decisions are what is under test.
/// </summary>
internal static class IconFakes
{
    /// <summary>A distinct frozen image; the fill's width keeps the instances distinguishable.</summary>
    internal static ImageSource Image(int tag = 24)
    {
        var drawing = new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb((byte)(tag & 0xFF), 0x40, 0x80)),
            null,
            new RectangleGeometry(new Rect(0, 0, tag, tag)));
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}

/// <summary>
/// A scripted identity probe: per-hwnd class name/AUMID, per-pid exe path, and counters so a test
/// can prove an ordinary window never pays for the property-store read.
/// </summary>
internal sealed class FakeIdentity : IWindowIdentityProbe
{
    internal Dictionary<IntPtr, string> ClassNames { get; } = [];
    internal Dictionary<IntPtr, string> AppUserModelIds { get; } = [];
    internal Dictionary<uint, string> ExecutablePaths { get; } = [];
    internal int AumidReads { get; private set; }
    internal int PathReads { get; private set; }

    public string? TryGetClassName(IntPtr hwnd)
        => ClassNames.TryGetValue(hwnd, out var name) ? name : null;

    public string? TryGetExecutablePath(uint processId)
    {
        PathReads++;
        return ExecutablePaths.TryGetValue(processId, out var path) ? path : null;
    }

    public string? TryGetAppUserModelId(IntPtr hwnd)
    {
        AumidReads++;
        return AppUserModelIds.TryGetValue(hwnd, out var aumid) ? aumid : null;
    }
}

/// <summary>
/// A scripted shell image provider: records every parsing name and requested size, and returns a
/// per-name image (or null when the name is in <see cref="Unavailable"/>).
/// </summary>
internal sealed class FakeShellImages : IShellImageSource
{
    internal List<(string ParsingName, int SizePx)> Requests { get; } = [];
    internal HashSet<string> Unavailable { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ImageSource? TryGetImage(string parsingName, int sizePx)
    {
        Requests.Add((parsingName, sizePx));
        return Unavailable.Contains(parsingName) ? null : IconFakes.Image(parsingName.Length + 16);
    }
}

/// <summary>A source double: a recorded call, a canned answer, and an optional throw.</summary>
internal sealed class FakeIconSource : IAppIconSource
{
    private readonly ImageSource? _icon;
    private readonly bool _throws;

    internal FakeIconSource(AppIconSourceKind kind, ImageSource? icon = null, bool throws = false)
    {
        Kind = kind;
        _icon = icon;
        _throws = throws;
    }

    public AppIconSourceKind Kind { get; }

    internal int Calls { get; private set; }

    public ImageSource? TryGetIcon(IconRequest request)
    {
        Calls++;
        if (_throws)
            throw new InvalidOperationException("the fake source is broken on purpose");
        return _icon;
    }
}

/// <summary>A window-icon reader double: a recorded handle and a canned answer.</summary>
internal sealed class FakeWindowIconReader : IWindowIconReader
{
    private readonly ImageSource? _icon;

    internal FakeWindowIconReader(ImageSource? icon) => _icon = icon;

    internal IntPtr? LastHandle { get; private set; }

    internal int Calls { get; private set; }

    public ImageSource? TryReadIcon(IntPtr hwnd)
    {
        Calls++;
        LastHandle = hwnd;
        return _icon;
    }
}
