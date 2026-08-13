namespace EyeCtrl.App.Tests;

using System;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Settings;

/// <summary>
/// A scoped instance runs alone: there is no supervisor to hand off to, and a self-restart child
/// would inherit <c>EYECTRL_INSTANCE_ID</c> and re-enter the same state. These pin the refusal at
/// the load-bearing gate rather than at the button.
/// </summary>
public class AppRestartCoordinatorScopeTests
{
    private static AppRestartCoordinator Build(
        bool isScoped, EyeCtrlSettings settings,
        Action? onShutdown = null, Action? onEnablePublishing = null,
        Func<bool>? onLaunchSupervisor = null, Action<string>? onUnavailable = null)
        => new(
            new WeakReferenceMessenger(),
            settings,
            tryReachSupervisor: () => false,
            selfRestart: () => RestartOutcome.Started,
            shutdown: () => onShutdown?.Invoke(),
            reportInProgress: () => { },
            reportUnavailable: m => onUnavailable?.Invoke(m),
            runInBackground: a => a(),          // synchronous, so assertions need no waiting
            postToUi: a => a(),
            supervisorInstalled: () => true,
            launchSupervisor: () => onLaunchSupervisor?.Invoke() ?? true,
            enablePublishing: () => onEnablePublishing?.Invoke(),
            persistSettings: () => { },
            isScoped: isScoped);

    [Fact]
    public void Unscoped_IsNotGated()
    {
        var c = Build(isScoped: false, new EyeCtrlSettings());

        Check.That(c.IsScopeGated).IsFalse();
    }

    [Fact]
    public void Scoped_ReportsItselfGated()
    {
        var c = Build(isScoped: true, new EyeCtrlSettings());

        Check.That(c.IsScopeGated).IsTrue();
    }

    [Fact]
    public void Scoped_HandleRestart_DoesNotShutDown_AndSaysWhy()
    {
        var shutdowns = 0;
        string? message = null;
        var c = Build(isScoped: true, new EyeCtrlSettings(),
            onShutdown: () => shutdowns++, onUnavailable: m => message = m);

        c.HandleRestart(startSupervisor: false);

        Check.That(shutdowns).IsEqualTo(0);
        Check.That(message).IsNotNull();
        Check.That(message!).Contains("scoped");
    }

    [Fact]
    public void Scoped_HandleRestart_NeverEnablesPublishing()
    {
        // The specific escape this gate closes: TryBringUpSupervisor enables publishing BEFORE
        // flipping Supervision.Enabled, so a mid-session restart-with-supervisor would put a scoped
        // instance on the heartbeat pipe despite supervision being forced off at startup.
        var enables = 0;
        var launches = 0;
        var c = Build(isScoped: true, new EyeCtrlSettings(),
            onEnablePublishing: () => enables++, onLaunchSupervisor: () => { launches++; return true; });

        c.HandleRestart(startSupervisor: true);

        Check.That(enables).IsEqualTo(0);
        Check.That(launches).IsEqualTo(0);
    }

    [Fact]
    public void Scoped_HandleRestart_RefusesEveryTime_NotJustOnce()
    {
        // The refusal returns BEFORE the in-flight latch, so it must never consume the single
        // attempt. A gate placed after the latch would refuse once and then go silent forever.
        var messages = 0;
        var c = Build(isScoped: true, new EyeCtrlSettings(), onUnavailable: _ => messages++);

        c.HandleRestart(startSupervisor: false);
        c.HandleRestart(startSupervisor: false);

        Check.That(messages).IsEqualTo(2);
    }

    [Fact]
    public void Scoped_ShouldOfferSupervisor_IsFalse_EvenThoughOneIsInstalled()
    {
        var c = Build(isScoped: true, new EyeCtrlSettings());

        Check.That(c.ShouldOfferSupervisor()).IsFalse();
    }

    [Fact]
    public void Unscoped_ShouldOfferSupervisor_StillTrue_WhenInstalledAndDisabled()
    {
        // Guards against the gate being applied unconditionally: the unscoped behaviour is
        // "offer when one is installed and none is answering".
        var c = Build(isScoped: false, new EyeCtrlSettings());

        Check.That(c.ShouldOfferSupervisor()).IsTrue();
    }
}
