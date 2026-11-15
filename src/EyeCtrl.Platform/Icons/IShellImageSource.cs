namespace EyeCtrl.Platform.Icons;

using System.Windows.Media;

/// <summary>
/// The shell's large-image provider behind a seam. It hands back a 64–96 px source for a filesystem
/// path (an executable) or a <c>shell:AppsFolder\{AUMID}</c> parsing name — the source sizes the
/// design's 40 DIP icon at 200 % needs (80 physical px), where <c>ExtractAssociatedIcon</c> topped
/// out at 32 px.
/// </summary>
internal interface IShellImageSource
{
    /// <summary>An icon image of about <paramref name="sizePx"/> for the parsing name, or null.</summary>
    ImageSource? TryGetImage(string parsingName, int sizePx);
}
