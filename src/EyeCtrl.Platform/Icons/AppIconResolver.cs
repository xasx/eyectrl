namespace EyeCtrl.Platform.Icons;

using System.Windows.Media;

/// <summary>
/// The icon-source decision, as a pure function: packaged app (AUMID) → executable → the window's
/// own icon → the neutral default glyph. First source that produces an image wins.
///
/// <para>
/// The order belongs to THIS class, not to the supplied list: sources may arrive in any order (DI,
/// a test's shuffled list) and the canonical walk still decides. Every source is contained: a throw
/// or a null falls through to the next one, so a single misbehaving extraction can never leave a
/// tile without an icon or break the switcher's opening path.
/// </para>
/// </summary>
internal static class AppIconResolver
{
    /// <summary>The canonical decision order (S3-B): UWP → path → window icon → default.</summary>
    internal static readonly AppIconSourceKind[] Order =
    [
        AppIconSourceKind.PackagedApp,
        AppIconSourceKind.Executable,
        AppIconSourceKind.Window,
        AppIconSourceKind.Default,
    ];

    /// <summary>
    /// Resolves the icon and the source that produced it (the source kind is a closed enum, so it
    /// is safe to log — a path or an app name is not).
    /// </summary>
    internal static (AppIconSourceKind Kind, ImageSource Icon) Resolve(
        IReadOnlyList<IAppIconSource> sources, IconRequest request)
    {
        foreach (var kind in Order)
        {
            foreach (var source in sources)
            {
                if (source.Kind != kind)
                    continue;

                try
                {
                    if (source.TryGetIcon(request) is { } icon)
                        return (kind, icon);
                }
                catch
                {
                    // Extraction is decoration: a broken source falls through, never throws.
                }
            }
        }

        // Defensive: a source list without a Default source must still never yield an empty hole.
        return (AppIconSourceKind.Default, DefaultGlyphIconSource.Glyph);
    }
}
