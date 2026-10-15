namespace EyeCtrl.App.Tests;

using System.IO;

/// <summary>
/// Every dwell surface must be CLASSIFIED for the cue (spec §Testing): cue family / typing
/// family / excluded. Keyed on BOTH mechanisms — every `new DwellDetector(` construction site
/// AND every `DwellContext` enum member (surfaces that hand-roll their dwell, like
/// KeyboardViewModel's key tracker, still declare a context; OverlayTelemetryWiringTests
/// already pins ASSIGNMENT — this census pins the cue CLASSIFICATION). A new surface that is
/// silent AND unclassified fails here by name; the staleness half fails when an allowlist
/// entry stops matching the code (ToolbarButtonDriftTests pattern).
/// </summary>
public class DwellSurfaceCensusTests
{
    // project-rooted path fragment → classification + reason. Mirrors the spec's surface table.
    private static readonly (string Path, string Classification, string Reason)[] DetectorSites =
    [
        ("EyeCtrl.App/Overlays/EmojiGridManager.cs", "cue", "category bar = navigation (D1)"),
        ("EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs", "typing", "prediction bar — click family (D1)"),
        ("EyeCtrl.App/Services/ExternalDwellService.cs", "excluded", "external apps — owner declined"),
        ("EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml.cs", "cue", "start/snooze exercise actions"),
        ("EyeCtrl.App/Overlays/RestartNoticeWindow.xaml.cs", "cue", "restart-notice close (post-effect cue, like the eye-care banner)"),
        ("EyeCtrl.App/Services/NoteManager.cs", "cue", "note button actions"),
        ("EyeCtrl.App/Services/ManualSelectionDwellService.cs", "cue", "manual-selection dwell clicks"),
        ("EyeCtrl.ViewModels/SettingsWindowViewModel.cs", "cue", "tabs/controls/pagination/modal"),
        ("EyeCtrl.ViewModels/DwellOverlayViewModelBase.cs", "cue", "base wrapper — 9 effectful VMs + 2 opt-outs"),
    ];

    // DwellContext members that are not a user-facing surface of their own.
    private static readonly string[] ContextMembersNotSurfaces = ["Other"];

    private static readonly Dictionary<string, string> ContextClassification = new(StringComparer.Ordinal)
    {
        ["Keyboard"] = "typing — keys click (hand-rolled tracker, KeyboardViewModel.cs:111)",
        ["KeyboardPrediction"] = "typing — click family (D1)",
        ["ActionMenu"] = "cue — base wrapper",
        ["ManualSelection"] = "cue — MSDS continuation",
        ["Toolbar"] = "cue — base wrapper",
        ["EdgeButtonBar"] = "cue — base wrapper",
        ["QuickActions"] = "cue — base wrapper",
        ["TaskSwitcher"] = "cue — base wrapper",
        ["Emoji"] = "cue — category bar",
        ["Notes"] = "cue — NoteManager",
        ["Scroll"] = "excluded — empty handler, opted out",
        ["DragTrail"] = "cue — base wrapper",
        ["ActivationButton"] = "cue — base wrapper",
        ["Backdrop"] = "cue — base wrapper",
        ["ExternalDwell"] = "excluded — owner declined",
        ["EyeCare"] = "cue — banner",
    };

    [Fact]
    public void EveryDwellDetectorConstructionSite_IsClassified()
    {
        var found = SourceFilesContaining("new DwellDetector(");
        var classified = DetectorSites.Select(s => s.Path).ToList();
        var unclassified = found.Where(f => !classified.Contains(f, StringComparer.Ordinal)).ToList();

        Check.WithCustomMessage(
            "These files construct a DwellDetector but are not in DwellSurfaceCensusTests' "
            + "allowlist — classify the surface (cue/typing/excluded) or wire its publish: "
            + string.Join(", ", unclassified))
            .That(unclassified).IsEmpty();
    }

    [Fact]
    public void EveryTargetActivatedSubscription_IsClassified()
    {
        // External review G1: the spec's census promised BOTH keys. Scanning only
        // `new DwellDetector(` misses code that subscribes to a detector it did not construct
        // (an existing detector handed in from elsewhere) — that surface would ship silent and
        // unnoticed. Today the subscription set equals the construction set; this fact holds
        // it that way.
        var found = SourceFilesContaining("TargetActivated +=");
        var classified = DetectorSites.Select(s => s.Path).ToList();
        var unclassified = found.Where(f => !classified.Contains(f, StringComparer.Ordinal)).ToList();

        Check.WithCustomMessage(
            "These files subscribe to a DwellDetector's TargetActivated but are not in "
            + "DwellSurfaceCensusTests' allowlist — a subscription without a publish is a "
            + "silently dead cue surface: " + string.Join(", ", unclassified))
            .That(unclassified).IsEmpty();
    }

    private static List<string> SourceFilesContaining(string marker)
    {
        var src = Path.Combine(SourceScanner.RepoRoot().FullName, "src");
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains(marker))
            .Select(f => f[(src.Length + 1)..].Replace('\\', '/'))
            .ToList();
    }

    [Fact]
    public void EveryDwellContextMember_IsClassified()
    {
        var members = Enum.GetNames<EyeCtrl.Core.Adaptation.DwellContext>()
            .Where(m => !ContextMembersNotSurfaces.Contains(m))
            .OrderBy(m => m, StringComparer.Ordinal).ToList();

        var missing = members.Where(m => !ContextClassification.ContainsKey(m)).ToList();
        Check.WithCustomMessage(
            "New DwellContext member(s) without a cue classification: " + string.Join(", ", missing)
            + " — classify in this census (spec §Testing) and wire the surface's publish or opt-out.")
            .That(missing).IsEmpty();
    }

    [Fact]
    public void TheDetectorAllowlist_HasNoStaleEntries()
    {
        var src = Path.Combine(SourceScanner.RepoRoot().FullName, "src");
        foreach (var (path, _, reason) in DetectorSites)
        {
            var full = Path.Combine(src, path.Replace('/', Path.DirectorySeparatorChar));
            Check.WithCustomMessage($"Census entry '{path}' ({reason}) no longer exists on disk.")
                .That(File.Exists(full)).IsTrue();
            Check.WithCustomMessage(
                $"Census entry '{path}' ({reason}) no longer constructs a DwellDetector — "
                + "update the classification or remove the entry.")
                .That(File.ReadAllText(full).Contains("new DwellDetector(")).IsTrue();
        }
    }
}
