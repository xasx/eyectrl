namespace EyeCtrl.App.Tests.Settings;

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// T04's deep link (D9): the App tray's one flat "Sprache / Language" item opens Settings on
/// General page 1 with the language row in view. The decision is
/// <see cref="SettingsDeepLink.Decide"/> — pure and unit-tested here; the queue and the wire-up
/// are pinned below.
/// </summary>
public class SettingsDeepLinkDecisionTests
{
    [Fact]
    public void ClosedWindow_DefaultRequest_JustOpens()
    {
        Check.That(SettingsDeepLink.Decide(windowOpen: false, SettingsTarget.Default))
            .IsEqualTo(SettingsDeepLinkAction.OpenOnly);
    }

    [Fact]
    public void ClosedWindow_GeneralPage1Request_OpensThenNavigates()
    {
        // The tray's 0-dwell reach depends on this arm (kills M6).
        Check.That(SettingsDeepLink.Decide(windowOpen: false, SettingsTarget.GeneralPage1))
            .IsEqualTo(SettingsDeepLinkAction.OpenThenNavigate);
    }

    [Fact]
    public void OpenWindow_DefaultRequest_DoesNothing()
    {
        // The toolbar's Settings button must never yank an open window anywhere (kills M5).
        Check.That(SettingsDeepLink.Decide(windowOpen: true, SettingsTarget.Default))
            .IsEqualTo(SettingsDeepLinkAction.None);
    }

    [Fact]
    public void OpenWindow_GeneralPage1Request_Navigates()
    {
        Check.That(SettingsDeepLink.Decide(windowOpen: true, SettingsTarget.GeneralPage1))
            .IsEqualTo(SettingsDeepLinkAction.NavigateOpen);
    }
}

/// <summary>
/// The dispatch plan and its execution: the whole effect of each action, as data plus two
/// mechanical `if`s. Because the coordinator has no branch of its own, every mutation of an arm
/// (None navigating, true/false swapped, a guard negated) lands here and is killed.
/// </summary>
public class SettingsDispatchPlanTests
{
    [Fact]
    public void None_OpensNothing_AndNavigatesNothing()
    {
        Check.That(SettingsDispatchPlan.For(SettingsDeepLinkAction.None))
            .IsEqualTo(new SettingsDispatchPlan(OpenWindow: false, Navigate: false));
    }

    [Fact]
    public void OpenOnly_OpensTheWindow_AndDoesNotNavigate()
    {
        Check.That(SettingsDispatchPlan.For(SettingsDeepLinkAction.OpenOnly))
            .IsEqualTo(new SettingsDispatchPlan(OpenWindow: true, Navigate: false));
    }

    [Fact]
    public void OpenThenNavigate_OpensThenNavigates()
    {
        Check.That(SettingsDispatchPlan.For(SettingsDeepLinkAction.OpenThenNavigate))
            .IsEqualTo(new SettingsDispatchPlan(OpenWindow: true, Navigate: true));
    }

    [Fact]
    public void NavigateOpen_NavigatesOnly()
    {
        Check.That(SettingsDispatchPlan.For(SettingsDeepLinkAction.NavigateOpen))
            .IsEqualTo(new SettingsDispatchPlan(OpenWindow: false, Navigate: true));
    }

    [Theory]
    [InlineData((int)SettingsDeepLinkAction.None, false, false)]
    [InlineData((int)SettingsDeepLinkAction.OpenOnly, true, false)]
    [InlineData((int)SettingsDeepLinkAction.OpenThenNavigate, true, true)]
    [InlineData((int)SettingsDeepLinkAction.NavigateOpen, false, true)]
    public void Execute_RunsExactlyThePlan_InOrder(
        int action, bool expectOpen, bool expectNavigate)
    {
        var calls = new List<string>();
        SettingsDispatchPlan.Execute(
            SettingsDispatchPlan.For((SettingsDeepLinkAction)action),
            openWindow: () => calls.Add("open"),
            navigate: () => calls.Add("navigate"));

        var expected = new List<string>();
        if (expectOpen) expected.Add("open");
        if (expectNavigate) expected.Add("navigate");
        Check.That(calls).ContainsExactly(expected);
    }

    [Fact]
    public void Dispatch_ComposesDecisionAndPlan()
    {
        var calls = new List<string>();
        SettingsDeepLink.Dispatch(
            windowOpen: false,
            target: SettingsTarget.GeneralPage1,
            openWindow: () => calls.Add("open"),
            navigate: () => calls.Add("navigate"));

        Check.That(calls).ContainsExactly("open", "navigate");
    }
}

