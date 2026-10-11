namespace EyeCtrl.App.Views.Settings;

using System;
using System.Globalization;

/// <summary>
/// The "Last calibration" date, rendered in the form the Settings surface's current language
/// reads unambiguously.
/// <para>
/// The user's call (2026-10-08): while Settings renders English — its provider is
/// <see cref="CultureInfo.InvariantCulture"/> — the date shows the sortable, unambiguous
/// <c>yyyy-MM-dd HH:mm</c> ("2026-10-08 14:03"), because "10/08/2026" reads as 10 August to a
/// German reader. Once the surface renders German the provider is de-DE and the date uses that
/// culture's own general date/time pattern (<c>g</c>, "08.10.2026 14:03"), which is what a German
/// reader expects. A third provider would take the <c>g</c> branch.
/// </para>
/// <para>
/// A pure string helper rather than an inline format: when T07 keys the sentence, the template
/// carries a plain <c>{0}</c> for the date, so the validator's German specifier parity holds.
/// </para>
/// </summary>
internal static class CalibrationDateText
{
    /// <summary>Renders a calibration timestamp for the Settings surface.</summary>
    /// <param name="value">The date/time to render (the page passes <c>LocalDateTime</c>).</param>
    /// <param name="provider">The surface's format provider from <c>StringTable.FormatProviderFor</c>.</param>
    /// <returns>The date string the "Last calibration" label interpolates.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is null.</exception>
    public static string Format(DateTime value, IFormatProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var english = provider is CultureInfo culture && culture.Equals(CultureInfo.InvariantCulture);
        return english
            ? value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : value.ToString("g", provider);
    }
}
