namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;

/// <summary>
/// Every describable row (slider / toggle / combo / colour picker) on every covered tab carries a
/// Description — a settings row that ships bare fails here. QuickActions is exempt BY EXPLICIT
/// OMISSION, named here: its pages are hand-chunked and height-gated (HandChunkedPageFitTests), so
/// its rows deliberately carry no descriptions. The exemption is NOT expressed via
/// PrefersAutoPagination — that property defaults to false in the interface, so testing it would
/// silently excuse any future source that merely forgets to opt in. Models is built with a
/// downloaded model because an empty registry builds no describable row at all (md_active is gated
/// on SelectableModels being non-empty) — the empty build would cover nothing.
/// </summary>
public class SettingsRowDescriptionCompletenessTests
{
    [Fact]
    public void EveryDescribableRow_OnEveryCoveredSource_HasADescription()
    {
        var ctx = TestSettingsPageContext.Build();
        // Mirrors SettingsWindow._tabDefs order (QuickActions omitted, see the class doc).
        // HAND-MAINTAINED: unlike EyeCtrlSettingsEnumerationTests (reflection-based), this list
        // does NOT fail automatically when a new page source is added — a new source must be
        // added here consciously or it ships uncovered. A reflection-based drift guard over
        // ISettingsPageSource implementors is a known follow-up (OverlayInventoryDriftTests is
        // the in-repo pattern).
        ISettingsPageSource[] sources =
        [
            new CursorPageSource(ctx),
            new DwellPageSource(ctx),
            new SelectionPageSource(ctx),
            new LayoutPageSource(ctx),
            new ActionMenuPageSource(ctx),
            new KeyboardPageSource(ctx),
            new ToolbarPageSource(ctx),
            new CalibrationPageSource(ctx),
            new GeneralPageSource(ctx),
            new EyeCarePageSource(ctx),
            new AdaptationPageSource(ctx),
        ];

        var bare = sources.SelectMany(BareRows).ToList();

        using var scope = ModelTestFakes.Create();
        var (registry, mgmt) = scope.WithDownloadedBundled();
        var models = new ModelsPageSource(
            TestSettingsPageContext.Build(registry: registry, modelManagement: mgmt));
        bare.AddRange(BareRows(models));

        Check.That(bare).IsEmpty();
    }

    private static IEnumerable<string> BareRows(ISettingsPageSource source) =>
        source.Build().SelectMany(p => p.Rows)
            .Where(r => r is SliderSpec or ToggleSpec or ComboBoxSpec or ColorPickerSpec)
            .Where(r => string.IsNullOrWhiteSpace(r.Description))
            .Select(r => $"{source.GetType().Name}:{r.Id}");
}
