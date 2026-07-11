namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class EyeCtrlSettingsEnumerationTests
{
    [Fact]
    public void EnumerateSubSettings_Yields16Entries_AllValidatable()
    {
        var subs = new EyeCtrlSettings().EnumerateSubSettings().ToList();

        Check.That(subs).HasSize(16);
        Check.That(subs.All(t => t.Settings is IValidatable)).IsTrue();
    }

    [Fact]
    public void EnumerateSubSettings_CoversEveryValidatableProperty()
    {
        var expected = typeof(EyeCtrlSettings).GetProperties()
            .Where(p => typeof(IValidatable).IsAssignableFrom(p.PropertyType))
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToList();

        var actual = new EyeCtrlSettings().EnumerateSubSettings()
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        Check.That(actual).ContainsExactly(expected);
    }

    [Fact]
    public void Validate_DefaultInstance_HasNoErrors()
    {
        Check.That(new EyeCtrlSettings().Validate()).IsEmpty();
    }
}
