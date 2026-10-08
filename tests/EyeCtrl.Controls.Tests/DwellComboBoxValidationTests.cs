namespace EyeCtrl.Controls.Tests;

using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

public class DwellComboBoxValidationTests
{
    // [StaFact] like its sibling: the length guard happens to run before any WPF object is
    // constructed today, but a plain [Fact] would start failing with a threading exception rather
    // than a clean assertion the moment that ordering changes.
    [StaFact]
    public void Constructor_MismatchedArrayLengths_ThrowsArgumentException()
    {
        Check.ThatCode(() => new DwellComboBox(
                new FakeDwellRegistrar(),
                "prefix",
                "label",
                new[] { "A", "B" },
                new[] { "1" },
                () => "",
                _ => { },
                DwellCategory.Control,
                40,
                800,
                "⚠ {0} (not available)"))
            .Throws<ArgumentException>();
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
            800,
            "⚠ {0} (not available)");

        Check.That(combo).IsNotNull();
    }

}
