namespace EyeCtrl.App.Tests.Settings;

using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.App.Views.Settings;
using NFluent;
using Xunit;

/// <summary>
/// T02 fixup-02, the user's date pick (2026-10-08): the Settings surface's "Last calibration"
/// date shows the unambiguous <c>yyyy-MM-dd HH:mm</c> while the surface renders English, and the
/// provider's own <c>g</c> once it renders German. The rule lives in
/// <see cref="CalibrationDateText"/>, and the page must route through it.
/// </summary>
public class CalibrationDateTextTests
{
    private static readonly DateTime Sample = new(2026, 10, 8, 14, 3, 0);

    [Fact]
    public void EnglishSettings_RendersTheUnambiguousSortableDate()
    {
        Check.That(CalibrationDateText.Format(Sample, CultureInfo.InvariantCulture))
            .IsEqualTo("2026-10-08 14:03");
    }

    [Fact]
    public void GermanSettings_RendersTheProvidersOwnGeneralDate()
    {
        Check.That(CalibrationDateText.Format(Sample, CultureInfo.GetCultureInfo("de-DE")))
            .IsEqualTo("08.10.2026 14:03");
    }

    /// <summary>
    /// The helper is not enough on its own: the page could keep formatting <c>CalibratedAt</c>
    /// inline with <c>:g</c> and every test above would stay green. This reads the page's source
    /// and pins the call plus the absence of the inline general format.
    /// </summary>
    [Fact]
    public void CalibrationPageSource_UsesTheHelper_AndNoInlineGeneralFormat()
    {
        var text = LocalizationTestData.SourceFiles()
            .Single(f => f.Relative == "src/EyeCtrl.App/Views/Settings/Sources/CalibrationPageSource.cs")
            .Text;

        Check.WithCustomMessage(
                "the status label no longer renders its date through CalibrationDateText, so the "
                + "English screen falls back to some other order.")
            .That(text.Contains("CalibrationDateText.Format(", StringComparison.Ordinal)).IsTrue();

        Check.WithCustomMessage(
                "the status label formats CalibratedAt inline with :g again — on an English screen "
                + "that is the month-first US order the user rejected.")
            .That(Regex.IsMatch(text, @"CalibratedAt[^;\n]*:g")).IsFalse();
    }
}
