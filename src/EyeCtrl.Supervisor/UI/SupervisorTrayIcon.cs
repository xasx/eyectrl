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
    /// <summary>The hover refresh interval: <see cref="NotifyIcon.MouseMove"/> repeats while the
    /// pointer moves, and each refresh re-resolves the table, so a burst costs one read.</summary>
    private const long HoverRefreshIntervalMs = 2000;

    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _launchItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly Func<SupervisorTrayTexts> _texts;
    private readonly Func<string?>? _refreshStatus;
    private readonly Func<long> _tickCount;
    private long _lastHoverRefreshTick;
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
    /// <param name="refreshStatus">Re-resolves the tooltip line at its display moments — hover
    /// (throttled) and menu open; null when the host does not manage a status tooltip. Never
    /// called from the supervision loop (T06 review-01 F2).</param>
    /// <param name="tickCount">The MONOTONIC milliseconds clock the hover throttle reads; a
    /// backward wall-clock step cannot swallow hovers, and tests advance it without waiting.
    /// Null uses <see cref="Environment.TickCount64"/>.</param>
    public SupervisorTrayIcon(
        Action openSettings, Action launchAppNow, Action exit, Func<SupervisorTrayTexts> texts,
        bool visible = true, Func<string?>? refreshStatus = null, Func<long>? tickCount = null)
    {
        _texts = texts;
        _refreshStatus = refreshStatus;
        _tickCount = tickCount ?? (() => Environment.TickCount64);
        // Seed the throttle so the FIRST hover passes: subtracting the interval (rather than
        // starting at 0/MinValue) keeps the comparison overflow-free on a monotonic clock.
        _lastHoverRefreshTick = _tickCount() - HoverRefreshIntervalMs;
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
        menu.Opening += (_, _) =>
        {
            RefreshTexts();
            // The menu is the tooltip's other display moment; unlike the hover it is not
            // throttled, because opening the menu is one deliberate act.
            ApplyStatusRefresh();
        };

        // The supervisor ships no pack-URI icon assets (no ApplicationDefinition / App.xaml),
        // so use the system default rather than attempting TrayIconManager's pack-URI load.
        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "EyeCtrl Supervisor",
            Visible = visible,
            ContextMenuStrip = menu
        };

        if (_refreshStatus is not null)
            _trayIcon.MouseMove += (_, _) => ApplyHoverRefresh();

        // Double-click is the conventional shortcut to the primary action (settings).
        _trayIcon.DoubleClick += (_, _) => openSettings();
    }

    /// <summary>The context menu, as a plain component: tests raise its Opening event by reflection
    /// and so need no WinForms reference of their own.</summary>
    internal System.ComponentModel.Component Menu { get; }

    /// <summary>The menu's current item texts, top to bottom (for tests).</summary>
    internal (string Settings, string LaunchNow, string Exit) ItemTexts =>
        (_settingsItem.Text ?? "", _launchItem.Text ?? "", _exitItem.Text ?? "");

    /// <summary>The tooltip currently shown (for tests); <see cref="SetStatusText"/> already
    /// applied the shell's cap.</summary>
    internal string StatusText => _trayIcon.Text ?? "";

    /// <summary>The icon component, so tests can raise its hover event by reflection the way the
    /// shell does (the <see cref="Menu"/> precedent).</summary>
    internal NotifyIcon TrayIcon => _trayIcon;

    /// <summary>The hover refresh, throttled to one resolve per
    /// <see cref="HoverRefreshIntervalMs"/> (for tests and the icon's MouseMove).</summary>
    /// <returns>The applied line, or null when the throttle swallowed the call or the provider
    /// abstained/failed.</returns>
    internal string? ApplyHoverRefresh()
    {
        if (_refreshStatus is null) return null;

        var now = _tickCount();
        if (now - _lastHoverRefreshTick < HoverRefreshIntervalMs) return null;
        _lastHoverRefreshTick = now;
        return ApplyStatusRefresh();
    }

    /// <summary>The unthrottled status refresh (the menu's Opening).</summary>
    /// <returns>The applied line, or null when there is no provider or it abstained/failed.</returns>
    internal string? ApplyStatusRefresh()
    {
        if (_refreshStatus is null) return null;

        string? text;
        try
        {
            text = _refreshStatus();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Tray status text could not be resolved; keeping the current one.");
            return null;
        }

        if (text is not null) SetStatusText(text);
        return text;
    }

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

    /// <summary>
    /// The tooltip's character budget (D12, <c>tray63</c>): the shell caps NotifyIcon.Text at about
    /// 63 characters, so the prefix plus the state phrase are written against it. T09's tray63 fit
    /// gate measures every phrase against this number.
    /// </summary>
    internal const int TooltipCharacterCap = 63;

    /// <summary>Updates the icon's hover text (e.g. to reflect current supervision state).</summary>
    public void SetStatusText(string text)
    {
        // NotifyIcon.Text is capped at ~63 chars by the shell; truncate defensively.
        _trayIcon.Text = text.Length > TooltipCharacterCap ? text[..TooltipCharacterCap] : text;
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
