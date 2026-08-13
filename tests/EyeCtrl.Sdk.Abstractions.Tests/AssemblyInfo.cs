using Xunit;

// DwellHead is process-wide static state (the latched pipe name). Distinct test classes run in
// parallel collections by default, so DwellHeadTests' ResetForTests would race any sibling class
// that starts a head — DwellHeadCoreTests.EnsureStarted_IsIdempotent latches without resetting.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
