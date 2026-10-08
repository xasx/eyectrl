using Xunit;

// The tests that activate StringTable.Current need an exclusive xUnit phase: global activation
// while another collection reads the table would make their text assertion depend on scheduling.
// Writers use an injected table instance instead wherever they can.
[CollectionDefinition("StringTable Tests (Controls)", DisableParallelization = true)]
public class StringTableTestCollection
{
}
