using Xunit;

// The native Tokenizers.DotNet library is not safe to construct concurrently from multiple
// threads. xUnit runs distinct test classes in parallel collections by default, so LMTokenizerTests
// and SmallLMPredictorTests (both build native tokenizers) would race, producing nondeterministic
// JSON-parse corruption and a native access-violation crash of the test host. These tests run in
// ~60ms, so serializing the whole assembly costs nothing.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
