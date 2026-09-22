namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class EyeCtrlSettingsEnumerationTests
{
    [Fact]
    public void EnumerateSubSettings_Yields21Entries_AllValidatable()
    {
        var subs = new EyeCtrlSettings().EnumerateSubSettings().ToList();

        Check.That(subs).HasSize(21);
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

    [Fact]
    public void NonSectionWritableProperties_AreExactlyTheRootScalars()
    {
        // #153 review F2: SettingsService's tolerant path derives its root-scalar probe set
        // by THIS filter (public setter, not a section/IValidatable) — reflection, not a
        // hand-maintained list, so a NEW root scalar joins the repair automatically. This
        // pin makes such an addition visible: update the expected set consciously and add
        // repair coverage for the new property's failure shapes.
        var nonSection = typeof(EyeCtrlSettings).GetProperties()
            .Where(p => p.GetSetMethod() is not null
                && p.GetIndexParameters().Length == 0
                && !typeof(IValidatable).IsAssignableFrom(p.PropertyType))
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToList();

        Check.That(nonSection).ContainsExactly("SelectionMode", "ViewPosition");
    }
}
