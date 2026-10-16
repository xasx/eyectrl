namespace EyeCtrl.App.Tests.Services;

using System.ComponentModel;
using System.Reflection;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Processing;
using Xunit;

/// <summary>
/// Task 9 (P12/L4): the reset-adaptation tray item tracks
/// <c>AdaptationSettings.Enabled</c> through the menu's Opening, so the manual reset escape
/// (§8.6) is offered exactly while there can be learned state to reset.
/// <para>
/// <b>Available, never Visible.</b> <see cref="System.Windows.Forms.ToolStripItem.Visible"/>'s
/// getter answers false whenever the menu is not displayed, so it can never be read headlessly
/// as "is the item on". <c>Available</c> is the real gate the menu honors at open time.
/// </para>
/// <para>
/// The Opening path is driven without showing the menu, by raising the strip's own
/// <c>OnOpening</c> through reflection — the same precedent <c>SupervisorTrayIconTests</c> set
/// for the Supervisor's tray. A real open needs a shell tray interaction that CI does not have.
/// </para>
/// </summary>
public class TrayIconManagerTests
{
    private static TrayIconManager NewTray(Func<bool> isAdaptationEnabled)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        return new TrayIconManager(
            new InteractionManager(), dispatcher, new WeakReferenceMessenger(),
            shutdownCallback: () => { },
            isAdaptationEnabled: isAdaptationEnabled);
    }

    /// <summary>Raises the menu's Opening the way a shell open does, without showing it.</summary>
    private static void RaiseOpening(TrayIconManager tray)
    {
        var onOpening = tray.Menu.GetType().GetMethod(
            "OnOpening", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(CancelEventArgs)])!;
        onOpening.Invoke(tray.Menu, [new CancelEventArgs()]);
    }

    [StaFact]
    public void MenuOpening_WithAdaptationDisabled_HidesTheResetItem()
    {
        using var tray = NewTray(() => false);

        RaiseOpening(tray);

        Check.That(tray.IsResetAdaptationItemAvailable).IsFalse();
    }

    [StaFact]
    public void MenuOpening_ReasksTheGateOnEveryOpen()
    {
        // The delegate is live state, not a snapshot taken at construction: the whole point of
        // the gate is that the item follows the setting without re-creating the menu or the icon.
        var enabled = false;
        using var tray = NewTray(() => enabled);

        RaiseOpening(tray);
        Check.That(tray.IsResetAdaptationItemAvailable).IsFalse();

        enabled = true;
        RaiseOpening(tray);
        Check.That(tray.IsResetAdaptationItemAvailable).IsTrue();

        enabled = false;
        RaiseOpening(tray);
        Check.That(tray.IsResetAdaptationItemAvailable).IsFalse();
    }

    [StaFact]
    public void RefreshMenuItems_TheHeadlessSeam_AppliesTheSameGate()
    {
        // The Opening wiring delegates to this method; pinning it directly keeps a refactor of
        // the event handler honest even if the reflection raise above ever breaks.
        var enabled = true;
        using var tray = NewTray(() => enabled);

        tray.RefreshMenuItems();
        Check.That(tray.IsResetAdaptationItemAvailable).IsTrue();

        enabled = false;
        tray.RefreshMenuItems();
        Check.That(tray.IsResetAdaptationItemAvailable).IsFalse();
    }
}
