namespace EyeCtrl.App.Browse;

using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using EyeCtrl.Core.Localization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

/// <summary>
/// The concrete <see cref="IBrowseWebSource"/>: owns the
/// <c>Microsoft.Web.WebView2.Wpf.WebView2</c> control and is the ONLY type in the app that
/// touches the WebView2 runtime. The environment is created ONCE per process, lazily, with a
/// dedicated user-data folder under <c>%LOCALAPPDATA%\EyeCtrl\WebView2</c> — never the Edge
/// profile, and never a folder another WebView2 host already bound (a user-data folder bound
/// twice fails the environment creation).
/// <para>
/// Never constructed in unit tests — the seam exists precisely so nothing else needs the
/// runtime. The managed control itself loads no native code until
/// <see cref="EnsureInitializedAsync"/> runs, which is what keeps the failure modes separable:
/// a missing Evergreen runtime surfaces as <see cref="WebView2RuntimeNotFoundException"/> from
/// environment creation (T5's Failed path shows the user a notice), while a broken native-loader
/// load under PublishSingleFile surfaces as a <see cref="DllNotFoundException"/>/
/// <see cref="FileNotFoundException"/> — distinct reasons, one <see cref="IBrowseWebSource.Failed"/>
/// channel.
/// </para>
/// </summary>
internal sealed class WebView2BrowseHost : IBrowseWebSource
{
    private static CoreWebView2Environment? _environment;

    private readonly WebView2 _control = new();
    private CoreWebView2? _core;
    private Task<CoreWebView2>? _initialization;

    /// <summary>The WPF control the hosting window places. Managed construction only — the
    /// native runtime loads on first navigate, not here.</summary>
    internal FrameworkElement Control => _control;

    /// <inheritdoc />
    public event Action<string>? ElementMessageReceived;

    /// <inheritdoc />
    public event Action<string, bool>? NavigationCompleted;

    /// <inheritdoc />
    public event Action<bool>? BackPossibilityChanged;

    /// <inheritdoc />
    public event Action<bool>? ForwardPossibilityChanged;

    /// <inheritdoc />
    public event Action? Ready;

    /// <inheritdoc />
    public event Action<string>? Failed;

    /// <inheritdoc />
    public async void Navigate(Uri uri)
    {
        try
        {
            var core = await EnsureInitializedAsync().ConfigureAwait(true);
            core.Navigate(uri.ToString());
        }
        catch (Exception ex)
        {
            Failed?.Invoke(Describe(ex));
        }
    }

    /// <inheritdoc />
    public async void NavigateToString(string html)
    {
        try
        {
            var core = await EnsureInitializedAsync().ConfigureAwait(true);
            core.NavigateToString(html);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(Describe(ex));
        }
    }

    /// <inheritdoc />
    public void GoBack()
    {
        if (_core is { CanGoBack: true })
            _core.GoBack();
    }

    /// <inheritdoc />
    public void GoForward()
    {
        if (_core is { CanGoForward: true })
            _core.GoForward();
    }

    /// <inheritdoc />
    public void Reload() => _core?.Reload();

    /// <inheritdoc />
    public void PostToPage(string json) => _core?.PostWebMessageAsJson(json);

    /// <summary>
    /// Initializes the runtime once: creates (or reuses) the process-wide environment, awaits
    /// <c>EnsureCoreWebView2Async</c>, installs the bridge script and the event plumbing, and
    /// raises <see cref="Ready"/>. Every command funnels through this one task, so the
    /// initialization is single-flight; a failed initialization leaves the task faulted and a
    /// later command retries the whole thing (and re-raises <see cref="Failed"/> through
    /// <see cref="InitializeCoreAsync"/>'s catch). All commands arrive on the UI thread, so the
    /// check-then-start race below is not reachable in production.
    /// </summary>
    private Task<CoreWebView2> EnsureInitializedAsync()
    {
        if (_core is { } core)
            return Task.FromResult(core);
        if (_initialization is null || _initialization.IsFaulted)
            _initialization = InitializeCoreAsync();
        return _initialization;
    }

    private async Task<CoreWebView2> InitializeCoreAsync()
    {
        try
        {
            var environment = await GetOrCreateEnvironmentAsync().ConfigureAwait(true);
            await _control.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            var core = _control.CoreWebView2;

            await core.AddScriptToExecuteOnDocumentCreatedAsync(BrowseElementScript.Source)
                .ConfigureAwait(true);

            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationCompleted += OnNavigationCompleted;
            core.HistoryChanged += OnHistoryChanged;

            _core = core;
            Ready?.Invoke();
            return core;
        }
        catch (Exception ex)
        {
            Failed?.Invoke(Describe(ex));
            throw;
        }
    }

    private static async Task<CoreWebView2Environment> GetOrCreateEnvironmentAsync()
    {
        if (_environment is { } existing)
            return existing;

        // The user-data folder is ours alone: under the app's own data root, never the Edge
        // profile, and never a folder a second WebView2 host could bind concurrently (the
        // second CreateAsync would fail). Single-flight by construction: this runs only inside
        // EnsureInitializedAsync's one in-flight task.
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EyeCtrl", "WebView2");
        var created = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder).ConfigureAwait(true);

        _environment ??= created;
        return _environment;
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e) =>
        ElementMessageReceived?.Invoke(e.TryGetWebMessageAsString());

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // CoreWebView2NavigationCompletedEventArgs carries no Uri (only NavigationStarting does);
        // the control's Source is already the settled document's URL when this fires.
        NavigationCompleted?.Invoke(_control.CoreWebView2?.Source ?? string.Empty, e.IsSuccess);
    }

    private void OnHistoryChanged(object? sender, object e)
    {
        if (_core is not { } core)
            return;
        BackPossibilityChanged?.Invoke(core.CanGoBack);
        ForwardPossibilityChanged?.Invoke(core.CanGoForward);
    }

    /// <summary>Splits the two loader failure classes the T4 acceptance smoke must tell apart:
    /// a missing Evergreen runtime (CoreWebView2Environment.CreateAsync's typed exception) from
    /// a failed native-loader load under PublishSingleFile (Dll/FileNotFound). The sentences are
    /// user-facing (the coordinator shows the reason in the address bar), so they are keyed in
    /// the overlays surface, not hard-coded.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        WebView2RuntimeNotFoundException => StringTable.Current.T("overlays.browse.failed.evergreen"),
        DllNotFoundException => StringTable.Current.T("overlays.browse.failed.loader"),
        FileNotFoundException => StringTable.Current.T("overlays.browse.failed.loader"),
        _ => StringTable.Current.T("overlays.browse.failed.initialize", ex.Message),
    };

    public void Dispose()
    {
        if (_core is not null)
        {
            _core.WebMessageReceived -= OnWebMessageReceived;
            _core.NavigationCompleted -= OnNavigationCompleted;
            _core.HistoryChanged -= OnHistoryChanged;
        }

        _core = null;
        _control.Dispose();
    }
}
