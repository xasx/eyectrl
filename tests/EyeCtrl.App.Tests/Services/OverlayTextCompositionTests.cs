namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Windows.Threading;
using EyeCtrl.App.Localization;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Settings;

/// <summary>
/// T08 (N2/D3): the App composition hands the live grid the <c>overlays</c> surface's keyed preset
/// labels. The resolved value cannot distinguish the two functions while English is on (the English
/// default returns "Copy" too), so this pins the function identity: dropping the composition
/// assignment falls back to <c>QuickActionPresets.EnglishLabelFor</c> and fails here.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class OverlayTextCompositionTests
{
    [Fact]
    public void ResolveViewModels_GivesTheGridTheOverlaysKeyedPresetLabels()
    {
        var appData = TestAppDataDir.Create("t08-overlay-composition");
        using var sp = AppBootstrapper.BuildServiceProvider(
            new EyeCtrlSettings(),
            settingsFilePath: Path.Combine(appData, "settings.json"),
            dispatcher: Dispatcher.CurrentDispatcher,
            isTestMode: true,
            appDataDir: appData);

        var viewModels = AppBootstrapper.ResolveViewModels(sp, new EyeCtrlSettings());

        // A method-group conversion makes a fresh delegate each evaluation, so identity is pinned
        // through the delegate's target method — dropping the assignment leaves EnglishLabelFor.
        var labelFor = viewModels.QuickActionsGrid.PresetLabelFor;
        Check.That(labelFor.Method.DeclaringType).IsEqualTo(typeof(OverlayTextSources));
        Check.That(labelFor.Method.Name).IsEqualTo(nameof(OverlayTextSources.PresetLabelFor));
    }

    /// <summary>
    /// The other half of N2: the modes menu's icon-only names (D11) are composed from the overlays
    /// keys in <c>OverlayManager.Initialize</c>. A real <c>OverlayManager</c> builds ~20 WPF
    /// windows, so this pins the one line the way T03d's R3 pin pins the preview hand-off — without
    /// it the modes names silently stay embedded English and never translate.
    /// </summary>
    [Fact]
    public void TheShell_ComposesTheOverlaysKeyedLabelsOntoTheModesMenu()
    {
        var source = File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Services", "OverlayManager.cs"));

        // Comments stripped, like every source scan here: a commented-out line is not a hand-off.
        Check.WithCustomMessage(
                "OverlayManager must compose the overlays-keyed labels onto the modes menu (T08/N2); "
                + "otherwise its D11 names never follow the overlays surface.")
             .That(SourceScanner.StripComments(source)
                 .Contains("_modesMenu.Labels = RingLabelSource.Overlays")).IsTrue();
    }
}
