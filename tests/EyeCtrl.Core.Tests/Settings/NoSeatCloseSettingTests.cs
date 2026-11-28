namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

/// <summary>
/// The no-seat auto-close knobs (T10): a JSON-only int duration, default 1000 ms (0 turns the close
/// off, everything else 300–5000), and a JSON-only DIP margin, default 40 (0–400). There is
/// deliberately no Settings row and no user-visible string yet (the coordinator's D4 batch owns
/// those), so the whole contract is the stored values plus their validation.
/// </summary>
public class NoSeatCloseSettingTests
{
    private static string TestMutexName() =>
        @"Local\EyeCtrl.Settings.Writer.Test." + Guid.NewGuid().ToString("N");

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");

    [Fact]
    public void Default_IsOneSecond()
    {
        Check.That(new ManualSelectionSettings().NoSeatCloseMs).IsEqualTo(1000);
    }

    [Fact]
    public void MarginDefault_IsFortyDip()
    {
        Check.That(new ManualSelectionSettings().NoSeatCloseMarginLogical).IsEqualTo(40.0);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(299, false)]
    [InlineData(300, true)]
    [InlineData(5000, true)]
    [InlineData(5001, false)]
    public void Validate_AcceptsZeroOr300To5000(int value, bool accepted)
    {
        var settings = new ManualSelectionSettings { NoSeatCloseMs = value };

        var errors = settings.Validate();

        Check.WithCustomMessage(
                $"NoSeatCloseMs={value} should be {(accepted ? "accepted" : "rejected")}.")
            .That(errors.Any(e => e.Contains(
                nameof(ManualSelectionSettings.NoSeatCloseMs), StringComparison.Ordinal)))
            .IsEqualTo(!accepted);
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(400.0, true)]
    [InlineData(400.1, false)]
    [InlineData(-1.0, false)]
    public void MarginValidate_AcceptsZeroTo400(double value, bool accepted)
    {
        var settings = new ManualSelectionSettings { NoSeatCloseMarginLogical = value };

        var errors = settings.Validate();

        Check.WithCustomMessage(
                $"NoSeatCloseMarginLogical={value} should be {(accepted ? "accepted" : "rejected")}.")
            .That(errors.Any(e => e.Contains(
                nameof(ManualSelectionSettings.NoSeatCloseMarginLogical), StringComparison.Ordinal)))
            .IsEqualTo(!accepted);
    }

    [Fact]
    public void Json_RoundTrip_KeepsTheValue()
    {
        var path = TempPath();
        try
        {
            var settings = new EyeCtrlSettings();
            settings.ManualSelection.NoSeatCloseMs = 4000;
            settings.ManualSelection.NoSeatCloseMarginLogical = 120.0;
            var service = new SettingsService(path, SettingsWriter.App, TestMutexName());

            service.Save(settings);

            var loaded = new SettingsService(path).Load();
            Check.That(loaded.ManualSelection.NoSeatCloseMs).IsEqualTo(4000);
            Check.That(loaded.ManualSelection.NoSeatCloseMarginLogical).IsEqualTo(120.0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OldFileWithoutTheKey_LoadsTheDefault_AndKeepsEveryOtherValue()
    {
        // The owner's file predates the key. Loading it must take the default — not fail validation
        // and drop the section (the tightening-a-settings-guard-wipes-the-file trap) — and the next
        // Save must leave every neighbouring value untouched.
        var path = TempPath();
        try
        {
            File.WriteAllText(path, """
            {
                "manualSelection": {
                    "circleRadiusPixels": 150,
                    "magnificationLevel": 2.0,
                    "manualSelectionGracePeriodMs": 1200,
                    "holdRadiusLogical": 60.0,
                    "stabilitySampleCount": 5,
                    "holdOutlierSkipLimit": 10
                },
                "dwell": { "activationButtonDwellMs": 800 }
            }
            """);

            var service = new SettingsService(path, SettingsWriter.App, TestMutexName());
            var loaded = service.Load();

            Check.That(loaded.ManualSelection.NoSeatCloseMs).IsEqualTo(1000);
            Check.That(loaded.ManualSelection.NoSeatCloseMarginLogical).IsEqualTo(40.0);
            AssertEveryOtherValue(loaded);

            service.Save(loaded);
            var again = new SettingsService(path).Load();

            Check.That(again.ManualSelection.NoSeatCloseMs).IsEqualTo(1000);
            Check.That(again.ManualSelection.NoSeatCloseMarginLogical).IsEqualTo(40.0);
            AssertEveryOtherValue(again);
            Check.WithCustomMessage("The Save must persist the keys explicitly, never drop their section.")
                .That(File.ReadAllText(path)).Contains("\"noSeatCloseMs\": 1000");
            Check.That(File.ReadAllText(path)).Contains("\"noSeatCloseMarginLogical\": 40");
        }
        finally { File.Delete(path); }
    }

    private static void AssertEveryOtherValue(EyeCtrlSettings settings)
    {
        Check.That(settings.ManualSelection.CircleRadiusPixels).IsEqualTo(150);
        Check.That(settings.ManualSelection.MagnificationLevel).IsEqualTo(2.0);
        Check.That(settings.ManualSelection.ManualSelectionGracePeriodMs).IsEqualTo(1200);
        Check.That(settings.ManualSelection.HoldRadiusLogical).IsEqualTo(60.0);
        Check.That(settings.ManualSelection.StabilitySampleCount).IsEqualTo(5);
        Check.That(settings.ManualSelection.HoldOutlierSkipLimit).IsEqualTo(10);
        Check.That(settings.Dwell.ActivationButtonDwellMs).IsEqualTo(800);
    }
}
