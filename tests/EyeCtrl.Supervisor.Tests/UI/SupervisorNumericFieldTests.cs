namespace EyeCtrl.Supervisor.Tests.UI;

using System.Globalization;
using EyeCtrl.Core.Localization;
using EyeCtrl.Supervisor.UI;

/// <summary>
/// The setup window's two advanced integer fields (<c>MaxRetriesBox</c>, <c>HangThresholdBox</c>)
/// must round-trip: whatever <see cref="SupervisorNumericField.Format"/> writes,
/// <see cref="SupervisorNumericField.TryParse"/> must read back. A format that adds a group
/// separator (<c>:N0</c>) renders "1,234", which <see cref="NumberStyles.Integer"/> refuses —
/// the Save button would silently keep the old value, and nothing would throw.
///
/// <para>
/// The provider is the <c>supervisor</c> surface's (T02), and the test runs the pair under de-DE
/// and fr-FR too, so the guard is meaningful on an English CI box rather than resting on the
/// developer's machine locale.
/// </para>
/// </summary>
public class SupervisorNumericFieldTests
{
    /// <summary>Providers the round trip must survive, the surface's own among them.</summary>
    public static TheoryData<IFormatProvider> Providers()
    {
        var data = new TheoryData<IFormatProvider>
        {
            CultureInfo.InvariantCulture,
            CultureInfo.GetCultureInfo("de-DE"),
            CultureInfo.GetCultureInfo("fr-FR"),
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void FormatThenParse_RoundTripsEveryValue(IFormatProvider provider)
    {
        foreach (var value in new[] { 0, 1, 3, 30, 1234, -7 })
        {
            var text = SupervisorNumericField.Format(value, provider);

            Check.WithCustomMessage(
                    $"'{text}' (formatted from {value} under {provider}) did not parse back.")
                .That(SupervisorNumericField.TryParse(text, provider, out var parsed)).IsTrue();
            Check.That(parsed).IsEqualTo(value);
        }
    }

    /// <summary>
    /// The two fields as the window drives them: the same surface provider on both sides of the
    /// round trip, over the values their sliders and defaults actually carry.
    /// </summary>
    [Fact]
    public void TheTwoSetupFields_RoundTripThroughTheSupervisorSurfaceProvider()
    {
        var provider = StringTable.Current.FormatProviderFor("supervisor");

        foreach (var value in new[] { 3, 30, 1234 })
        {
            var maxRetries = SupervisorNumericField.Format(value, provider);
            var hangThreshold = SupervisorNumericField.Format(value, provider);

            Check.That(SupervisorNumericField.TryParse(maxRetries, provider, out var parsedRetries)).IsTrue();
            Check.That(parsedRetries).IsEqualTo(value);
            Check.That(SupervisorNumericField.TryParse(hangThreshold, provider, out var parsedHang)).IsTrue();
            Check.That(parsedHang).IsEqualTo(value);
        }
    }

    /// <summary>
    /// The mutation this file exists for: a <c>:N0</c> format would render 1234 as "1,234"
    /// (invariant) or "1.234" (de-DE), and neither reads back under
    /// <see cref="NumberStyles.Integer"/>. Pinned from both sides — the format emits plain
    /// digits, and the parser refuses a separated value.
    /// </summary>
    [Fact]
    public void Format_NeverAddsAGroupSeparator_AndTheParserRefusesOne()
    {
        Check.That(SupervisorNumericField.Format(1234, CultureInfo.InvariantCulture)).IsEqualTo("1234");
        Check.That(SupervisorNumericField.Format(1234, CultureInfo.GetCultureInfo("de-DE"))).IsEqualTo("1234");

        Check.That(SupervisorNumericField.TryParse("1,234", CultureInfo.InvariantCulture, out _)).IsFalse();
        Check.That(SupervisorNumericField.TryParse(
            "1.234", CultureInfo.GetCultureInfo("de-DE"), out _)).IsFalse();
    }
}
