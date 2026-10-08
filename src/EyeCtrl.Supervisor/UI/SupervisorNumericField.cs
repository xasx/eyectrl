namespace EyeCtrl.Supervisor.UI;

using System.Globalization;

/// <summary>
/// The format/parse pair the setup window's two advanced integer fields use
/// (<c>MaxRetriesBox</c>, <c>HangThresholdBox</c>).
/// <para>
/// Extracted so the round trip has a headless test: the pair is the only thing that decides
/// whether what the window writes can be read back, and a format that adds a group separator
/// (<c>:N0</c>) would render "1,234" — unparseable by <see cref="NumberStyles.Integer"/> — with
/// nothing thrown. The provider is the <c>supervisor</c> surface's (T02), so the text follows the
/// language the rest of the window shows.
/// </para>
/// </summary>
internal static class SupervisorNumericField
{
    /// <summary>Formats an integer for the field, through the surface's provider.</summary>
    /// <param name="value">The value to render.</param>
    /// <param name="provider">The supervisor surface's format provider.</param>
    /// <returns>The text the box shows.</returns>
    public static string Format(int value, IFormatProvider provider) =>
        value.ToString(provider);

    /// <summary>Reads an integer back from the field, through the same provider.</summary>
    /// <param name="text">The box's text.</param>
    /// <param name="provider">The supervisor surface's format provider.</param>
    /// <param name="value">The parsed value when the text is a plain integer.</param>
    /// <returns>True when the text parsed.</returns>
    public static bool TryParse(string? text, IFormatProvider provider, out int value) =>
        int.TryParse(text, NumberStyles.Integer, provider, out value);
}
