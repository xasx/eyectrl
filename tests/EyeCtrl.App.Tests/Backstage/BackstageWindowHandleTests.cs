namespace EyeCtrl.App.Tests.Backstage;

using System;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Ticket 04: the Backstage window's half of the uninstall hand-off. The coordinator's state
/// machine is pinned in <see cref="BackstageUninstallHandoffTests"/>; this proves the three window
/// calls it delegates to — <see cref="IUninstallHandoffWindowHandle.IsVisible"/>, <c>Hide()</c> and
/// <c>Show()</c> — on the real WPF window, including the notice subscriptions and the view model's
/// hidden state. The window is prepared with <see cref="InvisibleTestWindow"/> first: the owner
/// controls this PC by eye-tracking, so no test may paint a window.
/// </summary>
[Collection("WpfApplication")]
public class BackstageWindowHandleTests
{
    private sealed class Harness : IDisposable
    {
        public WeakReferenceMessenger Messenger { get; } = new();
        public EyeCtrlSettings Settings { get; } = new();
        public BackstageViewModel ViewModel { get; }
        public BackstageWindow Window { get; }
        public BackstageWindowHandle Handle { get; }

        public Harness()
        {
            WpfApplicationGate.EnsureApplicationWithTokens();
            ViewModel = new BackstageViewModel(Messenger, Settings);
            Window = InvisibleTestWindow.Prepare(new BackstageWindow());
            Window.Configure(
                Messenger,
                ViewModel,
                () => new BackstageStatusView(
                    new BackstageStatusSnapshot("Version 1.0", "Installed · Standard", "Supervisor connected"),
                    ShowSupervisor: true),
                () => false,
                () => { },
                _ => { },
                () => UiLanguage.English,
                setupGateways: null,
                uninstallHandoff: null,
                isPaused: () => false);
            Handle = new BackstageWindowHandle(Window);
        }

        public void Dispose()
        {
            Window.HideBackstage();
            ViewModel.Dispose();
        }
    }

    [StaFact]
    public void Hide_ThenShow_LeavesTheSameWindowAndViewModel_AndTogglesItsNotices()
    {
        using var h = new Harness();
        h.Window.ShowBackstage();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        Check.That(h.Handle.IsVisible).IsTrue();
        Check.That(h.Messenger.IsRegistered<RestartUnavailableMessage>(h.Window)).IsTrue();

        h.Handle.Hide();

        Check.That(h.Handle.IsVisible).IsFalse();
        Check.That(h.Window.IsVisible).IsFalse();
        Check.WithCustomMessage("the view model must leave the live dwell surface while hidden")
            .That(h.ViewModel.IsVisible).IsFalse();
        Check.WithCustomMessage("a notice delivered during Setup's Q8 must go to the App host, not a hidden window")
            .That(h.Messenger.IsRegistered<RestartUnavailableMessage>(h.Window)).IsFalse();

        h.Handle.Show();

        Check.That(h.Handle.IsVisible).IsTrue();
        Check.That(h.Window.IsVisible).IsTrue();
        Check.That(h.ViewModel.IsVisible).IsTrue();
        Check.That(h.Messenger.IsRegistered<RestartUnavailableMessage>(h.Window)).IsTrue();
        Check.WithCustomMessage("the restored window did not render its clock")
            .That(h.Window.ClockTimeForTest).Not.IsEmpty();
    }

    [StaFact]
    public void AnUnshownWindow_IsNotVisible()
    {
        // The coordinator reads this before hiding: a window that was never shown (or is already
        // hidden) must not report itself as visible, or the restore would show a second time.
        using var h = new Harness();

        Check.That(h.Handle.IsVisible).IsFalse();
        Check.That(h.Window.IsVisible).IsFalse();
    }
}
