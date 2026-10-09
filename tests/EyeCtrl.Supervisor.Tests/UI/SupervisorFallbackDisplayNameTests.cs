namespace EyeCtrl.Supervisor.Tests.UI;

using EyeCtrl.Core.Localization;
using EyeCtrl.Supervisor.UI;

/// <summary>
/// T06: the persisted fallback ids get display names on the supervisor surface. Product names are
/// written out as English literals on the expected side by design — resolving both sides from the
/// keys would let a missing key pass as its own raw text.
/// </summary>
public class SupervisorFallbackDisplayNameTests
{
    [Theory]
    [InlineData("TDControl", "TD Control")]
    [InlineData("SnapCoreFirst", "Snap Core First")]
    [InlineData("WindowsEyeControl", "Windows Eye Control")]
    public void KnownIds_ShowTheirEnglishDisplayNames(string id, string expected)
    {
        Check.That(SupervisorSetupWindow.FallbackDisplayName(StringTable.EmbeddedEnglish, id))
            .IsEqualTo(expected);
    }

    [Fact]
    public void AnUnknownId_ShowsItself()
    {
        // A stale settings entry must stay visible as its id rather than vanish or throw.
        Check.That(SupervisorSetupWindow.FallbackDisplayName(StringTable.EmbeddedEnglish, "Nonsense"))
            .IsEqualTo("Nonsense");
    }
}
