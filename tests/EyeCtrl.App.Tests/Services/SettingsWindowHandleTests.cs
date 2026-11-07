namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Windows;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Overlays;
using EyeCtrl.App.Views;
using EyeCtrl.ViewModels;
using Xunit;

/// <summary>
/// C9 S6e fixup-08: the real Settings window's hide and restore. The ticket's owner decision is
/// about a window that must actually leave the screen (Settings is band-2 topmost; Setup's Q8 can
/// never be raised above it) and come back on „Erweitert". The coordinator's state machine is
/// pinned in <see cref="Coordinators.SettingsUninstallHandoffTests" />; this proves the two window
/// calls it delegates to — <c>Hide()</c> and <c>ShowOnAdvanced()</c> — on the real WPF window.
/// The window is prepared with <see cref="InvisibleTestWindow" /> first: the owner controls this
/// PC by eye-tracking, so no test may paint a window.
/// </summary>
[Collection("WpfApplication")]
public class SettingsWindowHandleTests
{
    [StaFact]
    public void Hide_ThenShowOnAdvanced_LeavesTheRealWindowVisibleOnTheAdvancedTab()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var cleanups = new List<Action>();
        SettingsWindow? window = null;
        try
        {
            window = InvisibleTestWindow.Prepare(
                OverlayClientOriginTests.BuildSettingsWindow(cleanups));
            var handoff = new SettingsWindowHandle(window);

            window.Show();
            var viewModel = (SettingsWindowViewModel)window.DataContext;
            Check.That(handoff.IsVisible).IsTrue();

            handoff.Hide();

            Check.That(handoff.IsVisible).IsFalse();
            Check.That(window.IsVisible).IsFalse();
            // Hide, not Close: the window and its view model keep their state (a close permanently
            // disposes the view model).
            Check.That(window.DataContext).IsSameReferenceAs(viewModel);

            handoff.ShowOnAdvanced();

            Check.That(handoff.IsVisible).IsTrue();
            Check.That(window.IsVisible).IsTrue();
            Check.WithCustomMessage("the restore did not land on „Erweitert\"")
                .That(viewModel.Settings.CurrentTabName).IsEqualTo("Advanced");
            Check.WithCustomMessage("the sidebar would highlight a different tab than the content")
                .That(viewModel.ActiveTabId).IsEqualTo("Advanced");
        }
        finally
        {
            try { window?.Close(); } catch (Exception) { /* the harness owns teardown */ }
            foreach (var cleanup in cleanups)
                cleanup();
        }
    }

    [StaFact]
    public void AnUnshownWindow_IsNotVisible()
    {
        // The seam the coordinator reads before hiding: a window that is already hidden (or never
        // shown) must not report itself as visible, or the restore would show a second time.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var cleanups = new List<Action>();
        try
        {
            var window = InvisibleTestWindow.Prepare(
                OverlayClientOriginTests.BuildSettingsWindow(cleanups));
            var handoff = new SettingsWindowHandle(window);

            Check.That(handoff.IsVisible).IsFalse();
            Check.That(window.IsVisible).IsFalse();
        }
        finally
        {
            foreach (var cleanup in cleanups)
                cleanup();
        }
    }
}
