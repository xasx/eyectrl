namespace EyeCtrl.App.Tests.Settings;

using System.IO;

/// <summary>
/// Source scan for the ordered mode-switch contract (spec §5.6): the filter step in
/// <see cref="EyeCtrl.App.Views.SettingsWindow.RebuildContent"/> and the
/// model → snap → one-sidebar-apply ordering in <c>SwitchSettingsUiMode</c>, which Task 11's
/// header toggle calls. The window stays un-constructible here, so the pins read the source.
/// </summary>
public class SettingsModeSwitchWiringTests
{
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

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public void SwitchSettingsUiMode_RendersTheSidebarOnce_AfterTheSnap()
    {
        var body = ExtractMethod(WindowSource(), "private void SwitchSettingsUiMode(");
        Check.WithCustomMessage(
                "The ordered contract (spec §5.6): model -> snap -> ONE sidebar apply. Applying "
                + "the sidebar before the snap renders an empty sidebar (round-3 I4).")
            .That(body.IndexOf("ShowTab(")).IsStrictlyLessThan(body.IndexOf("ApplySidebarLayout("));
        Check.That(CountOccurrences(body, "RenderTabPage(")).IsEqualTo(0); // inside ApplySidebarLayout, not here
        Check.That(body).Contains("CurrentPage = 0");
        Check.That(body).Contains("ActiveTabId = layout.SnapTo");   // the F6 sidebar-must-not-contradict pin
    }

    [Fact]
    public void SwitchSettingsUiMode_RelabelsAfterTheSidebarApply()
    {
        var body = ExtractMethod(WindowSource(), "private void SwitchSettingsUiMode(");
        Check.WithCustomMessage(
                "The switch must re-label the toggle AFTER applying the sidebar — the label names "
                + "the mode now active (L21).")
            .That(body.IndexOf("ApplySidebarLayout("))
            .IsStrictlyLessThan(body.IndexOf("UpdateModeToggleLabel()"));
    }

    [Fact]
    public void RebuildContent_FiltersPagesInSimpleMode()
    {
        var body = ExtractMethod(WindowSource(), "private void RebuildContent(");
        Check.WithCustomMessage(
                "The simple-mode filter must sit between BuildPages and the height repagination.")
            .That(body).Contains("SimpleSettingsFilter.Apply");
    }

    [Fact]
    public void ApplySidebarLayout_AssignsTheModelsPage_RendersOnce()
    {
        var body = ExtractMethod(WindowSource(), "private void ApplySidebarLayout(");
        Check.WithCustomMessage(
                "The applier must assign the model's post-snap page (a stale _tabPageIndex is "
                + "the empty-sidebar regression, round-3 I4).")
            .That(body).Contains("_tabPageIndex = layout.PageIndex");
        Check.WithCustomMessage("Exactly one RenderTabPage in the applier.")
            .That(CountOccurrences(body, "RenderTabPage(")).IsEqualTo(1);
    }

    [Fact]
    public void RebuildTabPagerChrome_ClearsBeforeItBuilds()
    {
        var body = ExtractMethod(WindowSource(), "private void RebuildTabPagerChrome(");
        Check.WithCustomMessage(
                "BuildTabPagePager only appends — the panel must be cleared first (round-2 I3).")
            .That(body.IndexOf("TabPagerPanel.Children.Clear()"))
            .IsStrictlyLessThan(body.IndexOf("BuildTabPagePager("));
    }
}
