namespace EyeCtrl.Architecture.Tests;

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

/// <summary>
/// Encodes the architectural layering invariants documented in <c>ARCHITECTURE.md §7</c> as
/// CI-gated tests. Each invariant ("Core never references Platform", "Supervisor never references
/// App", "the SDK chain is isolated from Core/App", …) is asserted against the real
/// assembly-reference graph read directly from the compiled assemblies — so a future ill-advised
/// <c>&lt;ProjectReference&gt;</c> addition fails the build instead of silently violating the
/// layering that is today enforced only by convention.
/// </summary>
/// <remarks>
/// <para>
/// References are read via <see cref="MetadataReader"/> rather than <c>Assembly.Load</c>: this
/// reads the PE metadata with zero side effects (no WPF/WinUI activation, no module initialisers),
/// keeping the test fast and headless-safe — it runs in the normal <c>Category!=UI</c> CI job.
/// </para>
/// <para>
/// <c>EyeCtrl.Sdk.WinUI</c> is intentionally NOT project-referenced by the test project (pulling it
/// in would bring the WinUI 3 / <c>WindowsAppSDK</c> AppxPackage build tooling). Its invariant is
/// verified by parsing its <c>.csproj</c>, whose <c>&lt;ProjectReference&gt;</c> list is the sole
/// way this project acquires EyeCtrl dependencies.
/// </para>
/// </remarks>
public class LayeringTests
{
    private const string EyeCtrlPrefix = "EyeCtrl.";

    /// <summary>
    /// Assembly simple name → the set of <c>EyeCtrl.*</c> assemblies it directly references, read
    /// once from the compiled DLLs in the test output directory.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> References =
        BuildReferenceMap(AppContext.BaseDirectory);

    // ─── Foundation: zero-dependency leaves ──────────────────────────────

    [Fact]
    public void Core_References_No_Other_EyeCtrl_Assembly()
    {
        // Core is the framework-agnostic foundation (ARCHITECTURE.md §2). It must not depend on any
        // other EyeCtrl project — every other project depends on it. A reverse edge here would be a
        // cycle in the dependency graph.
        ExpectRefs("EyeCtrl.Core", Array.Empty<string>());
    }

    [Fact]
    public void Contracts_References_No_Other_EyeCtrl_Assembly()
    {
        // Contracts is the zero-dependency wire format shared by App (server) and the SDK (client)
        // so the eyectrl_dwell pipe name/protocol can never drift (invariant #11). It must stay
        // dependency-free so net8.0 third-party consumers never pull in net10 Core.
        ExpectRefs("EyeCtrl.Contracts", Array.Empty<string>());
    }

    // ─── Core dependents: exactly one allowed dependency ─────────────────

    [Fact]
    public void ViewModels_References_Only_Core()
    {
        // ViewModels sits above Core and MUST NOT reach into Platform/App (invariant: "ViewModels
        // depends only on Core, never Platform"). A Platform edge would couple the MVVM layer to
        // Win32/Tobii and break headless unit testing of the VMs.
        ExpectRefs("EyeCtrl.ViewModels", new[] { "EyeCtrl.Core" });
    }

    [Fact]
    public void Platform_References_Only_Core()
    {
        // Platform (Win32 SendInput / Tobii) depends on Core domain types only — never on
        // ViewModels or App. It is the OS/SDK bridge, not a composition layer.
        ExpectRefs("EyeCtrl.Platform", new[] { "EyeCtrl.Core" });
    }

    [Fact]
    public void Controls_References_Only_Core()
    {
        ExpectRefs("EyeCtrl.Controls", new[] { "EyeCtrl.Core" });
    }

    [Fact]
    public void EyeCare_References_Only_Core()
    {
        // EyeCare is a leaf subscriber (invariant #9): it observes gaze samples from Core and must
        // not reach back into App/Platform. Core never references EyeCare — App composes it.
        ExpectRefs("EyeCtrl.EyeCare", new[] { "EyeCtrl.Core" });
    }

