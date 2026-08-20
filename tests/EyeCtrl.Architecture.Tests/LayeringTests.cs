namespace EyeCtrl.Architecture.Tests;

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

/// <summary>
/// Encodes the architectural layering invariants from <c>ARCHITECTURE.md</c> (the dependency-
/// direction rules in §1 and the leaf / isolation invariants #8–#11 of §7) as CI gates, so a
/// layering regression fails the build instead of being caught only by review.
/// </summary>
/// <remarks>
/// <para>
/// The suite uses TWO complementary mechanisms, because they catch different mistakes:
/// </para>
/// <list type="number">
/// <item><term>Declaration gate (csproj)</term><description> parses every <c>src/*.csproj</c>'s
/// <c>&lt;ProjectReference&gt;</c> list and asserts it matches the documented dependency graph. This
/// catches an ill-advised reference the moment it is <i>declared</i> — even before any code uses it.
/// It is needed because Roslyn elides the <c>AssemblyRef</c> for an <i>unused</c> reference, so the
/// IL-level check below is a lagging gate for a declared-but-unused edge.</description></item>
/// <item><term>Usage gate (IL)</term><description> reads each compiled assembly's <c>AssemblyRef</c>
/// table via <see cref="MetadataReader"/> and asserts which <c>EyeCtrl.*</c> assemblies it actually
/// couples to. This is stricter than the csproj pass in one dimension: it sees transitive types that
/// are <i>used</i> (e.g. <c>Sdk.Wpf</c> uses <c>Contracts</c> types directly even though its csproj
/// only lists <c>Sdk.Abstractions</c>), so it is what enforces "ViewModels never reaches into
/// Platform" at the level that matters — real coupling, not just declarations. It reads PE metadata
/// with zero side effects (no assembly loading, no WPF/WinUI activation, no module initialisers), so
/// it is headless-safe and runs in the normal <c>Category!=UI</c> CI job.</description></item>
/// </list>
/// <para>
/// <c>EyeCtrl.Sdk.WinUI</c> is intentionally NOT project-referenced by this test project (pulling it
/// in would bring WinUI 3 / <c>WindowsAppSDK</c> AppxPackage build tooling). It is covered by the
/// declaration gate like every other project; it is not covered by the usage gate because its DLL is
/// absent from the test bin.
/// </para>
/// </remarks>
public class LayeringTests
{
    private const string EyeCtrlPrefix = "EyeCtrl.";

    /// <summary>Assembly simple name → the set of EyeCtrl.* assemblies it directly couples to (IL AssemblyRef).</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> UsageReferences =
        BuildUsageReferenceMap(AppContext.BaseDirectory);

    // ─── Declaration gate: the csproj ProjectReference graph ─────────────

    [Fact]
    public void ProjectReference_Declaration_Graph_Matches_The_Documented_Layering()
    {
        // The declaration gate. Catches a bad reference the moment it is DECLARED, even if no code
        // uses it yet (Roslyn elides the AssemblyRef for an unused reference, so the usage gate below
        // would not fire until a type is actually touched).
        var expected = ExpectedDeclarationReferences();
        var srcDir = Path.Combine(FindRepoRoot(), "src");

        var actualByProject = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var csproj in Directory.EnumerateFiles(srcDir, "*.csproj", SearchOption.AllDirectories)
                     // Exclude obj//bin so a future toolchain that drops a build-generated csproj under
                     // obj/ (e.g. WPF's _wpftmp) can't trip the gate spuriously.
                     .Where(p => { var n = p.Replace('\\', '/'); return !n.Contains("/obj/", StringComparison.OrdinalIgnoreCase) && !n.Contains("/bin/", StringComparison.OrdinalIgnoreCase); }))
            actualByProject[Path.GetFileNameWithoutExtension(csproj)] = ReadProjectReferenceNames(csproj);

        var discrepancies = new List<string>();

        foreach (var p in expected.Keys.Except(actualByProject.Keys, StringComparer.OrdinalIgnoreCase))
            discrepancies.Add($"expected csproj not found under src/: {p}");
        foreach (var p in actualByProject.Keys.Except(expected.Keys, StringComparer.OrdinalIgnoreCase))
            discrepancies.Add($"unexpected csproj under src/ (add to {nameof(ExpectedDeclarationReferences)}): {p}");

