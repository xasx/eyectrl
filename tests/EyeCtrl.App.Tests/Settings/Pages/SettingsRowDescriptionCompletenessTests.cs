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
///
/// <para>The covered list is still hand-maintained, but it is no longer hand-POLICED:
/// <see cref="EveryPageSourceInTheApp_IsEitherCovered_OrExplicitlyExempt"/> reflects over the
/// shipping assembly and fails when an <c>ISettingsPageSource</c> appears in neither the covered
/// list nor the named-exemption map. That closes the weakness this doc used to describe as a
/// follow-up — a weakness that had by then let two sources ship unchecked.</para>
/// </summary>
public class SettingsRowDescriptionCompletenessTests
{
    [Fact]
    public void EveryDescribableRow_OnEveryCoveredSource_HasADescription()
    {
        var bare = SimpleSources().SelectMany(BareRows).ToList();

        using var scope = ModelTestFakes.Create();
        var (registry, mgmt) = scope.WithDownloadedBundled();
        var models = new ModelsPageSource(
            TestSettingsPageContext.Build(registry: registry, modelManagement: mgmt));
        bare.AddRange(BareRows(models));

        Check.That(bare).IsEmpty();
    }

    /// <summary>
    /// Every page source that a plain <see cref="TestSettingsPageContext"/> can build, in
    /// <c>SettingsWindow._tabDefs</c> order. Models is not here: it needs a registry holding a
    /// downloaded model or it builds no describable row at all.
    /// </summary>
    private static ISettingsPageSource[] SimpleSources()
    {
        var ctx = TestSettingsPageContext.Build();
        return
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
            new AdvancedPageSource(ctx),
            new DiagnosticsPageSource(ctx),
        ];
    }

    /// <summary>
    /// Sources deliberately outside the list above, each with the reason it is out.
    /// An entry here is a decision; an absence from both lists is an accident.
    /// </summary>
    private static readonly Dictionary<string, string> NotInTheSimpleList = new(StringComparer.Ordinal)
    {
        ["QuickActionsPageSource"] =
            "exempt from the description rule entirely: its pages are hand-chunked and height-gated "
            + "(HandChunkedPageFitTests), so its rows deliberately carry no descriptions",
        ["ModelsPageSource"] =
            "covered, but built separately below with a downloaded model — an empty registry builds "
            + "no describable row (md_active is gated on SelectableModels being non-empty)",
    };

    [Fact]
    public void EveryPageSourceInTheApp_IsEitherCovered_OrExplicitlyExempt()
    {
        // The list above is hand-maintained, and this test is what stops that from being a silent
        // hole. It has already fired twice in practice: a source added to SettingsWindow but not to
        // the list ships with no description coverage at all, and nothing fails — the very shape
        // this file's own comment described as a known weakness, left as a follow-up.
        //
        // Reflection over the shipping assembly, so the question asked is "what does the app
        // actually have" rather than "what did someone remember to write down". Pattern:
        // OverlayInventoryDriftTests.
        var declared = typeof(ISettingsPageSource).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => typeof(ISettingsPageSource).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

        var covered = SimpleSources().Select(s => s.GetType().Name).ToHashSet(StringComparer.Ordinal);

        var unaccounted = declared
            .Where(name => !covered.Contains(name))
            .Where(name => !NotInTheSimpleList.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Check.WithCustomMessage(
                "These ISettingsPageSource implementors are in neither the covered list nor the "
                + "explicit-exemption list: " + string.Join(", ", unaccounted)
                + ". Add each to SimpleSources() so its rows are description-checked, or to "
                + "NotInTheSimpleList with the reason it is out.")
            .That(unaccounted).IsEmpty();

        // And the reverse: an exemption for a source that no longer exists is stale bookkeeping that
        // would quietly excuse a future type that happens to reuse the name.
        var phantom = NotInTheSimpleList.Keys
            .Where(name => !declared.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Check.WithCustomMessage(
                "These exemptions name types that no longer exist: " + string.Join(", ", phantom))
            .That(phantom).IsEmpty();
    }

    private static IEnumerable<string> BareRows(ISettingsPageSource source) =>
        source.Build().SelectMany(p => p.Rows)
            .Where(r => r is SliderSpec or ToggleSpec or ComboBoxSpec or ColorPickerSpec)
            .Where(r => string.IsNullOrWhiteSpace(r.Description))
            .Select(r => $"{source.GetType().Name}:{r.Id}");
}
