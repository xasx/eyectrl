namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// T11: the SHIPPED German switch-on — the production manifest and the loose German file that land
/// beside the App exe, not <see cref="GermanDraft"/>'s test manifest (which switches every surface
/// on so T10 could measure German before any surface shipped). <see cref="Load"/> goes through the
/// production loader against the copy in this test's output, so the table built here is exactly
/// the one the App reads on this de-DE machine, System selection and all.
/// </summary>
internal static class ShippedGerman
{
    /// <summary>The seven surfaces Step 1 switches on, in the manifest's sorted order.</summary>
    internal static readonly string[] SwitchedOn =
    [
        "app-tray",
        "caregiver-alert",
        "drift-notice",
        "language-recovery",
        "startup-error",
        "supervisor",
        "track-status"
    ];

    /// <summary>The surfaces that must stay English while Step 1 runs: Steps 2 and 3 are not shipped.</summary>
    internal static readonly string[] StillEnglish =
    [
        "settings",
        "calibration",
        "eye-care",
        "overlays"
    ];

    /// <summary>The machine culture the switch-on is measured on: this PC's de-DE.</summary>
    internal static readonly CultureInfo GermanCulture = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>The shipped manifest in the repo source — the canonical copy the build copies beside the exe.</summary>
    internal static string RepoManifestPath => Path.Combine(
        GermanDraft.RepoRoot(), "src", "EyeCtrl.App", "Localization", StringTable.ManifestFileName);

    /// <summary>
    /// The App's own load: System selection against de-DE, the loose German file and manifest from
    /// <paramref name="directory"/> (this test's output, where the App project's content lands).
    /// </summary>
    internal static StringTable Load(Action<string>? warn = null, string? directory = null) =>
        StringTable.Load(UiLanguage.System, GermanCulture, directory ?? AppContext.BaseDirectory, warn);

    /// <summary>The surface ids the shipped manifest file names, in file order.</summary>
    internal static IReadOnlyList<string> ManifestSurfaces(string? path = null)
    {
        var node = JsonNode.Parse(File.ReadAllText(path ?? RepoManifestPath));
        var result = new List<string>();
        foreach (var item in node?.AsArray() ?? throw new InvalidOperationException($"{path ?? RepoManifestPath} is not a JSON array."))
        {
            result.Add(item?.GetValue<string>()
                ?? throw new InvalidOperationException($"{path ?? RepoManifestPath} holds a non-string surface id."));
        }

        return result;
    }
}
