namespace EyeCtrl.App.Tests.Startup;

using System.IO;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Fails the build when a new process-external side effect appears without being instance-scoped.
/// <para>
/// This exists because hand enumeration of this surface failed four times out of four while people
/// were actively looking — including once on a resource the same document had already written the
/// correct fix for. The guard is the mechanism being trusted instead of care.
/// </para>
/// <para>
/// Syntax nodes, not regex: a textual scan matches the extensive pipe commentary in
/// <c>ExternalDwellService</c> and the tray commentary in <c>SupervisorTrayIcon</c> while missing
/// anything reached through a wrapper.
/// </para>
/// </summary>
public class InstanceScopeDriftTests
{
    /// <summary>Construction and member-access spellings that create process-external state.</summary>
    private static readonly string[] WatchedTypes =
    [
        "Mutex", "EventWaitHandle", "Semaphore",
        "NamedPipeServerStream", "NamedPipeClientStream",
        "NotifyIcon", "MemoryMappedFile"
    ];

    private static readonly string[] WatchedMembers =
    [
        "Registry.CurrentUser", "Registry.LocalMachine", "Registry.ClassesRoot",
        "EventWaitHandle.OpenExisting", "Mutex.OpenExisting",
        "SpecialFolder.ApplicationData",
        // The supervisor's heartbeat server binds via the ACL factory, not `new
        // NamedPipeServerStream` (HeartbeatPipeServer.cs). Omitting this is why an earlier draft
        // of this guard could not see the very resource v1's enumeration missed.
        "NamedPipeServerStreamAcl.Create"
    ];