        foreach (var (project, allowed) in expected)
        {
            if (!actualByProject.TryGetValue(project, out var actual)) continue;
            var allowedSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
            foreach (var extra in actual.Except(allowedSet, StringComparer.OrdinalIgnoreCase).OrderBy(s => s))
                discrepancies.Add($"{project}: unexpected ProjectReference → {extra}");
            foreach (var missing in allowedSet.Except(actual, StringComparer.OrdinalIgnoreCase).OrderBy(s => s))
                discrepancies.Add($"{project}: missing expected ProjectReference → {missing}");
        }

        Check.WithCustomMessage(
            "The csproj ProjectReference graph must match the documented layering:\n" +
            (discrepancies.Count > 0 ? string.Join("\n", discrepancies) : "(none)"))
            .That(discrepancies).IsEmpty();
    }

    /// <summary>The expected <c>&lt;ProjectReference&gt;</c> set per src project — the documented csproj graph.</summary>
    private static IReadOnlyDictionary<string, IReadOnlySet<string>> ExpectedDeclarationReferences() =>
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["EyeCtrl.Core"] = Set(),
            ["EyeCtrl.Contracts"] = Set(),
            ["EyeCtrl.ViewModels"] = Set("EyeCtrl.Core"),
            ["EyeCtrl.Platform"] = Set("EyeCtrl.Core"),
            ["EyeCtrl.Controls"] = Set("EyeCtrl.Core"),
            ["EyeCtrl.EyeCare"] = Set("EyeCtrl.Core"),
            ["EyeCtrl.LM"] = Set("EyeCtrl.Core"),
            ["EyeCtrl.Supervisor"] = Set("EyeCtrl.Core", "EyeCtrl.Platform", "EyeCtrl.Controls"),
            ["EyeCtrl.Sdk.Abstractions"] = Set("EyeCtrl.Contracts"),
            ["EyeCtrl.Sdk.Wpf"] = Set("EyeCtrl.Sdk.Abstractions"),
            ["EyeCtrl.Sdk.WinUI"] = Set("EyeCtrl.Sdk.Abstractions"),
            ["EyeCtrl.App"] = Set("EyeCtrl.Core", "EyeCtrl.Platform", "EyeCtrl.ViewModels",
                "EyeCtrl.Contracts", "EyeCtrl.EyeCare", "EyeCtrl.Controls", "EyeCtrl.LM"),
        };

    private static IReadOnlySet<string> Set(params string[] items) =>
        new HashSet<string>(items, StringComparer.OrdinalIgnoreCase);

    // ─── Usage gate (IL AssemblyRef): real coupling, including transitive types used ──

    [Fact]
    public void Core_References_No_Other_EyeCtrl_Assembly()
    {
        // Core is the framework-agnostic foundation (ARCHITECTURE.md §2). It must not couple to any
        // other EyeCtrl project — every other project depends on it.
        ExpectUsageRefs("EyeCtrl.Core", Array.Empty<string>());
    }

    [Fact]
    public void Contracts_References_No_Other_EyeCtrl_Assembly()
    {
        // Contracts is the zero-dependency wire format shared by App (server) and the SDK (client)
        // so the eyectrl_dwell pipe name/protocol can never drift (invariant #11).
        ExpectUsageRefs("EyeCtrl.Contracts", Array.Empty<string>());
    }

    [Fact]
    public void ViewModels_References_Only_Core()
    {
        // ViewModels sits above Core and MUST NOT reach into Platform/App (ARCHITECTURE.md §1,
        // dependency-direction prose). A Platform edge would couple the MVVM layer to Win32/Tobii
        // and break headless unit testing of the VMs.
        ExpectUsageRefs("EyeCtrl.ViewModels", new[] { "EyeCtrl.Core" });
    }

    [Fact]
    public void Platform_References_Only_Core()
    {
        // Platform (Win32 SendInput / Tobii) depends on Core domain types only — never on
        // ViewModels or App. It is the OS/SDK bridge, not a composition layer.
        ExpectUsageRefs("EyeCtrl.Platform", new[] { "EyeCtrl.Core" });
    }

    [Fact]
    public void Controls_References_Only_Core()
    {
        ExpectUsageRefs("EyeCtrl.Controls", new[] { "EyeCtrl.Core" });
    }

    [Fact]
    public void EyeCare_References_Only_Core()
    {
        // EyeCare is a leaf subscriber (invariant #9): it observes gaze samples from Core and must
        // not reach back into App/Platform. Core never references EyeCare — App composes it.
        ExpectUsageRefs("EyeCtrl.EyeCare", new[] { "EyeCtrl.Core" });
    }

    [Fact]
    public void LM_References_Only_Core()
    {
        // LM is the ONNX leaf behind the ILanguageModelPredictor seam (invariant #8). Core never
        // references LM — App wires the concrete SmallLMPredictor in at startup.
        ExpectUsageRefs("EyeCtrl.LM", new[] { "EyeCtrl.Core" });
    }

    [Fact]
    public void Supervisor_Couples_To_Core_Platform_Controls_But_Not_App()
    {
        // Invariant #10: the Supervisor is a SEPARATE process. It must never couple to the
        // application it guards — a crash in App must not be able to take out the watchdog that
        // relaunches it. (Usage gate is the meaningful one here: it catches an actual type reference,
        // not just a declared ProjectReference.)
        var refs = UsageRefsOf("EyeCtrl.Supervisor");
        Check.That(refs.Contains("EyeCtrl.App")).IsFalse();
        ExpectUsageRefs("EyeCtrl.Supervisor",
            new[] { "EyeCtrl.Core", "EyeCtrl.Platform", "EyeCtrl.Controls" });
    }

    [Fact]
    public void App_Does_Not_Couple_To_Supervisor_Or_The_Sdk_Chain()
    {
        // App is the composition root and the eyectrl_dwell pipe SERVER; it must not couple to its
        // own watchdog (Supervisor) nor its own third-party client SDK chain. App is intentionally
        // open-ended WITHIN the app domain (a new app-domain project may legitimately be referenced),
        // so this is a negative boundary check, not an exact set.
        var refs = UsageRefsOf("EyeCtrl.App");
        var forbidden = new[] { "EyeCtrl.Supervisor", "EyeCtrl.Sdk.Abstractions", "EyeCtrl.Sdk.Wpf", "EyeCtrl.Sdk.WinUI" };
        var leakage = refs.Intersect(forbidden, StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();
        Check.WithCustomMessage(
            "App must not couple to its own watchdog or its own client SDK chain. Leakage: " +
            $"[{string.Join(", ", leakage)}].")
            .That(leakage).IsEmpty();
    }

    [Fact]
    public void Sdk_Abstractions_References_Only_Contracts()
    {
        ExpectUsageRefs("EyeCtrl.Sdk.Abstractions", new[] { "EyeCtrl.Contracts" });
    }

    [Fact]
    public void Sdk_Wpf_Couples_Only_To_Abstractions_And_Contracts()
    {
        // Sdk.Wpf directly uses both Sdk.Abstractions (DwellClient / IDwellElementAdapter) and
        // Contracts (SdkRect / WireMessages it builds to send over the pipe). Both live inside the
        // isolated SDK chain. The invariant is that it NEVER reaches into the app domain (Core,
        // Platform, ViewModels, App, Supervisor, EyeCare, LM, Controls). Asserted as an exact set so
        // a future EyeCtrl.* project added to the forbidden side also fails the gate.
        ExpectUsageRefs("EyeCtrl.Sdk.Wpf", new[] { "EyeCtrl.Sdk.Abstractions", "EyeCtrl.Contracts" });
    }

    [Fact]
    public void Sdk_WinUI_Couples_Only_To_Abstractions_And_Contracts()
    {
        // Sdk.WinUI is NOT referenced by this test project (it would pull WinUI 3 / AppxPackage
        // build tooling), so its DLL is absent from the test bin and cannot be read via the
        // UsageReferences map. Locate the assembly the solution built — CI builds the full solution
        // before testing (`dotnet build EyeCtrl.slnx` then `--no-build`), and a local full build does
        // too — and read its IL AssemblyRef table directly, giving WinUI the same usage-level coverage
        // WPF gets. If the DLL is absent, FAIL with guidance rather than pass vacuously; CI never hits
        // this (it always builds first), it only triggers on a cold single-project test run.
        // (WinUiElementAdapter uses both Contracts and Sdk.Abstractions types, so its direct IL set
        // must be {Contracts, Sdk.Abstractions}, matching WPF.)
        var binDir = Path.Combine(FindRepoRoot(), "src", "EyeCtrl.Sdk.WinUI", "bin");
        // bin/ is absent entirely on a cold worktree, and EnumerateFiles throws
        // DirectoryNotFoundException in that case — before FirstOrDefault could yield null, so
        // the guidance below never rendered and the test crashed instead of failing.
        var dll = Directory.Exists(binDir)
            ? Directory.EnumerateFiles(binDir, "EyeCtrl.Sdk.WinUI.dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        Check.WithCustomMessage(
            "EyeCtrl.Sdk.WinUI.dll not found under src/EyeCtrl.Sdk.WinUI/bin — run " +
            "`dotnet build EyeCtrl.slnx` first so the WinUI usage gate has an assembly to read.")
            .That(dll is null ? new[] { "missing Sdk.WinUI assembly" } : Array.Empty<string>()).IsEmpty();

        AssertUsageRefs("EyeCtrl.Sdk.WinUI (IL)",
            new[] { "EyeCtrl.Sdk.Abstractions", "EyeCtrl.Contracts" },
            ReadEyeCtrlReferences(dll!));
    }

    // ─── Guard: every assembly the usage gate asserts on was actually discovered ───

    [Fact]
    public void All_Expected_EyeCtrl_Assemblies_Are_Present_In_The_Test_Bin()
    {
        // If a project's DLL stops reaching the test bin, the usage tests above would silently pass
        // against a missing entry. This turns a missing assembly into a loud, named failure.
        string[] expected =
        {
            "EyeCtrl.Core", "EyeCtrl.Platform", "EyeCtrl.ViewModels", "EyeCtrl.App",
            "EyeCtrl.Controls", "EyeCtrl.EyeCare", "EyeCtrl.LM", "EyeCtrl.Supervisor",
            "EyeCtrl.Contracts", "EyeCtrl.Sdk.Abstractions", "EyeCtrl.Sdk.Wpf",
        };
        var missing = expected.Where(n => !UsageReferences.ContainsKey(n)).OrderBy(s => s).ToList();
        Check.WithCustomMessage(
            "These assemblies were not found in the test bin (a transitive likely stopped being " +
            $"copied); the usage gate would silently pass against each: [{string.Join(", ", missing)}].")
            .That(missing).IsEmpty();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static IReadOnlySet<string> UsageRefsOf(string assemblyName)
    {
        Check.WithCustomMessage(
            $"Assembly '{assemblyName}' was not found in the test bin — has its project reference been removed?")
            .That(UsageReferences.ContainsKey(assemblyName)).IsTrue();
        return UsageReferences[assemblyName];
    }

    private static void ExpectUsageRefs(string assemblyName, string[] allowed) =>
        AssertUsageRefs(assemblyName, allowed, UsageRefsOf(assemblyName));

    /// <summary>Asserts the assembly couples to EXACTLY the allowed EyeCtrl.* set — exact both ways.
    /// A NEW forbidden coupling fails (extra); a REMOVED legitimate coupling also fails (missing)
    /// until you update the allowed set in the caller. That edit is the intended flow when a refactor
    /// legitimately changes which assembly a type is used from — update the expectation here, don't
    /// weaken the check.</summary>
    private static void AssertUsageRefs(string label, string[] allowed, IReadOnlySet<string> actual)
    {
        var allowedSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);

        var extra = actual.Except(allowedSet, StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();
        var missing = allowedSet.Except(actual, StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();

        Check.WithCustomMessage(
            $"'{label}' must couple to exactly [{string.Join(", ", allowed)}] among EyeCtrl.*, " +
            $"but coupled to [{string.Join(", ", actual.OrderBy(s => s))}]. Unexpected: [{string.Join(", ", extra)}].")
            .That(extra).IsEmpty();

        Check.WithCustomMessage(
            $"'{label}' was expected to also couple to [{string.Join(", ", missing)}] but did not.")
            .That(missing).IsEmpty();
    }

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> BuildUsageReferenceMap(string binDir)
    {
        var map = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dll in Directory.EnumerateFiles(binDir, "EyeCtrl.*.dll")
                     .Where(p => !Path.GetFileNameWithoutExtension(p)
                         .EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)))
        {
            map[Path.GetFileNameWithoutExtension(dll)] = ReadEyeCtrlReferences(dll);
        }
        return map;
    }

    private static IReadOnlySet<string> ReadEyeCtrlReferences(string dllPath)
    {
        using var pe = new PEReader(File.OpenRead(dllPath));
        var reader = pe.GetMetadataReader();
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var handle in reader.AssemblyReferences)
        {
            var ar = reader.GetAssemblyReference(handle);
            var refName = reader.GetString(ar.Name);
            if (refName.StartsWith(EyeCtrlPrefix, StringComparison.Ordinal))
                refs.Add(refName);
        }
        return refs;
    }

    /// <summary>The EyeCtrl.* target project names declared as <c>&lt;ProjectReference&gt;</c> in a csproj.</summary>
    private static IReadOnlySet<string> ReadProjectReferenceNames(string csprojPath)
    {
        var doc = XDocument.Load(csprojPath);
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(include => Path.GetFileNameWithoutExtension(include!))
            .Where(n => n.StartsWith(EyeCtrlPrefix, StringComparison.Ordinal))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate the repository root (EyeCtrl.slnx) above " + AppContext.BaseDirectory);
    }
}
