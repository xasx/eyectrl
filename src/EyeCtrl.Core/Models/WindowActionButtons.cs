namespace EyeCtrl.Core.Models;

/// <summary>The toolbar ids + glyphs for the window-actions region, as a single source of truth.</summary>
public static class WindowActionButtons
{
    /// <summary>All three window-action region buttons (id + symbol), in display order.</summary>
    public static readonly (string Id, string Symbol)[] All =
    {
        ("Minimize", "\u2581"),
        ("MaximizeRestore", "\u25A1"),
        ("Close", "\u2715"),
    };

    /// <summary>The set of window-action region ids, for membership checks.</summary>
    public static readonly HashSet<string> Ids = new(All.Select(b => b.Id));

    /// <summary>Returns the canonical glyph for the given window-action <paramref name="id"/>.</summary>
    public static string SymbolFor(string id) => All.First(b => b.Id == id).Symbol;

    /// <summary>Returns whether <paramref name="id"/> is one of the window-action region buttons.</summary>
    public static bool IsWindowActionId(string id) => Ids.Contains(id);
}