    /// <summary>
    /// Every known site, keyed by "<c>RelativePath::Symbol</c>", with the reason it is safe. Entries
    /// for refused resources point at the GATE, not merely at the call site — the distinction
    /// matters because an allow-list entry that just names a residual legitimises it.
    /// </summary>
    private static readonly Dictionary<string, string> AllowList = new()
    {
        [@"src\EyeCtrl.Core\Services\SingleInstanceGuard.cs::Mutex"] =
            "Scoped: name injected by the composition root, reported via EffectiveName.",
        [@"src\EyeCtrl.Core\Settings\SettingsService.cs::Mutex"] =
            "Scoped: writerMutexName injected, reported via EffectiveWriterMutexName.",
        [@"src\EyeCtrl.App\App.xaml.cs::SpecialFolder.ApplicationData"] =
            "The composition root's single %APPDATA% read, in InitializeInstanceAndLogging. "
            + "AppPaths takes roamingAppData as a PARAMETER — that is what keeps the resolver pure "
            + "and testable — so this is where it has to be read. Every other site delegates here.",
        [@"src\EyeCtrl.App\Services\ExternalDwellService.cs::NamedPipeServerStream"] =
            "Scoped: pipeName injected, reported via EffectivePipeName.",
        [@"src\EyeCtrl.App\Services\HeartbeatPublisher.cs::NamedPipeClientStream"] =
            "REFUSED, not scoped. Gate: supervision forced off => IsPublishing false, and "
            + "SignalShuttingDown guards its send. See the spec's 'heartbeat exit path'.",
        [@"src\EyeCtrl.App\Services\RestartHandoff.cs::EventWaitHandle.OpenExisting"] =
            "REFUSED, not scoped. Gate: phase 1 rejects --restart-handoff when scoped.",
        [@"src\EyeCtrl.App\Services\SelfRestartStrategy.cs::EventWaitHandle"] =
            "REFUSED, not scoped. Gate: Begin returns RefusedScoped before creating the event.",
        [@"src\EyeCtrl.App\Services\TrayIconManager.cs::NotifyIcon"] =
            "Unnamed per-process shell icon: no name to scope, no collision. Cosmetic only.",
        [@"src\EyeCtrl.Platform\Services\WindowsStartupRegistrar.cs::Registry.CurrentUser"] =
            "Scoped: valueName injected, reported via EffectiveValueName.",
        // NOTE the two entries that deliberately do NOT appear here. The supervisor singleton is
        // `new SingleInstanceGuard(SupervisorMutexName)` (Program.cs), which this guard attributes
        // to SingleInstanceGuard.cs::Mutex — a `Program.cs::Mutex` key would never resolve and
        // EveryAllowListEntryStillResolves would fail. Same for the heartbeat server, which uses the
        // ACL factory rather than `new NamedPipeServerStream`; it is keyed below by that factory.
        [@"src\EyeCtrl.Supervisor\Ipc\HeartbeatPipeServer.cs::NamedPipeServerStreamAcl.Create"] =
            "Separate process, out of scope: no scoped instance launches a supervisor.",
        [@"src\EyeCtrl.Supervisor\Program.cs::SpecialFolder.ApplicationData"] =
            "Separate process, out of scope (same reason).",
        [@"src\EyeCtrl.Supervisor\UI\SupervisorTrayIcon.cs::NotifyIcon"] =
            "Separate process; unnamed per-process icon.",
        [@"src\EyeCtrl.Supervisor\Fallback\FallbackAtLauncher.cs::Registry.LocalMachine"] =
            "Read-only HKLM probe to discover installed assistive technology. Writes nothing.",
        [@"src\EyeCtrl.Sdk.Abstractions\DwellClient.cs::NamedPipeClientStream"] =
            "Named by the host via DwellHead.PipeName, latched at first use.",
    };

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "src")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException("Could not locate the repo root.");
    }

    private static IEnumerable<string> SourceFiles(string root)
    {
        // The spec says "the SDK", which is three assemblies, not one.
        foreach (var project in new[] { "EyeCtrl.App", "EyeCtrl.Core", "EyeCtrl.Platform",
                                        "EyeCtrl.ViewModels", "EyeCtrl.Supervisor",
                                        "EyeCtrl.Sdk.Abstractions", "EyeCtrl.Sdk.Wpf",
                                        "EyeCtrl.Sdk.WinUI", "EyeCtrl.Controls",
                                        "EyeCtrl.EyeCare", "EyeCtrl.LM", "EyeCtrl.Contracts" })
        {
            var dir = Path.Combine(root, "src", project);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                // Generated and build output are not hand-written side effects.
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                yield return file;
            }
        }
    }

    private static List<(string Key, string File, int Line)> FindSites(string root)
    {
        var found = new List<(string, string, int)>();

        foreach (var file in SourceFiles(root))
        {
            var text = File.ReadAllText(file);
            var tree = CSharpSyntaxTree.ParseText(text);
            var relative = Path.GetRelativePath(root, file);

            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                string? symbol = node switch
                {
                    // `new Mutex(...)`, `new NamedPipeServerStream(...)`
                    ObjectCreationExpressionSyntax oc
                        when WatchedTypes.Contains(TypeName(oc.Type)) => TypeName(oc.Type),

                    // `Registry.CurrentUser`, `EventWaitHandle.OpenExisting`, `SpecialFolder.ApplicationData`
                    MemberAccessExpressionSyntax ma
                        when WatchedMembers.Contains(Flatten(ma)) => Flatten(ma),

                    _ => null
                };

                if (symbol == null) continue;

                var line = tree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
                found.Add(($"{relative}::{symbol}", relative, line));
            }
        }

        return found;
    }

    private static string TypeName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        QualifiedNameSyntax q => q.Right.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        _ => type.ToString()
    };

    private static string Flatten(MemberAccessExpressionSyntax ma)
    {
        var left = ma.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax inner => inner.Name.Identifier.Text,
            _ => ma.Expression.ToString()
        };
        return $"{left}.{ma.Name.Identifier.Text}";
    }

    [Fact]
    public void EveryProcessExternalSiteIsAllowListed()
    {
        var root = RepoRoot();
        var unexpected = FindSites(root)
            .Where(s => !AllowList.ContainsKey(s.Key))
            .Select(s => $"{s.File}:{s.Line} -> {s.Key}")
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        // Joined into one string so a failure prints every offending site. NFluent's collection
        // message would say "has 3 items" without naming them, which is useless here.
        Check.That(string.Join(Environment.NewLine, unexpected)).IsEqualTo(string.Empty);
    }

    [Fact]
    public void EveryAllowListEntryStillResolves()
    {
        // A stale exemption must fail rather than silently widen the allowed surface.
        var root = RepoRoot();
        var actual = FindSites(root).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

        var stale = AllowList.Keys.Where(k => !actual.Contains(k)).OrderBy(x => x).ToList();

        Check.That(string.Join(Environment.NewLine, stale)).IsEqualTo(string.Empty);
    }

    [Fact]
    public void AppPathsIdRuleMatchesInstanceScope()
    {
        // AppPaths duplicates the id validation because Core cannot reference Contracts. If the two
        // drift, a path could accept an id a pipe name rejects.
        // Non-blank candidates only. Blank means "no id" to both Resolve and Scope, but TryParseId
        // answers a different question — "is this a valid id" — and returns false for it. Including
        // "" would compare two things that are deliberately not the same.
        foreach (var candidate in new[] { "ok", "ok-1", "ok_1", "bad id", "bad\\id", "bad/id", "bad.id" })
        {
            var contractsAccepts = EyeCtrl.Contracts.InstanceScope.TryParseId(candidate, out _);

            bool appPathsAccepts;
            try
            {
                EyeCtrl.Core.Services.AppPaths.Resolve(null, @"C:\r", candidate);
                appPathsAccepts = true;
            }
            catch (ArgumentException) { appPathsAccepts = false; }

            Check.That(appPathsAccepts).IsEqualTo(contractsAccepts);
        }
    }

    [Fact]
    public void AppPathsIdLengthLimitMatchesInstanceScope()
    {
        // The length cap is duplicated as a bare `64` in AppPaths. Covered separately from the
        // character rule because a drift here is invisible to the candidates above.
        var atLimit = new string('a', EyeCtrl.Contracts.InstanceScope.MaxIdLength);
        var overLimit = new string('a', EyeCtrl.Contracts.InstanceScope.MaxIdLength + 1);

        EyeCtrl.Core.Services.AppPaths.Resolve(null, @"C:\r", atLimit);   // must not throw

        Check.ThatCode(() => EyeCtrl.Core.Services.AppPaths.Resolve(null, @"C:\r", overLimit))
             .Throws<ArgumentException>();
    }
}
