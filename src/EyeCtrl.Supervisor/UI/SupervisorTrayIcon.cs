namespace EyeCtrl.Supervisor.UI;

using System;
using System.Windows.Forms;
using EyeCtrl.Core.Localization;

/// <summary>The tray menu's item texts, resolved each time the menu opens.</summary>
/// <param name="Settings">The "open setup" item.</param>
/// <param name="LaunchNow">The "launch the App" item; the shared <c>supervisor.launch-now</c> key.</param>
/// <param name="Exit">The "exit the Supervisor" item.</param>
internal sealed record SupervisorTrayTexts(string Settings, string LaunchNow, string Exit)
{
    /// <summary>The texts for the Supervisor's current table. The menu re-asks on every open
    /// (T05b), so the items follow a language change without a Supervisor restart; the keys are
    /// the <c>supervisor</c> surface's (T06).</summary>
    /// <param name="strings">The Supervisor's table, resolved at the moment the menu opens.</param>
    public static SupervisorTrayTexts For(StringTable strings) =>
        new(
            strings.T("supervisor.tray.settings"),
            strings.T("supervisor.launch-now"),
            strings.T("supervisor.tray.exit"));
}

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
    private readonly Func<SupervisorTrayTexts> _texts;
    private bool _disposed;

    /// <summary>
    /// Creates and shows the tray icon.
    /// </summary>
    /// <param name="openSettings">Invoked when the user picks "settings" (or double-clicks the icon). Opens the <see cref="SupervisorSetupWindow"/>.</param>
    /// <param name="launchAppNow">Invoked when the user picks "Launch EyeCtrl now". Task 20 feeds a <c>LaunchRequest</c> into the state machine.</param>
    /// <param name="exit">Invoked when the user picks "Exit supervisor". Task 20 tears the loop down.</param>
    /// <param name="texts">The item texts, asked again every time the menu opens, so they follow a
    /// language change without a Supervisor restart (T05b).</param>
    /// <param name="visible">Whether the icon shows in the tray; tests pass false.</param>
    public SupervisorTrayIcon(
        Action openSettings, Action launchAppNow, Action exit, Func<SupervisorTrayTexts> texts, bool visible = true)
    {
        _texts = texts;
        _settingsItem = new ToolStripMenuItem();
        _settingsItem.Click += (_, _) => openSettings();

        _launchItem = new ToolStripMenuItem();
        _launchItem.Click += (_, _) => launchAppNow();

        _exitItem = new ToolStripMenuItem();
        _exitItem.Click += (_, _) => exit();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_settingsItem);
        menu.Items.Add(_launchItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exitItem);
        Menu = menu;
        RefreshTexts();
        menu.Opening += (_, _) => RefreshTexts();

        // The supervisor ships no pack-URI icon assets (no ApplicationDefinition / App.xaml),
        // so use the system default rather than attempting TrayIconManager's pack-URI load.
        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "EyeCtrl Supervisor",
            Visible = visible,
            ContextMenuStrip = menu
        };

        // Double-click is the conventional shortcut to the primary action (settings).
        _trayIcon.DoubleClick += (_, _) => openSettings();
    }

    /// <summary>The context menu, as a plain component: tests raise its Opening event by reflection
    /// and so need no WinForms reference of their own.</summary>
    internal System.ComponentModel.Component Menu { get; }

    /// <summary>The menu's current item texts, top to bottom (for tests).</summary>
    internal (string Settings, string LaunchNow, string Exit) ItemTexts =>
        (_settingsItem.Text ?? "", _launchItem.Text ?? "", _exitItem.Text ?? "");

    /// <summary>Re-reads the item texts. Runs when the menu opens; a failing provider keeps the
    /// texts the menu already shows rather than costing the menu.</summary>
    private void RefreshTexts()
    {
        SupervisorTrayTexts texts;
        try
        {
            texts = _texts();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Tray texts could not be resolved; keeping the current ones.");
            return;
        }
        _settingsItem.Text = texts.Settings;
        _launchItem.Text = texts.LaunchNow;
        _exitItem.Text = texts.Exit;
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
