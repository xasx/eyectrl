namespace EyeCtrl.Controls.Theme;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// The set of available themes, and the name-to-theme mapping used by settings and
/// the picker. Order is the picker's order: the shipping default first, the
/// all-day alternative next, then the situational ones — Daylight for bright
/// rooms, Contrast last as the boldest extreme (spec §10.7/§10.8).
/// </summary>
public static class ThemeRegistry
{
    /// <summary>Every theme, in display order.</summary>
    public static IReadOnlyList<ITheme> All { get; } =
    [
        MidnightTheme.Instance,
        EmberTheme.Instance,
        DaylightTheme.Instance,
        ContrastTheme.Instance,
    ];

    /// <summary>Theme names in the same order as <see cref="All"/>, for the settings picker.</summary>
    public static IReadOnlyList<string> Names { get; } = All.Select(t => t.Name).ToArray();

    /// <summary>
    /// Resolves a persisted theme name. Unknown, empty and null names fall back to
    /// Midnight rather than throwing: settings.json is hand-editable, and an
    /// unthemed UI would leave the user without a usable interface to fix it with.
    /// </summary>
    /// <param name="name">The persisted theme name; matched case-insensitively.</param>
    /// <returns>The named theme, or Midnight if it is not recognised.</returns>
    public static ITheme Resolve(string? name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? MidnightTheme.Instance;
}
