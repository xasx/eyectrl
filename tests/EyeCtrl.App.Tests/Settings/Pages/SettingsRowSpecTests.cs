namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using NFluent;
using Xunit;

public class SettingsRowSpecTests
{
    [Fact]
    public void SliderSpec_CarriesRangeAndBinding()
    {
        var slider = new SliderSpec("s", "Label", 0, 1, 0.1, 1, () => 0.5, _ => { });
        Check.That(slider.Id).IsEqualTo("s");
        Check.That(slider.Min).IsEqualTo(0);
        Check.That(slider.Max).IsEqualTo(1);
        Check.That(slider.Get()).IsEqualTo(0.5);
    }

    [Fact]
    public void ComboBoxSpec_NullCode_HasDisplayOnly()
    {
        var combo = new ComboBoxSpec("c", "L", new[] { "A", "B" }, null, () => "A", _ => { });
        Check.That(combo.Code).IsNull();
        Check.That(combo.Display).HasSize(2);
    }

    [Fact]
    public void Simple_DefaultsToFalse_AndIsSettableOnAnySpecKind()
    {
        var unflagged = new ToggleSpec("t1", "Label", () => true, _ => { });
        var flagged = new SliderSpec("s1", "Label", 0, 1, 0.1, 1, () => 0, _ => { }) { Simple = true };
        var flaggedLabel = new LabelSpec(() => "note") { Simple = true };

        Check.That(unflagged.Simple).IsFalse();
        Check.That(flagged.Simple).IsTrue();
        Check.That(flaggedLabel.Simple).IsTrue();
    }

    [Fact]
    public void PageSpec_HoldsRowsInOrder()
    {
        var page = new PageSpec(new SettingsRowSpec[]
        {
            new SliderSpec("a", "A", 0, 1, 1, 0, () => 0, _ => { }),
            new LabelSpec(() => "hi"),
        });
        Check.That(page.Rows).HasSize(2);
        Check.That(page.Rows[0]).IsInstanceOf<SliderSpec>();
        Check.That(page.Rows[1]).IsInstanceOf<LabelSpec>();
    }
}
