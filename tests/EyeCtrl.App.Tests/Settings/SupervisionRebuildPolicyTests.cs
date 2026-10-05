namespace EyeCtrl.App.Tests.Settings;

using EyeCtrl.App.Views.Settings;
using NFluent;
using Xunit;

/// <summary>
/// Ticket 07 review F3: a supervision state change must only rebuild the content while the
/// General tab (the only tab showing those rows) is current. A rebuild resets the open tab's
/// in-progress Control dwells under a gaze that cannot re-aim at a moving target.
/// </summary>
public class SupervisionRebuildPolicyTests
{
    [Theory]
    [InlineData("General", true)]
    [InlineData("Keyboard", false)]
    [InlineData("Dwell", false)]
    [InlineData("Cursor", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ShouldRebuildNow_OnlyOnTheGeneralTab(string? tab, bool expected) =>
        Check.That(SupervisionRebuildPolicy.ShouldRebuildNow(tab)).IsEqualTo(expected);

    [Fact]
    public void GeneralTabId_IsTheRegistryId() =>
        Check.That(SupervisionRebuildPolicy.GeneralTabId).IsEqualTo("General");
}
