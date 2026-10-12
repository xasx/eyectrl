namespace EyeCtrl.Controls.Tests.Theme;

using System.Linq;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

public class ThemeRegistryTests
{
    [Fact]
    public void All_ListsMidnightFirst()
    {
        // Order is the picker's order, and Midnight is the shipping default.
        Check.That(ThemeRegistry.All.Select(t => t.Name).ToList())
             .ContainsExactly("Midnight", "Ember", "Daylight", "Contrast");
    }

    [Theory]
    [InlineData("Midnight")]
    [InlineData("Ember")]
    [InlineData("Daylight")]
    [InlineData("Contrast")]
    public void Resolve_ReturnsTheNamedTheme(string name)
    {
        Check.That(ThemeRegistry.Resolve(name).Name).IsEqualTo(name);
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        // settings.json is hand-editable; a lowercase name must not silently
        // downgrade the user to Midnight.
        Check.That(ThemeRegistry.Resolve("ember").Name).IsEqualTo("Ember");
    }

    [Theory]
    [InlineData("Nonexistent")]
    [InlineData("")]
    [InlineData(null)]
    public void Resolve_FallsBackToMidnight_ForAnUnknownName(string? name)
    {
        // A corrupt or hand-edited settings.json must not leave the UI unthemed.
        Check.That(ThemeRegistry.Resolve(name!).Name).IsEqualTo("Midnight");
    }

    [Fact]
    public void Names_MatchesAll()
    {
        Check.That(ThemeRegistry.Names).ContainsExactly(ThemeRegistry.All.Select(t => t.Name));
    }
}
