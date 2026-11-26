namespace EyeCtrl.App.Tests.Backstage;

using System;
using System.Globalization;
using EyeCtrl.App.Services;

/// <summary>
/// The header clock's formatting ruling (Ticket 03 scope addition, l10n 2026-11-25): the culture's
/// short time pattern, the full weekday plus <c>MonthDayPattern</c> with no year, and the
/// UI-language culture supplied by the caller — never <c>CurrentCulture</c>.
/// </summary>
public class BackstageClockTests
{
    // 25 November 2025 is a Tuesday, matching the ruling's „Dienstag, 25. November" example.
    private static readonly DateTime Sample = new(2025, 11, 25, 14, 32, 0);

    [Fact]
    public void GermanCulture_GivesTwentyFourHourTime_AndTheLongGermanDate_WithoutAYear()
    {
        var text = BackstageClock.Format(Sample, CultureInfo.GetCultureInfo("de-DE"));

        Check.That(text.Time).IsEqualTo("14:32");
        Check.That(text.Date).IsEqualTo("Dienstag, 25. November");
        Check.That(text.Date.Contains("2026", StringComparison.Ordinal)).IsFalse();
    }

    [Fact]
    public void InvariantCulture_IsTheEnglishFallback_TheSameShapeAsTheGermanOne()
    {
        var text = BackstageClock.Format(Sample, CultureInfo.InvariantCulture);

        Check.That(text.Time).IsEqualTo("14:32");
        Check.That(text.Date).IsEqualTo("Tuesday, November 25");
        Check.That(text.Date.Contains("2026", StringComparison.Ordinal)).IsFalse();
    }

    [Fact]
    public void TheProviderDecides_NotTheProcessCurrentCulture()
    {
        // A German UI on an English-region Windows must not read "Tuesday" beside the German menu.
        // The caller passes the StringTable's UI-language provider; the formatter never reads the
        // process culture. Passing de-DE while the process is whatever the test host is proves it.
        var text = BackstageClock.Format(Sample, CultureInfo.GetCultureInfo("de-DE"));
        Check.That(text.Date.StartsWith("Dienstag", StringComparison.Ordinal)).IsTrue();
    }
}
