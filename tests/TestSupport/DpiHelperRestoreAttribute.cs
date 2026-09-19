namespace EyeCtrl.Tests.TestSupport;

using System.Reflection;
using EyeCtrl.Core.Services;
using Xunit.v3;

/// <summary>
/// Restores <see cref="DpiHelper"/>'s process-global scale to 1.0 — the value its own field
/// initializer starts at — after EVERY test in whatever assembly links this file. Declared at
/// assembly level so no test can change what a later test reads, whether it writes the global
/// directly or through production code.
///
/// <para>
/// This replaces a per-class restore obligation that failed. <c>DisplayGeometryWatcherTests</c>
/// drives the production <c>DisplayGeometryWatcher</c>, whose <c>Apply</c> calls
/// <c>DpiHelper.Update(2.0, 2.0)</c> for this user's real display geometry; five of its tests left
/// the global at 2.0 and the single test that restored did so as a bare trailing statement an
/// assertion failure skips. At 2.0 the emoji category tab's half-diagonal dwell radius grows
/// 49.5 → 99.0, crosses the 80 px gap to <c>emoji_0</c>, and the tab's activation rebuilds the
/// grid and empties the registry on exactly the sample the cell would have completed — the
/// activation flake this hook closes.
/// </para>
///
/// <para>
/// Why a hook rather than an <c>IDisposable</c> on the leaking class: the leak was possible
/// because restoring was a per-class obligation and most writers in one class forgot it. An
/// assembly-level hook cannot be forgotten by a class, and it also reaches writers a source scan
/// cannot classify because they call the global through production code.
/// </para>
///
/// <para>
/// Why force 1.0 rather than snapshot and restore. DpiHelper's default is 1.0 by its own field
/// initializer, and this hook exists to guarantee every test starts from the value the product
/// ships with. A test that needs another scale sets it itself, in its constructor or its method,
/// and is unaffected: the reset runs only after the test returns. Snapshotting would instead
/// restore whatever a constructor wrote, which lets a constructor's scale leak onward; and
/// snapshot/restore across concurrent tests needs per-test instance state that cannot be assumed
/// of an attribute instance.
/// </para>
///
/// <para>
/// It cannot throw — a fault here would land on every test in the assembly rather than on one.
/// xUnit v3 invokes assembly-level <see cref="BeforeAfterTestAttribute"/>s for every test, which
/// <c>NonParallelCollectionPin</c> in EyeCtrl.App.Tests already relied on before this hook existed
/// (verified there against xunit.v3 3.2.2 under the VSTest runner CI uses). <c>After</c> runs on
/// test failure too, which matters here: the original leak escaped on precisely the runs where an
/// assertion above the restore threw.
/// </para>
///
/// <para>
/// LINKED into every test project that holds a DpiHelper writer, with one
/// <c>[assembly: DpiHelperRestore]</c> per project, not copied: a second copy is a second rule.
/// Which projects must declare it is pinned by
/// <c>DpiHelperWriterIsolationTests.EveryWriterBearingProject_InstallsTheRestoreHook</c>, and the
/// declaration's presence in the compiled assembly by
/// <c>DpiHelperWriterIsolationTests.TheRestoreHook_IsInstalledOnThisAssembly</c>.
/// </para>
/// </summary>
public sealed class DpiHelperRestoreAttribute : BeforeAfterTestAttribute
{
    /// <inheritdoc/>
    public override void After(MethodInfo methodUnderTest, IXunitTest test) =>
        DpiHelper.Initialize(1.0, 1.0);
}
