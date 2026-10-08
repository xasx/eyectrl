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
/// General page 1 with the language row in view. Its three window states — closed, open on
/// another tab, a modal already up — live in <see cref="SettingsDeepLinkQueue"/> (headlessly
/// pinned here) plus the wire-up inside <c>SettingsWindow</c> and <c>SettingsUiCoordinator</c>
/// (source-pinned, because both need a display and their full dependency graphs to construct).
/// </summary>
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
/// The wire-up the queue test above cannot reach: the coordinator navigates for an already-open
/// window AND for a freshly opened one, the window routes through the queue and forward-brings,
/// and the tray item sends the target. Source-pinned in the same spirit as the settings
/// chunker's wiring guards.
/// </summary>
public class SettingsDeepLinkWiringTests
{
    private static string ReadSource(params string[] relative)
    {
        var parts = new List<string> { SourceScanner.RepoRoot().FullName };
        parts.AddRange(relative);
        return File.ReadAllText(Path.Combine(parts.ToArray()));
    }

    [Fact]
    public void TheCoordinator_AppliesTheDeepLink_ToBothAFreshAndAnOpenWindow()
    {
        var source = ReadSource("src", "EyeCtrl.App", "Services", "Coordinators", "SettingsUiCoordinator.cs");
        var start = source.IndexOf("private void ShowSettingsWindow(SettingsTarget target", StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var body = source[start..source.IndexOf("\n    }", start, StringComparison.Ordinal)];

        Check.WithCustomMessage(
                "ShowSettingsWindow no longer routes a deep-link request through the window. An "
                + "already-open window must not silently swallow the request (its old early return "
                + "did), and a fresh window must navigate after Show. Repoint this check, do not "
                + "delete it.")
            .That(Occurrences(body, "NavigateToLanguageRow()")).IsEqualTo(2);

        Check.WithCustomMessage(
                "The open-window branch must still leave the default request a no-op — only a "
                + "General-page deep link touches an open window.")
            .That(body).Contains("SettingsTarget.GeneralPage1");
    }

    [Fact]
    public void TheWindow_NavigatesThroughTheQueue_AndForwardBrings()
    {
        var source = ReadSource("src", "EyeCtrl.App", "Views", "SettingsWindow.xaml.cs");
        var start = source.IndexOf("internal void NavigateToLanguageRow()", StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var body = source[start..source.IndexOf("\n    }", start, StringComparison.Ordinal)];

        Check.That(body).Contains("Activate()");
        Check.That(body).Contains("_languageRowDeepLink.Request(_viewModel.IsModalVisible)");
        Check.That(body).Contains("ShowTab(\"General\")");

        // And the queued request runs when the modal closes: the IsModalVisible handler applies it.
        Check.WithCustomMessage(
                "The IsModalVisible handler no longer consumes the queued deep link, so a request "
                + "that arrived behind a modal would never run.")
            .That(source).Contains("_languageRowDeepLink.TryApply(_viewModel.IsModalVisible)");
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

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
