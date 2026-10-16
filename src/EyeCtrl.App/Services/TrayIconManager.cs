namespace EyeCtrl.App.Services;

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels.Messages;
using Serilog;

/// <summary>The App tray menu's item texts, resolved once at construction — the App tray follows a
/// language change by restart, so per-open re-resolution (the Supervisor's pattern) is not needed.
/// A record rather than inline constructor calls so the English is pinnable headlessly: a real
/// <see cref="NotifyIcon"/> needs a shell tray.</summary>
/// <param name="Pause">The pause item's face while running.</param>
/// <param name="Resume">The pause item's face while paused.</param>
/// <param name="Settings">The "open Settings" item.</param>
/// <param name="ResetAdaptation">The "reset adaptation" item.</param>
/// <param name="Exit">The "exit EyeCtrl" item.</param>
/// <param name="ShowToolbar">The "show toolbar" item, present only when the toolbar auto-hides.</param>
internal sealed record AppTrayTexts(
    string Pause, string Resume, string Settings, string ResetAdaptation, string Exit, string ShowToolbar)
{
    /// <summary>The texts for the process's current table.</summary>
    /// <param name="strings">The table to resolve from.</param>
    /// <returns>The resolved texts.</returns>
    public static AppTrayTexts For(StringTable strings) => new(
        strings.T("app-tray.pause"),
        strings.T("app-tray.resume"),
        strings.T("app-tray.settings"),
        strings.T("app-tray.reset-adaptation"),
        strings.T("app-tray.exit"),
        strings.T("app-tray.show-toolbar"));

    /// <summary>The pause item's face for <paramref name="state"/>: "Resume" while paused,
    /// "Pause" for every other state. Written at three sites, so the pair lives in one place.</summary>
    /// <param name="state">The interaction state the face describes.</param>
    /// <returns>The item text.</returns>
    public string PauseItemText(InteractionState state) =>
        state == InteractionState.Paused ? Resume : Pause;
}