/// <summary>One-slot pending navigation behind a modal (unchanged from the original fixup input).</summary>
public class SettingsDeepLinkQueueTests
{
    [Fact]
    public void WithNoModal_TheRequest_AppliesImmediately()
    {
        var queue = new SettingsDeepLinkQueue();

        Check.That(queue.Request(modalVisible: false)).IsTrue();
        Check.That(queue.IsPending).IsFalse();
        Check.That(queue.TryApply(modalVisible: false)).IsFalse();   // already applied: no re-run
    }

    [Fact]
    public void WithAModal_TheRequest_QueuesAndAppliesOnceWhenTheModalCloses()
    {
        var queue = new SettingsDeepLinkQueue();

        Check.That(queue.Request(modalVisible: true)).IsFalse();
        Check.That(queue.IsPending).IsTrue();
        Check.That(queue.TryApply(modalVisible: true)).IsFalse();    // modal still up: stay queued
        Check.That(queue.IsPending).IsTrue();
        Check.That(queue.TryApply(modalVisible: false)).IsTrue();    // modal closed: apply
        Check.That(queue.IsPending).IsFalse();
        Check.That(queue.TryApply(modalVisible: false)).IsFalse();   // consumed exactly once
    }
}

/// <summary>
/// The wire-up the pure decision cannot reach: the coordinator dispatches through it, the window
/// queues and lands the whole row, the notices resolve in the target language, and the tray item
/// carries the label and the target. Source pins, in the same spirit as the settings chunker's
/// wiring guards — the real window needs a display and the coordinator its full context.
/// </summary>
public class SettingsDeepLinkWiringTests
{
    private static string ReadSource(params string[] relative)
    {
        var parts = new List<string> { SourceScanner.RepoRoot().FullName };
        parts.AddRange(relative);
        return File.ReadAllText(Path.Combine(parts.ToArray())).Replace("\r\n", "\n");
    }

