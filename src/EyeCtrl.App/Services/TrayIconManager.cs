namespace EyeCtrl.App.Services;

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels.Messages;
using Serilog;

/// <summary>
/// Manages the system tray icon and its menu.
/// Extracted from App.xaml.cs to improve Single Responsibility Principle compliance.
/// </summary>
public class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _pauseMenuItem;
    private readonly Icon? _customIcon;
    private readonly Dispatcher _dispatcher;
    private readonly InteractionManager _interactionManager;
    private bool _disposed;

    public TrayIconManager(InteractionManager interactionManager, Dispatcher dispatcher, Action shutdownCallback)
    {
        _dispatcher = dispatcher;
        _interactionManager = interactionManager;

        _pauseMenuItem = new ToolStripMenuItem("Pause");
        _pauseMenuItem.Click += (_, _) =>
        {
            interactionManager.TogglePause();
            _pauseMenuItem.Text = interactionManager.CurrentState == InteractionState.Paused ? "Resume" : "Pause";
        };

        var settingsItem = new ToolStripMenuItem("Settings");
        settingsItem.Click += (_, _) => dispatcher.BeginInvoke(() =>
            WeakReferenceMessenger.Default.Send(new SettingsRequestedMessage()));

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => dispatcher.BeginInvoke(shutdownCallback);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_pauseMenuItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        // Load custom icon from Assets directory
        var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "eyectrl.ico");
        Icon trayIcon = SystemIcons.Application;

        if (File.Exists(iconPath))
        {
            try
            {
                _customIcon = new Icon(iconPath);
                trayIcon = _customIcon;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load custom icon from {Path}, using system default", iconPath);
            }
        }

        _trayIcon = new NotifyIcon
        {
            Icon = trayIcon,
            Text = "EyeCtrl",
            Visible = true,
            ContextMenuStrip = menu
        };

        _trayIcon.DoubleClick += (_, _) => dispatcher.BeginInvoke(() =>
            WeakReferenceMessenger.Default.Send(new SettingsRequestedMessage()));

        interactionManager.StateChanged += OnStateChanged;
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
            _pauseMenuItem.Text = state == InteractionState.Paused ? "Resume" : "Pause";
        });
    }
}
