namespace EyeCtrl.Controls.Tests;

using System.Globalization;
using EyeCtrl.Controls;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

public class DwellSliderFormatTests
{
    [Fact]
    public void Format_IntegerFormat_TruncatesDecimal()
    {
        Check.That(DwellSlider.Format(0, 3.7, CultureInfo.InvariantCulture)).IsEqualTo("3");
        Check.That(DwellSlider.Format(0, 0.0, CultureInfo.InvariantCulture)).IsEqualTo("0");
        Check.That(DwellSlider.Format(0, -1.9, CultureInfo.InvariantCulture)).IsEqualTo("-1");
    }

    [Fact]
    public void Format_DecimalFormat_UsesFixedPlaces()
    {
        Check.That(DwellSlider.Format(2, 3.14159, CultureInfo.InvariantCulture)).IsEqualTo("3.14");
        Check.That(DwellSlider.Format(2, 0.0, CultureInfo.InvariantCulture)).IsEqualTo("0.00");
    }

    [Fact]
    public void Format_ThreeDecimalPlaces()
    {
        Check.That(DwellSlider.Format(3, 1.23456, CultureInfo.InvariantCulture)).IsEqualTo("1.235");
    }

    [Fact]
    public void Format_IntegerFormatWholeNumber()
    {
        Check.That(DwellSlider.Format(0, 42.0, CultureInfo.InvariantCulture)).IsEqualTo("42");
    }

    /// <summary>
    /// The formats above could pass by consulting <c>CultureInfo.CurrentCulture</c> on an
    /// English machine. This pins the provider as the only input: the same double renders a
    /// point decimal under the invariant culture and a comma under de-DE.
    /// </summary>
    [Fact]
    public void Format_UsesTheProvider_NotTheAmbientCulture()
    {
        Check.That(DwellSlider.Format(1, 1.5, CultureInfo.InvariantCulture)).IsEqualTo("1.5");
        Check.That(DwellSlider.Format(1, 1.5, CultureInfo.GetCultureInfo("de-DE"))).IsEqualTo("1,5");
    }

    /// <summary>
    /// T02's visible change, asserted through the real control: on this de-DE machine an English
    /// UI must render "1.5", because the host passes the settings surface's provider — invariant
    /// while the surface is English — and the slider never reads the ambient culture.
    ///
    /// <para>
    /// The ambient culture is forced here ON PURPOSE (and restored in <c>finally</c>), so the test
    /// is meaningful on an en-US CI box too. It is set on the test's own async-local
    /// <c>CurrentCulture</c>, never <c>DefaultThreadCurrentCulture</c>, and only inside this test.
    /// A slider that ignores its provider renders "1,5" and fails here.
    /// </para>
    /// </summary>
    [StaFact]
    public void EnglishUiOnADeDeMachine_RendersOnePointFive()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            var value = 1.5;
            var slider = new DwellSlider(
                new FakeDwellRegistrar(), "s", "Label", 0, 10, 0.5, 1,
                () => value, v => value = v, DwellCategory.Control, 50, 800,
                formatProvider: StringTable.Current.FormatProviderFor("settings"));

            Check.That(slider.ValueText.Text).IsEqualTo("1.5");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
