namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Linq;
using System.Reflection;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;

public class SettingsSnapshotsTests
{
    [Fact]
    public void KeyboardStackSnapshot_CoversEveryKeyboardSetting_OrExplicitlyExcludes()
    {
        // Drift guard: every KeyboardSettings property must be either captured by the snapshot (so
        // changing it rebuilds the prediction/keyboard stack) or explicitly listed as intentionally
        // excluded (read live at use, or managed by a separate flow). A newly-added property lands
        // in neither set and fails this test, forcing a conscious decision.
        var captured = new HashSet<string>
        {
            nameof(KeyboardSettings.ActiveLayout),
            nameof(KeyboardSettings.KeySizeMultiplier),
            nameof(KeyboardSettings.PredictionEnabled),
            nameof(KeyboardSettings.LanguagePack),
            nameof(KeyboardSettings.ScreenEdge),
            nameof(KeyboardSettings.EmojiSkinTone),
            nameof(KeyboardSettings.ClickSoundEnabled),
            nameof(KeyboardSettings.DecayEnabled),
            nameof(KeyboardSettings.DecayHalfLifeDays),
            nameof(KeyboardSettings.MLSuggestionsEnabled),
            nameof(KeyboardSettings.MLModelSize),
            nameof(KeyboardSettings.PreferGPU),
        };
        var intentionallyExcluded = new HashSet<string>
        {
            nameof(KeyboardSettings.ShowPredictionConfidence),   // read live in KeyboardWindow
            nameof(KeyboardSettings.ShowPredictionTypes),        // read live in KeyboardWindow
            nameof(KeyboardSettings.SmartCapitalizationEnabled), // read live in KeyboardViewModel
            nameof(KeyboardSettings.NextWordPredictionEnabled),
            nameof(KeyboardSettings.ShowLMLoadingIndicator),  // read live at render time (prediction loading hint) — toggling it must not rebuild the stack
            nameof(KeyboardSettings.CustomModels),               // managed by ModelRegistryService
            nameof(KeyboardSettings.MaxSuggestions),             // read live in KeyboardViewModel per keystroke
        };

        var all = typeof(KeyboardSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Select(p => p.Name)
            .ToHashSet();

        var accountedFor = new HashSet<string>(captured);
        accountedFor.UnionWith(intentionallyExcluded);
        var unaccounted = all.Except(accountedFor).OrderBy(n => n).ToList();

        Check.WithCustomMessage(
                $"KeyboardSettings not captured by KeyboardStackSnapshot nor intentionally excluded: {string.Join(", ", unaccounted)}")
            .That(unaccounted).IsEmpty();
    }

    [Fact]
    public void KeyboardStackSnapshot_UnrelatedSettingChange_SnapshotsAreEqual()
    {
        var s = new EyeCtrlSettings();
        // A real, on-disk active model (planted AFTER the registry was constructed) so the disk
        // term is TRUE on both captures: an equality over two false terms could not detect a
        // phantom diff in the term.
        var registry = NewRegistry(TempTestDirectory.Create("snapshots-unrelated"), s);
        PutActiveModelOnDisk(registry);

        var before = KeyboardStackSnapshot.Capture(s, registry);

        s.Cursor.CircleOpacity = 0.42;
        s.Toolbar.Size = 96;

        var after = KeyboardStackSnapshot.Capture(s, registry);
        Check.That(after).IsEqualTo(before);
    }

    [Fact]
    public void KeyboardStackSnapshot_PredictionRelevantChange_SnapshotsDiffer()
    {
        var s = new EyeCtrlSettings();
        var registry = NewRegistry(TempTestDirectory.Create("snapshots-relevant"), s);

        var before = KeyboardStackSnapshot.Capture(s, registry);
        s.Keyboard.MLModelSize = "1.7B";
        Check.That(KeyboardStackSnapshot.Capture(s, registry)).IsNotEqualTo(before);

        before = KeyboardStackSnapshot.Capture(s, registry);
        s.Dwell.KeyboardKeyDwellMs += 25;
        Check.That(KeyboardStackSnapshot.Capture(s, registry)).IsNotEqualTo(before);
    }

    // Ticket 11 (the defect): a download of the model MLModelSize ALREADY names changes no
    // setting, so a settings-only snapshot stays equal and ReloadIfChanged suppresses the
    // rebuild — the LM stays off until restart. The disk term is what makes the transition
    // visible. Pinned at the Capture level, where ReloadIfChanged's Context early-out cannot
    // make the assertion vacuously true.
    [Fact]
    public void KeyboardStackSnapshot_ActiveModelBecomingLoadable_MovesTheSnapshot_WithoutASettingChanging()
    {
        var s = new EyeCtrlSettings();                       // MLModelSize defaults to "360M"
        var registry = NewRegistry(TempTestDirectory.Create("snapshots-loadable"), s);

        var before = KeyboardStackSnapshot.Capture(s, registry);
        Check.That(before.ActiveModelOnDisk).IsFalse();

        // Negative control in the same run: with nothing changed, two captures are equal — the
        // determinism Capture must have for the stored-versus-fresh comparison to mean anything.
        // It does NOT pin the reload guard: mutating ShouldReload to always-true leaves this test
        // green and reddens KeyboardCoordinatorTests.ShouldReload_... instead. This comment
        // claimed the opposite until that mutation proved it false; do not restore the claim.
        Check.That(KeyboardStackSnapshot.Capture(s, registry)).IsEqualTo(before);

        // The transition: the files appear. Planted AFTER the registry exists and without touching
        // DownloadedModels/SelectableModels, so the cached entry.IsDownloaded is still false — a
        // term built on that flag would stay false and fail everything below.
        PutActiveModelOnDisk(registry);
        Check.That(registry.Registry["360M"].IsDownloaded).IsFalse();

        var after = KeyboardStackSnapshot.Capture(s, registry);
        Check.That(after.ActiveModelOnDisk).IsTrue();
        Check.WithCustomMessage(
                "Downloading the model MLModelSize already names changes no setting; the only " +
                "field that may differ is the disk term — every setting must be identical. If " +
                "the snapshot does not move, ReloadIfChanged suppresses the rebuild and the LM " +
                "stays off until restart — the defect.")
            .That(after with { ActiveModelOnDisk = false }).IsEqualTo(before);
    }

    /// <summary>A registry on <paramref name="dir"/> whose manifest lists the active model id
    /// (360M by default), with NO files on disk at construction.</summary>
    private static ModelRegistryService NewRegistry(string dir, EyeCtrlSettings settings, string modelId = "360M") =>
        new(dir, new ModelManifest
        {
            Models =
            {
                [modelId] = new ModelEntry
                {
                    DisplayName = modelId,
                    Url = "https://example.invalid/model.onnx",
                    TokenizerUrl = "https://example.invalid/tokenizer.json",
                },
            },
        }, settings);

    /// <summary>Plants both files for <paramref name="modelId"/> through the registry's own paths.
    /// Callers use this AFTER the registry exists, which is what makes it a discriminator against
    /// the stale <c>entry.IsDownloaded</c> flag.</summary>
    private static void PutActiveModelOnDisk(ModelRegistryService registry, string modelId = "360M")
    {
        File.WriteAllText(registry.GetModelPath(modelId), "onnx");
        File.WriteAllText(registry.GetTokenizerPath(modelId), "{}");
    }

    [Fact]
    public void ToolbarSnapshot_OnlyToolbarFieldsMatter()
    {
        var s = new EyeCtrlSettings();
        var before = ToolbarSnapshot.Capture(s);
        s.Keyboard.KeySizeMultiplier = 1.5;
        Check.That(ToolbarSnapshot.Capture(s)).IsEqualTo(before);

        s.Toolbar.DockedEdge = "Left";
        Check.That(ToolbarSnapshot.Capture(s)).IsNotEqualTo(before);

        before = ToolbarSnapshot.Capture(s);
        s.Dwell.ToolbarButtonDwellMs += 50;
        Check.That(ToolbarSnapshot.Capture(s)).IsNotEqualTo(before);

        before = ToolbarSnapshot.Capture(s);
        s.Toolbar.IsDocked = !s.Toolbar.IsDocked;
        Check.That(ToolbarSnapshot.Capture(s)).IsNotEqualTo(before);

        before = ToolbarSnapshot.Capture(s);
        s.Toolbar.Size = 96;
        Check.That(ToolbarSnapshot.Capture(s)).IsNotEqualTo(before);
    }

    [Fact]
    public void KeyboardStackSnapshot_MaxSuggestionsChange_SnapshotsAreEqual()
    {
        // Executable half of the census exclusion: MaxSuggestions must stay OUT of the snapshot
        // (read live per keystroke — no dictionary reload / orchestrator rebuild on a count
        // change). The census set alone cannot detect someone ADDING it to Capture, because the
        // census only unions the two name sets; this can. If this ever fails, either remove the
        // property from KeyboardStackSnapshot or move it to `captured` and accept the rebuild.
        // Real registry (no on-disk model — both captures share it, so its terms cannot differ).
        var s = new EyeCtrlSettings();
        var registry = NewRegistry(TempTestDirectory.Create("snapshots-max-suggestions"), s);
        var before = KeyboardStackSnapshot.Capture(s, registry);
        s.Keyboard.MaxSuggestions = 2;
        Check.That(KeyboardStackSnapshot.Capture(s, registry)).IsEqualTo(before);
    }
}
