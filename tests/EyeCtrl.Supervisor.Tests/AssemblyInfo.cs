using Xunit;

// ProcessControllerEnvironmentTests sets EYECTRL_INSTANCE_ID process-wide: ProcessStartInfo
// .Environment is materialised from the current process block on first access, and Launch builds
// its own psi, so there is no seam to inject the variable through. Distinct test classes run in
// parallel collections by default, which would let that set leak into any sibling reading the
// environment.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
