namespace EyeCtrl.Core.Tests.Models;

using EyeCtrl.Core.Models;
using Xunit;

public class QuickActionTests
{
    [Fact]
    public void Constructor_AssignsUniqueId()
    {
        var a = new QuickAction();
        var b = new QuickAction();
        Check.That(a.Id).IsNotNull();
        Check.That(a.Id).IsNotEmpty();
        Check.That(a.Id).Not.IsEqualTo(b.Id);
    }

    [Fact]
    public void Constructor_SetsDefaults()
    {
        var qa = new QuickAction();
        Check.That(qa.Label).IsEqualTo(string.Empty);
        Check.That(qa.Payload).IsEqualTo(string.Empty);
        Check.That(qa.Enabled).IsTrue();
    }

    [Fact]
    public void Properties_AreSettable()
    {
        var qa = new QuickAction
        {
            Type = QuickActionType.KeyboardShortcut,
            Label = "Copy",
            Payload = "Ctrl+C"
        };
        Check.That(qa.Type).IsEqualTo(QuickActionType.KeyboardShortcut);
        Check.That(qa.Label).IsEqualTo("Copy");
        Check.That(qa.Payload).IsEqualTo("Ctrl+C");
    }

    [Fact]
    public void QuickActionType_AllValues_AreDistinct()
    {
        var values = Enum.GetValues<QuickActionType>();
        Check.That(values).HasSize(6);
        Check.That(values.Distinct().Count()).IsEqualTo(values.Length);
    }
}
