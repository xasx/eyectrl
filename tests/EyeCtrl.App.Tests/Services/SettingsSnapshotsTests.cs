namespace EyeCtrl.App.Tests.Services;

using System.Linq;
using System.Reflection;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Settings;

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
            nameof(KeyboardSettings.EmojiSuggestionsEnabled),
            nameof(KeyboardSettings.ShowLMLoadingIndicator),
            nameof(KeyboardSettings.CustomModels),               // managed by ModelRegistryService
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
        var before = KeyboardStackSnapshot.Capture(s);
        s.Cursor.CircleOpacity = 0.42;
        s.Toolbar.Size = 96;
        var after = KeyboardStackSnapshot.Capture(s);
        Check.That(after).IsEqualTo(before);
    }

    [Fact]
    public void KeyboardStackSnapshot_PredictionRelevantChange_SnapshotsDiffer()
    {
        var s = new EyeCtrlSettings();
        var before = KeyboardStackSnapshot.Capture(s);
        s.Keyboard.MLModelSize = "1.7B";
        Check.That(KeyboardStackSnapshot.Capture(s)).IsNotEqualTo(before);

        before = KeyboardStackSnapshot.Capture(s);
        s.Dwell.KeyboardKeyDwellMs += 25;
        Check.That(KeyboardStackSnapshot.Capture(s)).IsNotEqualTo(before);
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
    }
}