    [Fact]
    public void TheCoordinator_DispatchesThroughTheExtractedDecision()
    {
        var source = ReadSource("src", "EyeCtrl.App", "Services", "Coordinators", "SettingsUiCoordinator.cs");
        var start = source.IndexOf("private void ShowSettingsWindow(SettingsTarget target", StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var body = source[start..source.IndexOf("\n    }", start, StringComparison.Ordinal)];

        Check.WithCustomMessage(
                "ShowSettingsWindow no longer dispatches through SettingsDeepLink — the decision, "
                + "plan and execution are unit-tested there; a branch here is how the arm mutations "
                + "survive. Repoint this check, do not delete it.")
            .That(body).Contains("SettingsDeepLink.Dispatch(");
        Check.WithCustomMessage(
                "ShowSettingsWindow should contain no ad-hoc GeneralPage1 comparison any more; "
                + "the whole decision belongs in SettingsDeepLink.Decide.")
            .That(body).Not.Contains("SettingsTarget.GeneralPage1");
        Check.WithCustomMessage(
                "The dispatch belongs to SettingsDeepLink (plan + Execute), so the coordinator has "
                + "no per-action switch arm of its own to mutate (M5c/M6c/M6d).")
            .That(body).Not.Contains("switch");

        // The fresh-window navigation must not sit inside the construction try (F7): it runs from
        // Dispatch's Execute, after OpenSettingsWindow has returned.
        var openStart = source.IndexOf("private void OpenSettingsWindow()", StringComparison.Ordinal);
        Check.That(openStart).IsStrictlyGreaterThan(-1);
        var openBody = source[openStart..source.IndexOf("\n    }", openStart, StringComparison.Ordinal)];
        Check.WithCustomMessage(
                "OpenSettingsWindow must not navigate itself — Dispatch's Execute owns the ordering.")
            .That(openBody).Not.Contains("NavigateToLanguageRow()");
    }

    [Fact]
    public void TheWindow_RaisesAndLandsTheWholeRow()
    {
        var source = ReadSource("src", "EyeCtrl.App", "Views", "SettingsWindow.xaml.cs");
        var start = source.IndexOf("internal void NavigateToLanguageRow()", StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var body = source[start..source.IndexOf("\n    }", start, StringComparison.Ordinal)];

        Check.WithCustomMessage(
                "NavigateToLanguageRow must use RaiseToTop, not Activate(): the overlay is "
                + "WS_EX_NOACTIVATE (F5).")
            .That(body).Contains("RaiseToTop()");
        Check.That(body).Not.Contains("Activate();");
        Check.That(body).Contains("_languageRowDeepLink.Request(_viewModel.IsModalVisible)");
        Check.That(body).Contains("ShowLanguageRow()");

        // The queued request runs when the modal closes.
        Check.WithCustomMessage(
                "The IsModalVisible handler no longer consumes the queued deep link, so a request "
                + "that arrived behind a modal would never run.")
            .That(source).Contains("_languageRowDeepLink.TryApply(_viewModel.IsModalVisible)");

        var rowStart = source.IndexOf("private void ShowLanguageRow()", StringComparison.Ordinal);
        Check.That(rowStart).IsStrictlyGreaterThan(-1);
        var rowBody = source[rowStart..source.IndexOf("\n    }", rowStart, StringComparison.Ordinal)];
        Check.WithCustomMessage(
                "The deep link must land content, sidebar highlight AND sidebar page (F6).")
            .That(rowBody).Contains("ShowTab(\"General\")");
        Check.That(rowBody).Contains("_viewModel.ActiveTabId = \"General\"");
        Check.That(rowBody).Contains("SwitchTabPage(");
        // F6a: the sidebar page comes from General's OWN index in the visible (mode-filtered)
        // tab list, so moving General (or changing the tabs-per-page arithmetic) cannot strand
        // the highlight on the wrong sidebar page.
        Check.WithCustomMessage(
                "The sidebar page must be computed from General's own _visibleDefs index (F6a) — "
                + "the mode-filtered tab list the sidebar actually renders — not a constant page "
                + "number.")
            .That(rowBody).Contains("Array.FindIndex(_visibleDefs, t => t.Id == \"General\")");
        Check.That(rowBody).Contains("SettingsTabPagination.PageOf(index, _tabsPerPage)");
    }

    [Fact]
    public void TheWindow_ResolvesTheRestartNoticesInTheTargetLanguage()
    {
        // F1's call-site half: the notices take the TARGET (the saved selection resolved against
        // the machine culture), not the running table's language. The resolution rule itself is
        // pinned by StringTableResolveLanguageTests.
        var source = ReadSource("src", "EyeCtrl.App", "Views", "SettingsWindow.xaml.cs");

        Check.WithCustomMessage(
                "The restart notices must resolve through TargetLanguage(), not through the running "
                + "table (F1).")
            .That(source).Contains("RestartNotices.InProgressMessage(TargetLanguage())");
        Check.That(source).Contains("RestartNotices.InProgressChoices(TargetLanguage())");
        Check.That(source).Contains("RestartNotices.RefusedChoices(TargetLanguage())");

        // The one rule lives in RestartNotices.TargetLanguage since T15 F5, shared with the
        // App-level host that shows the same notices when no Settings window is open.
        var start = source.IndexOf("private UiLanguage TargetLanguage()", StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var body = source[start..source.IndexOf(";", start, StringComparison.Ordinal)];
        Check.WithCustomMessage(
                "The window's TargetLanguage must be the shared rule over the SAVED selection.")
            .That(body).Contains("RestartNotices.TargetLanguage(_settings.General.UiLanguage)");

        var notices = ReadSource("src", "EyeCtrl.App", "Views", "RestartNotices.cs");
        var ruleStart = notices.IndexOf("internal static UiLanguage TargetLanguage(", StringComparison.Ordinal);
        Check.That(ruleStart).IsStrictlyGreaterThan(-1);
        var rule = notices[ruleStart..notices.IndexOf(";", ruleStart, StringComparison.Ordinal)];
        Check.WithCustomMessage(
                "TargetLanguage must resolve the SAVED selection against the table's culture.")
            .That(rule).Contains("StringTable.ResolveLanguage(savedSelection, StringTable.Current.Culture)");
    }

    [StaFact]
    public void TheTrayItem_CarriesTheBilingualLabel_AndDeepLinksToGeneralPage1()
    {
        var messenger = new WeakReferenceMessenger();
        SettingsRequestedMessage? received = null;
        messenger.Register<SettingsRequestedMessage>(this, (_, m) => received = m);

        var dispatcher = Dispatcher.CurrentDispatcher;
        var item = TrayIconManager.CreateLanguageMenuItem(dispatcher, messenger);

        Check.That(item.Text).IsEqualTo("Sprache / Language");

        item.PerformClick();
        // The production handler marshals through the dispatcher, as every tray item does; a
        // synchronous Invoke at Normal priority runs the queued send first.
        dispatcher.Invoke(() => { }, DispatcherPriority.Normal);

        Check.That(received).IsNotNull();
        Check.That(received!.Target).IsEqualTo(SettingsTarget.GeneralPage1);
    }
}
