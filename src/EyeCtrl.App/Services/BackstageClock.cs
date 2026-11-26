namespace EyeCtrl.App.Services;

using System;
using System.Globalization;

/// <summary>The two rendered clock strings. A value so the change-only update can compare both
/// without touching WPF.</summary>
internal readonly record struct ClockText(string Time, string Date);

/// <summary>
/// The Backstage header clock's formatting (Ticket 03 scope addition, l10n ruling 2026-11-25):
/// the culture's short time pattern (no seconds), and the full weekday plus
/// <see cref="DateTimeFormatInfo.MonthDayPattern"/>, with no year.
///
/// <para>
/// The culture is the UI-<b>language</b> culture, supplied by
/// <see cref="EyeCtrl.Core.Localization.StringTable.FormatProviderFor"/> — de-DE when the German
/// UI actually renders, invariant otherwise. It must not be <c>CultureInfo.CurrentCulture</c>: a
/// German UI on an English-region Windows would otherwise read "Tuesday" beside „EyeCtrl
/// verwalten". No string-table key is used because the validator requires identical placeholder
/// format specifiers across languages, which a translated date format cannot satisfy.
/// </para>
/// </summary>
internal static class BackstageClock
{
    // The three literals are .NET date-format data, not UI prose: the short-time pattern
    // specifier, the full-weekday specifier, and the join between the date's two parts. They are
    // const string initialisers so the text-literal ratchet records them, and named in
    // localization/text-literal-exceptions.json as Category "data".
    private const string ShortTimePattern = "t";
    private const string FullWeekdayPattern = "dddd";
    private const string DatePartSeparator = ", ";

    /// <summary>Formats <paramref name="now"/> in <paramref name="provider"/>'s language culture.</summary>
    internal static ClockText Format(DateTime now, IFormatProvider provider)
    {
        var culture = provider as CultureInfo ?? CultureInfo.InvariantCulture;
        var time = now.ToString(ShortTimePattern, culture);
        var weekday = now.ToString(FullWeekdayPattern, culture);
        var monthDay = now.ToString(culture.DateTimeFormat.MonthDayPattern, culture);
        return new ClockText(time, string.Join(DatePartSeparator, weekday, monthDay));
    }
}
