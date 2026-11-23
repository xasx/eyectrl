namespace EyeCtrl.Supervisor.Tests;

/// <summary>
/// Every test class that swaps the process-global <c>Serilog.Log.Logger</c> belongs here (today
/// that is <see cref="Supervision.ProcessControllerTests"/>, whose two ForceKill logging tests
/// capture the static logger). While such a swap is installed, ANY parallel test whose product
/// code writes through <c>Serilog.Log.*</c> lands in the capturing sink: the sink's message list
/// then races concurrent writes (the test's OWN line can be the one lost — the 22.11 CI flake,
/// run 45270942880 on PR #387, showed the sink holding another test's
/// <c>"StartOnBoot"=False: the App saved it</c>), and an unrelated line can trip an "any line"
/// assertion. A plain named collection would not help — it only serializes its own members while
/// the rest of the suite keeps running — so this collection uses <c>DisableParallelization</c>,
/// which in xUnit v3 runs alongside nothing at all: no other test can write through the global
/// logger while the swap is in place.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalSerilogLoggerCollection
{
    public const string Name = "Global Serilog logger";
}
