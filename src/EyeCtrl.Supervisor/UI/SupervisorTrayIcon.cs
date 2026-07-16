namespace EyeCtrl.Supervisor.UI;

using System;
using System.Windows.Forms;

/// <summary>
/// The supervisor's system-tray icon + context menu: a liveness indicator and a small set
/// of quick actions (open setup, launch the app, exit). Modeled on
/// <c>EyeCtrl.App.Services.TrayIconManager</c> but cut down to the supervisor's needs —
/// this is a watchdog, not the main app, so the menu is intentionally tiny.
/// </summary>
/// <remarks>
/// All actions are surfaced as constructor-injected callbacks so Task 20's supervision
/// loop wires them to the state machine (e.g. the "Launch now" item feeds a
/// <c>LaunchRequest</c> into the loop). Implements <see cref="IDisposable"/> and, like
/// <c>TrayIconManager</c>, sets <see cref="NotifyIcon.Visible"/> to <c>false</c> before
/// disposing the underlying <see cref="NotifyIcon"/> (so the icon vanishes cleanly from
/// the tray rather than lingering as a ghost until the pointer hovers it).
/// </remarks>
internal sealed class SupervisorTrayIcon : IDisposable
{
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _launchItem;
    private readonly ToolStripMenuItem _exitItem;
    private bool _disposed;

    /// <summary>
    /// Creates and shows the tray icon.
    /// </summary>
    /// <param name="openSettings">Invoked when the user picks "settings" (or double-clicks the icon). Opens the <see cref="SupervisorSetupWindow"/>.</param>
    /// <param name="launchAppNow">Invoked when the user picks "Launch EyeCtrl now". Task 20 feeds a <c>LaunchRequest</c> into the state machine.</param>
    /// <param name="exit">Invoked when the user picks "Exit supervisor". Task 20 tears the loop down.</param>
    public SupervisorTrayIcon(Action openSettings, Action launchAppNow, Action exit)
    {
        _settingsItem = new ToolStripMenuItem("EyeCtrl Supervisor settings");
        _settingsItem.Click += (_, _) => openSettings();

        _launchItem = new ToolStripMenuItem("Launch EyeCtrl now");
        _launchItem.Click += (_, _) => launchAppNow();

        _exitItem = new ToolStripMenuItem("Exit supervisor");
        _exitItem.Click += (_, _) => exit();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_settingsItem);
        menu.Items.Add(_launchItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exitItem);

        // The supervisor ships no pack-URI icon assets (no ApplicationDefinition / App.xaml),
        // so use the system default rather than attempting TrayIconManager's pack-URI load.
        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "EyeCtrl Supervisor",
            Visible = true,
            ContextMenuStrip = menu
        };

        // Double-click is the conventional shortcut to the primary action (settings).
        _trayIcon.DoubleClick += (_, _) => openSettings();
    }

    /// <summary>Updates the icon's hover text (e.g. to reflect current supervision state).</summary>
    public void SetStatusText(string text)
    {
        // NotifyIcon.Text is capped at ~63 chars by the shell; truncate defensively.
        _trayIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    /// <inheritdoc />
    /// <remarks>Mirrors TrayIconManager: hide first, then dispose.</remarks>
    public void Dispose()
    {
        if (_disposed) return;

        _trayIcon.Visible = false;
        _trayIcon.Dispose();

        _disposed = true;
    }
}
