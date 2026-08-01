using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Toolchains;
using BenchmarkDotNet.Toolchains.CsProj;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace EyeCtrl.Benchmarks;

/// <summary>
/// Pins BenchmarkDotNet's CsProj toolchain to <em>this</em> copy of EyeCtrl.Benchmarks.csproj instead of
/// letting it search for one.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. To build the per-benchmark boilerplate project, BDN needs the path of the benchmark
/// project. <c>CsProjGenerator.GetProjectFilePath(Type, ILogger)</c> finds it by walking <em>down</em> from
/// the repository root (the nearest ancestor holding a <c>.sln</c>/<c>.slnx</c>/<c>.git</c>) and collecting
/// every <c>EyeCtrl.Benchmarks.csproj</c> under it. This repo keeps its git worktrees in <c>.worktrees/</c>,
/// i.e. <em>inside</em> that root, and each worktree is a full checkout containing its own copy of the
/// benchmark project. The search therefore returns one hit per worktree plus the real one and BDN aborts
/// with <c>NotSupportedException: Found more than one matching project file ... Benchmark project names
/// needs to be unique.</c> — every benchmark reports NA and no result can be recorded while any worktree
/// exists.
///
/// THE FIX. The path is not actually ambiguous: the assembly currently executing was built from exactly one
/// of those projects, and it sits in that project's <c>bin/</c>. So resolve <em>upwards</em> from the
/// running assembly rather than downwards from the repo root, and override the search away entirely. This
/// is self-locating, not hard-coded — run the benchmarks from inside a worktree and they pin to that
/// worktree's project, which is the copy that was built.
///
/// WHY NOT <c>--inProcess</c>. InProcessEmitToolchain generates no project, so it does sidestep the search,
/// but it runs benchmarks in the host process: no process isolation, the host's JIT/GC state and
/// environment leak into the measurement. Acceptable as a smoke test, not for numbers that get recorded
/// under <c>BenchmarkDotNet.Artifacts/results/</c>. This fix keeps the real CsProj toolchain.
///
/// LIMITATION — SINGLE TARGET FRAMEWORK. The generator's TFM is fixed at construction, and the mutator
/// applies one toolchain to every job, so the whole suite builds as <see cref="TargetFrameworkMoniker"/>.
/// Cross-runtime runs (<c>--runtimes net8.0 net9.0</c>, or a class-level <c>[SimpleJob(RuntimeMoniker.NetXX)]</c>
/// naming another runtime) will still execute — but on net10.0, under a job label naming the runtime that
/// was asked for. This costs nothing today: EyeCtrl.Benchmarks.csproj targets net10.0 only, so a generated
/// project on an older TFM could not reference it and cross-runtime comparison was never available here.
/// It does mean a stale moniker now produces a mislabelled result instead of a hard failure — keep every
/// <c>[SimpleJob(...)]</c> in this project on <see cref="TargetFrameworkMoniker"/>'s runtime. If the project
/// ever multi-targets, this class has to grow a per-runtime toolchain instead of one mutator.
/// </remarks>
internal static class PinnedProjectToolchain
{
    private const string ProjectFileName = "EyeCtrl.Benchmarks.csproj";
    private const string TargetFrameworkMoniker = "net10.0";

    /// <summary>
    /// A mutator job carrying nothing but a toolchain. Mutators are applied on top of every other job
    /// rather than adding a job of their own, so the per-class <c>[SimpleJob(...)]</c> attributes keep their
    /// own settings (invocation counts, params) and only the toolchain is replaced.
    /// </summary>
    /// <param name="inProcess">
    /// Honours an explicit <c>--inProcess</c> on the command line. It has to be routed through the same
    /// mutator rather than left to BDN: on its own, <c>--inProcess</c> adds an in-process job <em>beside</em>
    /// the per-class <c>[SimpleJob(...)]</c> one, and that second job still went through the broken project
    /// search — it failed and was dropped from the summary table, so the run looked clean while half of it
    /// had died. Mutating every job keeps exactly one toolchain in play whichever mode is asked for.
    /// </param>
    public static Job AsMutatorJob(bool inProcess) =>
        Job.Default
            .WithToolchain(inProcess ? InProcessEmitToolchain.Instance : CreatePinned())
            .AsMutator();

    private static IToolchain CreatePinned()
    {
        // Borrow the stock net10.0 builder/executor; only generation needed fixing.
        var stock = CsProjCoreToolchain.NetCoreApp10_0;
        var generator = new PinnedCsProjGenerator(LocateProjectFile(), TargetFrameworkMoniker);
        return new Toolchain($"{TargetFrameworkMoniker}-pinned", generator, stock.Builder, stock.Executor);
    }

    /// <summary>
    /// Walks up from the running assembly's directory to the first one holding <see cref="ProjectFileName"/>.
    /// </summary>
    private static FileInfo LocateProjectFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = new FileInfo(Path.Combine(directory.FullName, ProjectFileName));
            if (candidate.Exists)
                return candidate;

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate {ProjectFileName} in any ancestor of '{AppContext.BaseDirectory}'. " +
            $"The benchmark host expects to run from the project's own build output. " +
            $"As a fallback, '--inProcess' skips project generation entirely (no process isolation).");
    }

    private sealed class PinnedCsProjGenerator : CsProjGenerator
    {
        private readonly FileInfo _projectFile;

        // The three nulls are the stock configuration — CsProjCoreToolchain.NetCoreApp10_0 passes exactly
        // these ("use the ambient dotnet CLI / packages folder / runtime version"). BDN declares the
        // parameters non-nullable anyway, hence null! rather than null.
        internal PinnedCsProjGenerator(FileInfo projectFile, string targetFrameworkMoniker)
            : base(targetFrameworkMoniker, cliPath: null!, packagesPath: null!, runtimeFrameworkVersion: null!)
            => _projectFile = projectFile;

        protected override FileInfo GetProjectFilePath(Type benchmarkTarget, ILogger logger) => _projectFile;
    }
}
