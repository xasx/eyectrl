namespace EyeCtrl.Controls.Tests;

using EyeCtrl.Controls;
using NFluent;
using Xunit;

public class DwellSliderFormatTests
{
    [Fact]
    public void Format_IntegerFormat_TruncatesDecimal()
    {
        Check.That(DwellSlider.Format(0, 3.7)).IsEqualTo("3");
        Check.That(DwellSlider.Format(0, 0.0)).IsEqualTo("0");
        Check.That(DwellSlider.Format(0, -1.9)).IsEqualTo("-1");
    }

    [Fact]
    public void Format_DecimalFormat_UsesFixedPlaces()
    {
        Check.That(DwellSlider.Format(2, 3.14159)).IsEqualTo("3.14");
        Check.That(DwellSlider.Format(2, 0.0)).IsEqualTo("0.00");
    }

    [Fact]
    public void Format_ThreeDecimalPlaces()
    {
        Check.That(DwellSlider.Format(3, 1.23456)).IsEqualTo("1.235");
    }

    [Fact]
    public void Format_IntegerFormatWholeNumber()
    {
        Check.That(DwellSlider.Format(0, 42.0)).IsEqualTo("42");
    }
}