    [Fact]
    public void LM_References_Only_Core()
    {
        // LM is the ONNX leaf behind the ILanguageModelPredictor seam (invariant #8). Core never
        // references LM — App wires the concrete SmallLMPredictor in at startup, keeping Core free
        // of native ML dependencies.
        ExpectRefs("EyeCtrl.LM", new[] { "EyeCtrl.Core" });
    }

    // ─── The watchdog: may use Core/Platform/Controls, never App ─────────

    [Fact]
    public void Supervisor_References_Core_Platform_Controls_But_Not_App()
    {
        // Invariant #10: the Supervisor is a SEPARATE process sharing only Contracts/Core types.
        // It must never reference the application it guards — a crash in App must not be able to
        // take out the watchdog that is supposed to relaunch it.
        ExpectRefs("EyeCtrl.Supervisor",
            new[] { "EyeCtrl.Core", "EyeCtrl.Platform", "EyeCtrl.Controls" });
    }

    // ─── Composition root: App must not depend on its guard or its client SDK ─

    [Fact]
    public void App_Does_Not_Reference_Supervisor_Or_The_Sdk_Chain()
    {
        // App is the composition root and may reference most projects — but it must NOT reference
        // the Supervisor (the process that guards App) nor the third-party SDK chain. App is the
        // eyectrl_dwell pipe SERVER; the SDK is the CLIENT consumed by external apps. An edge from
        // App to its own guard or its own client SDK is a layering smell.
        var refs = RefsOf("EyeCtrl.App");
        Check.That(refs.Contains("EyeCtrl.Supervisor")).IsFalse();
        Check.That(refs.Contains("EyeCtrl.Sdk.Abstractions")).IsFalse();
        Check.That(refs.Contains("EyeCtrl.Sdk.Wpf")).IsFalse();
        Check.That(refs.Contains("EyeCtrl.Sdk.WinUI")).IsFalse();
    }

    // ─── SDK chain: fully isolated from Core and App ─────────────────────

    [Fact]
    public void Sdk_Abstractions_References_Only_Contracts()
    {
        // The framework-agnostic SDK surface depends only on the wire-format contracts — never on
        // Core (no domain coupling for third-party consumers) and never on App.
        ExpectRefs("EyeCtrl.Sdk.Abstractions", new[] { "EyeCtrl.Contracts" });
    }

    [Fact]
    public void Sdk_Wpf_Stays_Within_The_Sdk_Chain_And_Out_Of_The_App_Domain()
    {
        // Sdk.Wpf is a WPF attached-property adapter. It may use both Sdk.Abstractions (DwellClient /
        // IDwellElementAdapter) and Contracts (SdkRect / WireMessages it builds to send over the
        // pipe) — both live inside the isolated SDK chain, and Sdk.Wpf sits on top of both. The
        // invariant is that it NEVER reaches into the app's domain: no Core, Platform, ViewModels,
        // App, Supervisor, EyeCare, LM, or Controls. (A direct Contracts edge is expected here even
        // though the csproj only lists Sdk.Abstractions, because the compiler emits an IL reference
        // to any assembly whose types are used.)
        var refs = RefsOf("EyeCtrl.Sdk.Wpf");
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "EyeCtrl.Core", "EyeCtrl.Platform", "EyeCtrl.ViewModels", "EyeCtrl.App",
            "EyeCtrl.Supervisor", "EyeCtrl.EyeCare", "EyeCtrl.LM", "EyeCtrl.Controls",
        };
        var leakage = refs.Intersect(forbidden, StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();
        Check.WithCustomMessage(
            $"Sdk.Wpf must stay within the SDK chain (Abstractions/Contracts) but reached into the " +
            $"app domain: [{string.Join(", ", leakage)}].")
            .That(leakage).IsEmpty();
    }

