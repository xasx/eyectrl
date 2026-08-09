namespace EyeCtrl.Controls.Tests;

using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

public class DwellComboBoxValidationTests
{
    [Fact]
    public void Constructor_MismatchedArrayLengths_ThrowsArgumentException()
    {
        ArgumentException? ex = null;
        try
        {
            var combo = new DwellComboBox(
                new FakeDwellRegistrar(),
                "prefix",
                "label",
                new[] { "A", "B" },
                new[] { "1" },
                () => "",
                _ => { },
                DwellCategory.Control,
                40,
                800);
        }
        catch (ArgumentException e)
        {
            ex = e;
        }

        Check.That(ex).IsNotNull();
    }

    [StaFact]
    public void Constructor_MatchingArrayLengths_DoesNotThrow()
    {
        var combo = new DwellComboBox(
            new FakeDwellRegistrar(),
            "prefix",
            "label",
            new[] { "A", "B" },
            new[] { "1", "2" },
            () => "",
            _ => { },
            DwellCategory.Control,
            40,
            800);

        Check.That(combo).IsNotNull();
    }

    private class FakeDwellRegistrar : IDwellRegistrar
    {
        public void Register(string id, DwellCategory category, double radius, int dwellMs, Action onActivated) { }
        public void Unregister(string id, DwellCategory category) { }
        public void UpdatePosition(string id, DwellCategory category, Core.Models.PhysicalPoint point) { }
        public double GetProgress(string id, DwellCategory category) => 0;
    }
}
