namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class QuickActionsSettingsDefaultsTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var s = new QuickActionsSettings();
        Check.That(s.Enabled).IsTrue();
        Check.That(s.SummonEdge).IsEqualTo("Left");
        Check.That(s.SummonThresholdMs).IsEqualTo(400);
        Check.That(s.DismissTimeoutMs).IsEqualTo(5000);
        Check.That(s.GridColumns).IsEqualTo(3);
    }

    [Fact]
    public void Validate_Defaults_NoErrors()
    {
        Check.That(new QuickActionsSettings().Validate()).IsEmpty();
    }

    [Fact]
    public void Validate_SummonThresholdOutOfRange_ReturnsError()
    {
        var s = new QuickActionsSettings { SummonThresholdMs = 150 };
        Check.That(s.Validate()).HasElementThatMatches(e => e.Contains(nameof(QuickActionsSettings.SummonThresholdMs)));
    }

    [Fact]
    public void Validate_SummonThresholdAtBounds_NoErrors()
    {
        var s = new QuickActionsSettings();
        s.SummonThresholdMs = 200;
        Check.That(s.Validate().All(e => !e.Contains("SummonThresholdMs"))).IsTrue();
        s.SummonThresholdMs = 2000;
        Check.That(s.Validate().All(e => !e.Contains("SummonThresholdMs"))).IsTrue();
    }

    [Fact]
    public void Validate_DismissTimeoutOutOfRange_ReturnsError()
    {
        var s = new QuickActionsSettings { DismissTimeoutMs = 500 };
        Check.That(s.Validate()).HasElementThatMatches(e => e.Contains(nameof(QuickActionsSettings.DismissTimeoutMs)));
    }

    [Fact]
    public void Validate_GridColumnsOutOfRange_ReturnsError()
    {
        var s = new QuickActionsSettings { GridColumns = 1 };
        Check.That(s.Validate()).HasElementThatMatches(e => e.Contains(nameof(QuickActionsSettings.GridColumns)));
        s.GridColumns = 6;
        Check.That(s.Validate()).HasElementThatMatches(e => e.Contains(nameof(QuickActionsSettings.GridColumns)));
    }

    [Fact]
    public void Validate_InvalidSummonEdge_ReturnsError()
    {
        var s = new QuickActionsSettings { SummonEdge = "Northeast" };
        Check.That(s.Validate()).HasElementThatMatches(e => e.Contains(nameof(QuickActionsSettings.SummonEdge)));
    }
}
