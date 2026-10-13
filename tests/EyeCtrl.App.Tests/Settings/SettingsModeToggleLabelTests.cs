namespace EyeCtrl.App.Tests.Settings;

using System.IO;
using EyeCtrl.App.Views;

/// <summary>
/// The mode toggle's label mapping (owner ruling, ledger L21): the label names the mode you
/// switch TO, not the one you are in — "All settings" while simple, "Basic" while advanced. The
/// window cannot be constructed here (eight non-optional dependencies), so the mapping is tested
/// on the pure helper and the re-label's routing is pinned on the source, the same technique as
/// <see cref="SettingsModeSwitchWiringTests"/>.
/// </summary>
public class SettingsModeToggleLabelTests
{
    [Fact]
    public void LabelKey_NamesTheModeYouSwitchTo()
    {
        // Owner ruling L21: "All settings" while IN simple mode (it offers the full mode);
        // "Basic" while IN advanced. The label names the TARGET.
        Check.That(SettingsWindow.ModeToggleLabelKey(simple: true)).IsEqualTo("settings.mode.show.all");
        Check.That(SettingsWindow.ModeToggleLabelKey(simple: false)).IsEqualTo("settings.mode.show.basic");
    }

    [Fact]
    public void UpdateModeToggleLabel_RoutesThroughTheKeyHelper()
    {
        var label = ExtractMethod(WindowSource(), "private void UpdateModeToggleLabel(");
        Check.WithCustomMessage("The re-label must route through ModeToggleLabelKey — the one tested mapping.")
            .That(label).Contains("ModeToggleLabelKey(");
    }

    private static string WindowSource() => File.ReadAllText(Path.Combine(
        SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Views", "SettingsWindow.xaml.cs"));

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Check.WithCustomMessage($"SettingsWindow.xaml.cs no longer declares `{signature}`. "
                + "Repoint this scan at wherever it moved; do not delete it.")
            .That(start).IsStrictlyGreaterThan(-1);
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Check.WithCustomMessage($"The `{signature}` body is unterminated.")
            .That(end).IsStrictlyGreaterThan(start);
        return source[start..end];
    }
}