/// <summary>
/// Manages the system tray icon and its menu.
/// Extracted from App.xaml.cs to improve Single Responsibility Principle compliance.
/// </summary>
public class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _pauseMenuItem;
    private readonly ToolStripMenuItem _resetAdaptationItem;
    private readonly ToolStripMenuItem? _showToolbarMenuItem;
    private readonly ContextMenuStrip _menu;
    private readonly Func<bool> _isAdaptationEnabled;
    private readonly Icon? _customIcon;
    private readonly Dispatcher _dispatcher;
    private readonly InteractionManager _interactionManager;
    private readonly IMessenger _messenger;
    private readonly AppTrayTexts _texts;
    private bool _disposed;

    public TrayIconManager(InteractionManager interactionManager, Dispatcher dispatcher, IMessenger messenger,
        Action shutdownCallback, Func<bool> isAdaptationEnabled, Action? showToolbarCallback = null)
    {
        _dispatcher = dispatcher;
        _interactionManager = interactionManager;
        _messenger = messenger;
        _texts = AppTrayTexts.For(StringTable.Current);
        // Null-rejected at the ctor rather than at first menu open — it is dereferenced only in
        // the Opening handler, far from the construction site.
        _isAdaptationEnabled = isAdaptationEnabled ?? throw new ArgumentNullException(nameof(isAdaptationEnabled));

        _pauseMenuItem = new ToolStripMenuItem(_texts.Pause);
        _pauseMenuItem.Click += (_, _) =>
        {
            interactionManager.TogglePause();
            _pauseMenuItem.Text = _texts.PauseItemText(interactionManager.CurrentState);
        };

        var settingsItem = new ToolStripMenuItem(_texts.Settings);
        settingsItem.Click += (_, _) => dispatcher.BeginInvoke(() =>
            _messenger.Send(new SettingsRequestedMessage()));

        _resetAdaptationItem = new ToolStripMenuItem(_texts.ResetAdaptation);
        _resetAdaptationItem.Click += (_, _) => dispatcher.BeginInvoke(() =>
            _messenger.Send(new ResetAdaptationMessage()));

        var exitItem = new ToolStripMenuItem(_texts.Exit);
        exitItem.Click += (_, _) => dispatcher.BeginInvoke(shutdownCallback);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_pauseMenuItem);
        menu.Items.Add(settingsItem);
        // D9 (the user's call 2026-10-07): ONE flat language item, no submenu — a hover cascade
        // is a poor fit for unsteady gaze. It deep-links to the language row in Settings, where
        // the endonyms are; it does not restart anything itself.
        menu.Items.Add(CreateLanguageMenuItem(dispatcher, _messenger));
        menu.Items.Add(_resetAdaptationItem);
        if (showToolbarCallback != null)
        {
            _showToolbarMenuItem = new ToolStripMenuItem(_texts.ShowToolbar);
            _showToolbarMenuItem.Click += (_, _) => dispatcher.BeginInvoke(showToolbarCallback);
            menu.Items.Add(_showToolbarMenuItem);
            menu.Items.Add(new ToolStripSeparator());
        }
        else
        {
            menu.Items.Add(new ToolStripSeparator());
        }
        menu.Items.Add(exitItem);
        _menu = menu;
        // Gated items re-ask their settings at every open, so they follow changes without
        // re-creating the menu or the icon (P12/L4, Task 9).
        menu.Opening += (_, _) => RefreshMenuItems();

        // Load custom icon from embedded resource
        Icon trayIcon = SystemIcons.Application;
        try
        {
            var iconUri = new Uri("pack://application:,,,/Assets/eyectrl.ico");
            var resourceInfo = System.Windows.Application.GetResourceStream(iconUri);
            if (resourceInfo != null)
            {
                using (resourceInfo.Stream)
                {
                    // L10/A4b: request the small-icon size so the tray gets the tuned small
                    // frame (32 px at 200 % DPI) instead of the large frame that
                    // Icon(Stream) picks and the shell then shrinks.
                    _customIcon = LoadTrayIcon(resourceInfo.Stream, SystemInformation.SmallIconSize);
                    trayIcon = _customIcon;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load custom icon from embedded resource, using system default");
        }

        _trayIcon = new NotifyIcon
        {
            Icon = trayIcon,
            Text = "EyeCtrl",
            Visible = true,
            ContextMenuStrip = menu
        };

        _trayIcon.DoubleClick += (_, _) => dispatcher.BeginInvoke(() =>
            _messenger.Send(new SettingsRequestedMessage()));

        interactionManager.StateChanged += OnStateChanged;
    }

    /// <summary>
    /// Loads the tray icon at an explicit size. Internal static so App.Tests can pin the
    /// size-requesting call (spec A4b): the ctor passes <see cref="System.Windows.Forms.SystemInformation.SmallIconSize"/>.
    /// </summary>
    internal static Icon LoadTrayIcon(Stream stream, System.Drawing.Size size)
    {
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        memoryStream.Position = 0;
        // Icon constructor copies the data, so the caller's stream can be disposed after
        return new Icon(memoryStream, size);
    }

    /// <summary>The tray menu, exposed for headless tests (the Supervisor tray tests' pattern:
    /// raise Opening through its own OnOpening without showing the menu).</summary>
    internal ContextMenuStrip Menu => _menu;

    /// <summary>Headless test read of the reset-adaptation item's gate. <c>Available</c>, never
    /// <c>Visible</c>: <see cref="ToolStripItem.Visible"/>'s getter answers false whenever the
    /// menu is not displayed, so it cannot be read as "is the item on".</summary>
    internal bool IsResetAdaptationItemAvailable => _resetAdaptationItem.Available;

    /// <summary>
    /// Re-evaluates gated item availability against the live delegate: the reset-adaptation item
    /// is offered only while adaptation is enabled (§8.6's manual escape exists exactly while
    /// there can be learned state to reset). Called from the menu's
    /// <see cref="ContextMenuStrip.Opening"/>; internal so headless tests can drive it directly.
    /// </summary>
    internal void RefreshMenuItems() => _resetAdaptationItem.Available = _isAdaptationEnabled();

    /// <summary>
    /// The one flat "Sprache / Language" item (D9): it deep-links to General page 1 with the
    /// language row in view, where the endonyms are readable whatever the current language.
    /// A factory rather than inline constructor lines so the label and the deep-link target are
    /// pinnable headlessly — a real <see cref="NotifyIcon"/> needs a shell tray.
    /// </summary>
    /// <param name="dispatcher">Marshals the send onto the UI thread, like every other item.</param>
    /// <param name="messenger">Receives <see cref="SettingsRequestedMessage"/>.</param>
    /// <returns>The configured menu item.</returns>
    internal static ToolStripMenuItem CreateLanguageMenuItem(Dispatcher dispatcher, IMessenger messenger)
    {
        var item = new ToolStripMenuItem(StringTable.Current.T("language-recovery.tray.item"));
        item.Click += (_, _) => dispatcher.BeginInvoke(() =>
            messenger.Send(new SettingsRequestedMessage(SettingsTarget.GeneralPage1)));
        return item;
    }

    public void Dispose()
    {
        if (_disposed) return;

        // Unsubscribe from InteractionManager events
        _interactionManager.StateChanged -= OnStateChanged;

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _customIcon?.Dispose();

        _disposed = true;
    }

    private void OnStateChanged(InteractionState state)
    {
        _dispatcher.BeginInvoke(() =>
        {
            _pauseMenuItem.Text = _texts.PauseItemText(state);
        });
    }
}
