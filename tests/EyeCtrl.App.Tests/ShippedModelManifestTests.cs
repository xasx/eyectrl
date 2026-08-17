namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using EyeCtrl.Core.Prediction;
using NFluent;
using Xunit;

/// <summary>
/// Guards the SHIPPED Resources/models.json, not a synthetic fixture — ModelManifestTests already
/// covers deserialization of hand-written JSON, which is exactly why the real manifest was able to
/// rot unnoticed: every entry had an empty sha256 (so ModelDownloadService skipped verification by
/// design, see its integrity-policy remark), ten of its twelve URLs 404'd, and every sizeBytes was
/// a round-number placeholder shown to the user in the download-consent modal.
///
/// The file is read through the same BaseDirectory/Resources/models.json path AppBootstrapper uses,
/// so this fails if the Content copy is ever dropped from the csproj too.
/// </summary>
public class ShippedModelManifestTests
{
    private const string Sha256Hex = "^[0-9a-f]{64}$";

    // HuggingFace revision SHAs are 40-char git hashes. Pinning to one is what makes a content hash
    // meaningful: against a mutable ref like /resolve/main/ the upstream file can change under us
    // and every download would start failing as a hash mismatch.
    private const string PinnedRevisionUrl =
        @"^https://huggingface\.co/[^/]+/[^/]+/resolve/[0-9a-f]{40}/.+$";

    private static Dictionary<string, ModelEntry> ShippedModels()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "models.json");
        Check.That(File.Exists(path)).As($"shipped manifest at {path}").IsTrue();

        var manifest = JsonSerializer.Deserialize<ModelManifest>(File.ReadAllText(path));
        Check.That(manifest).IsNotNull();
        Check.That(manifest!.Models).Not.IsEmpty();
        return manifest.Models;
    }

    [Fact]
    public void EveryEntry_HasWellFormedHashes()
    {
        foreach (var (id, entry) in ShippedModels())
        {
            Check.That(entry.Sha256).As($"{id}.sha256").Matches(Sha256Hex);
            Check.That(entry.TokenizerSha256).As($"{id}.tokenizerSha256").Matches(Sha256Hex);
        }
    }

    [Fact]
    public void EveryUrl_IsPinnedToAnImmutableRevision()
    {
        foreach (var (id, entry) in ShippedModels())
        {
            Check.That(entry.Url).As($"{id}.url").Matches(PinnedRevisionUrl);
            Check.That(entry.TokenizerUrl).As($"{id}.tokenizerUrl").Matches(PinnedRevisionUrl);
        }
    }

    [Fact]
    public void EveryEntry_HasDisplayNameAndPositiveSize()
    {
        foreach (var (id, entry) in ShippedModels())
        {
            Check.That(entry.DisplayName).As($"{id}.displayName").Not.IsNullOrEmpty();
            // Surfaced verbatim in the download-consent modal (ModelsPageSource), so a placeholder
            // here misleads the user at the moment they approve a multi-hundred-MB download.
            Check.That(entry.SizeBytes).As($"{id}.sizeBytes").IsStrictlyGreaterThan(0);
        }
    }
}
