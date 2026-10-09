namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Localization;

/// <summary>
/// The startup-error box's two texts, resolved together from the process table. Resolving both in
/// one call is the point: the title is also the exact string the 20-second auto-dismiss searches
/// for (<c>DismissMessageBoxAfter</c>), so a second resolution could differ from the one the box
/// was shown with and the box would never close.
/// <para>
/// The process table defaults to the compiled English before any load, so this resolves even when
/// startup failed before <c>StringTable.Activate</c> ran — the case the box exists for.
/// </para>
/// </summary>
/// <param name="Title">The box caption; also the exact title the auto-dismiss searches for.</param>
/// <param name="Message">The box body, with the raw failure detail under its label.</param>
internal sealed record StartupErrorText(string Title, string Message)
{
    /// <summary>Resolves both texts once for <paramref name="detail"/>.</summary>
    /// <param name="detail">The raw failure detail (English, or the Windows language for OS errors).</param>
    /// <returns>The resolved title and message.</returns>
    public static StartupErrorText Resolve(string detail)
    {
        var table = StringTable.Current;
        return new StartupErrorText(
            table.T("startup-error.title"),
            table.T("startup-error.message", detail));
    }
}