    [Fact]
    public void Sdk_WinUI_Stays_Within_The_Sdk_Chain_And_Out_Of_The_App_Domain()
    {
        // Sdk.WinUI is NOT project-referenced here (it would drag in WinUI 3 / AppxPackage build
        // tooling). Assert isolation by parsing its csproj — ProjectReference is the only way this
        // project acquires EyeCtrl dependencies, so its single ProjectReference must be Sdk.Abstractions
        // and nothing from Core/App/Platform/etc.
        var projectRefs = ReadProjectReferences(Path.Combine(FindRepoRoot(),
            "src", "EyeCtrl.Sdk.WinUI", "EyeCtrl.Sdk.WinUI.csproj"));

        var eyectrlRefs = projectRefs
            .Where(r => r.Contains("EyeCtrl.", StringComparison.OrdinalIgnoreCase))
            .ToList();
        // Exactly one EyeCtrl dependency: Sdk.Abstractions. (Microsoft.WindowsAppSDK is a
        // PackageReference, not a ProjectReference, so it does not appear here.)
        Check.WithCustomMessage(
            $"Sdk.WinUI must reference only Sdk.Abstractions among EyeCtrl projects, but had: [{string.Join(", ", eyectrlRefs)}].")
            .That(eyectrlRefs.Count).IsEqualTo(1);
        Check.That(eyectrlRefs[0].EndsWith("EyeCtrl.Sdk.Abstractions.csproj",
            StringComparison.OrdinalIgnoreCase)).IsTrue();
    }

    // ─── Guard: every assembly we assert on was actually discovered ───────

    [Fact]
    public void All_Expected_EyeCtrl_Assemblies_Are_Present_In_The_Test_Bin()
    {
        // If a project's DLL fails to reach the test bin (e.g. a transitive that stopped being
        // copied), the per-project tests above would silently pass against a stale/missing entry.
        // This guard turns "missing assembly" into a loud, named failure.
        string[] expected =
        {
            "EyeCtrl.Core", "EyeCtrl.Platform", "EyeCtrl.ViewModels", "EyeCtrl.App",
            "EyeCtrl.Controls", "EyeCtrl.EyeCare", "EyeCtrl.LM", "EyeCtrl.Supervisor",
            "EyeCtrl.Contracts", "EyeCtrl.Sdk.Abstractions", "EyeCtrl.Sdk.Wpf",
        };
        foreach (var name in expected)
        {
            Check.That(References.ContainsKey(name))
                .IsTrue();
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static IReadOnlySet<string> RefsOf(string assemblyName)
    {
        Check.That(References.ContainsKey(assemblyName)).IsTrue();
        return References[assemblyName];
    }

    private static void ExpectRefs(string assemblyName, string[] allowed)
    {
        var actual = RefsOf(assemblyName);
        var allowedSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);

        var extra = actual.Except(allowedSet, StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();
        var missing = allowedSet.Except(actual, StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();

        Check.WithCustomMessage(
            $"'{assemblyName}' must reference exactly [{string.Join(", ", allowed)}] among EyeCtrl.*, " +
            $"but referenced [{string.Join(", ", actual.OrderBy(s => s))}]. Unexpected: [{string.Join(", ", extra)}].")
            .That(extra).IsEmpty();

        Check.WithCustomMessage(
            $"'{assemblyName}' was expected to also reference [{string.Join(", ", missing)}] but did not.")
            .That(missing).IsEmpty();
    }

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> BuildReferenceMap(string binDir)
    {
        var map = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dll in Directory.EnumerateFiles(binDir, "EyeCtrl.*.dll")
                     .Where(p => !Path.GetFileNameWithoutExtension(p)
                         .EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileNameWithoutExtension(dll);
            map[name] = ReadEyeCtrlReferences(dll);
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

    private static IReadOnlyList<string> ReadProjectReferences(string csprojPath)
    {
        var doc = XDocument.Load(csprojPath);
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
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
