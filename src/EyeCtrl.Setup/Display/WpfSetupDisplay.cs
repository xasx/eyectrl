namespace EyeCtrl.Setup.Display;

using System.Threading;
using System.Windows.Threading;

/// <summary>
/// The production display: one progress/result window on its own STA dispatcher thread, so the
/// synchronous workflow can keep reporting stages while the window repaints. <see cref="Finish"/>
/// blocks until the result has closed itself, which keeps the process alive for the 10 s / 60 s
/// timer; <see cref="Dispose"/> then shuts the thread down.
///
/// <para>
/// Nothing here may fail the install: every call is wrapped and a window that cannot be created
/// simply shows nothing. The screen is display-only, never a target.
/// </para>
/// </summary>
internal sealed class WpfSetupDisplay : ISetupDisplay
{
    private readonly SetupVerb _verb;
    private readonly SetupUi _ui;
    private readonly string _appDataRoot;
    private readonly Func<SetupProgressWindow> _createWindow;
    private readonly Action<SetupProgressWindow> _showWindow;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _threadReady = new(false);
    private readonly ManualResetEventSlim _closed = new(false);

    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private SetupProgressWindow? _window;
    private bool _windowClosed;
    private bool _disposed;

    public WpfSetupDisplay(SetupVerb verb, SetupUi ui, string appDataRoot)
        : this(verb, ui, appDataRoot, createWindow: null, showWindow: null)
    {
    }

    /// <summary>
    /// The test seam: production leaves both null and gets the real window shown once with
    /// <see cref="Window.Show"/>. A test supplies a window it can close without ever painting it,
    /// which is how the "an early close only ends the display" path is exercised through this seam.
    /// </summary>
    internal WpfSetupDisplay(
        SetupVerb verb, SetupUi ui, string appDataRoot,
        Func<SetupProgressWindow>? createWindow, Action<SetupProgressWindow>? showWindow)
    {
        _verb = verb;
        _ui = ui;
        _appDataRoot = appDataRoot;
        _createWindow = createWindow ?? (() => new SetupProgressWindow(_ui.Strings, _verb));
        _showWindow = showWindow ?? (window => window.Show());
    }

    /// <inheritdoc />
    public void Stage(SetupStage stage)
    {
        try
        {
            Invoke(() =>
            {
                EnsureWindow();
                if (_windowClosed)
                    return;
                _window!.Stage(stage);
            });
        }
        catch (Exception)
        {
            // Display only: a screen failure never stops the install.
        }
    }

    /// <inheritdoc />
    public void Finish(SetupResult result)
    {
        try
        {
            var mapped = SetupDisplayText.Result(_ui.TableFor(result), result, _appDataRoot);
            Invoke(() =>
            {
                EnsureWindow();
                if (_windowClosed)
                    return;
                _window!.ShowResult(mapped);
            });

            // Wait for the window's own timer to close it (60 s + a margin for a slow paint). An
            // early close (the owner's Alt+F4 or close button) has already set this event, so the
            // display ends at once and the install is unaffected.
            _closed.Wait(TimeSpan.FromSeconds(75));
        }
        catch (Exception)
        {
            // Display only.
        }
        finally
        {
            Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Thread? thread;
        Dispatcher? dispatcher;
        SetupProgressWindow? window;

        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            thread = _thread;
            dispatcher = _dispatcher;
            window = _window;
            _window = null;
        }

        if (dispatcher is not null)
        {
            try
            {
                if (_windowClosed)
                {
                    // The owner already closed it; nothing to do.
                }
                else if (dispatcher.CheckAccess())
                {
                    window?.Close();
                }
                else
                {
                    dispatcher.Invoke(() => window?.Close());
                }
            }
            catch (Exception)
            {
                // The dispatcher may already be shutting down.
            }

            try
            {
                dispatcher.InvokeShutdown();
            }
            catch (Exception)
            {
                // Already shut down.
            }

            try
            {
                thread?.Join(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // A background thread that will not join must not hold the install.
            }
        }

        _threadReady.Dispose();
        _closed.Dispose();
    }

    private void Invoke(Action action)
    {
        var dispatcher = EnsureThread();
        if (dispatcher is null)
            return;

        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    private Dispatcher? EnsureThread()
    {
        lock (_gate)
        {
            if (_disposed)
                return null;

            if (_thread is null)
            {
                _thread = new Thread(() =>
                {
                    _dispatcher = Dispatcher.CurrentDispatcher;
                    _threadReady.Set();
                    Dispatcher.Run();
                })
                {
                    IsBackground = true,
                    Name = "EyeCtrl.Setup.Display",
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
                _threadReady.Wait();
            }

            return _dispatcher;
        }
    }

    private void EnsureWindow()
    {
        if (_window is not null)
            return;

        var window = _createWindow();
        // The owner may close the ordinary window early (Alt+F4 or its close button). That only
        // ends the display: the install keeps running and Finish stops waiting immediately.
        window.Closed += (_, _) =>
        {
            _windowClosed = true;
            _closed.Set();
        };
        _window = window;
        _showWindow(window);
    }
}

/// <summary>The production factory: a WPF display for the three verbs that show one, the null
/// display for everything else (repair, status).</summary>
public sealed class WpfSetupDisplayFactory : ISetupDisplayFactory
{
    private readonly SetupUi _ui;
    private readonly string _appDataRoot;

    /// <summary>Creates the factory.</summary>
    public WpfSetupDisplayFactory(SetupUi ui, string appDataRoot)
    {
        _ui = ui;
        _appDataRoot = appDataRoot;
    }

    /// <inheritdoc />
    public ISetupDisplay Create(SetupVerb verb) => verb switch
    {
        SetupVerb.Update or SetupVerb.Rollback or SetupVerb.Uninstall =>
            new WpfSetupDisplay(verb, _ui, _appDataRoot),
        _ => NullSetupDisplay.Instance,
    };
}
