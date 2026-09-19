namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.App.Services;

/// <summary>
/// The Models tab. Dynamic: content depends on the live
/// <see cref="SettingsPageContext.Registry"/> (download state, active selection) and on the
/// <see cref="ModelsView"/> state machine (list vs manage), which navigates via
/// <see cref="GoTo"/> + <see cref="SettingsPageContext.RebuildContent"/> — never page turns,
/// because <see cref="PrefersAutoPagination"/> is true (one row per registry entry is unbounded)
/// and page boundaries are re-split by height, so they cannot carry navigation semantics.
/// <see cref="ResetView"/> is called by the shell on every tab switch so re-entering Models lands
/// on the list view. Owns the two async download flows
/// (<see cref="DownloadBundledAsync"/> / <see cref="DownloadCustomFromClipboard"/>)
/// that previously lived in the shell; completion callbacks are marshalled back through
/// <see cref="SettingsPageContext.Invoke"/> (Dispatcher) so the source stays free of WPF coupling,
/// and the clipboard is read through <see cref="SettingsPageContext.ReadClipboard"/> for the same
/// reason and so the invalid-URL/downloading branches are headlessly testable.
/// </summary>
internal sealed class ModelsPageSource : ISettingsPageSource
{
    private enum ModelsView { List, Manage }

    private readonly SettingsPageContext _ctx;
    private ModelsView _view = ModelsView.List;
    public ModelsPageSource(SettingsPageContext ctx)
    {
        _ctx = ctx;
        // Live download progress (2026-08-17: downloads ran invisibly and read as broken).
        // Progress arrives at ~2 Hz on a worker thread; rebuild only when the user is actually
        // looking at this tab. Null-guarded: some headless contexts build without services.
        if (_ctx.ModelManagement is not null)
            _ctx.ModelManagement.DownloadProgressChanged += OnDownloadProgress;
    }

    /// <summary>
    /// Unsubscribes the progress handler. The window is constructed per open while the
    /// management service is a singleton — without this, every settings visit would add
    /// another subscription to the surviving service. Called from SettingsWindow.Closed.
    /// </summary>
    internal void Detach()
    {
        if (_ctx.ModelManagement is not null)
            _ctx.ModelManagement.DownloadProgressChanged -= OnDownloadProgress;
    }

    private void OnDownloadProgress()
    {
        if (_ctx.ViewModel?.Settings.CurrentTabName == "Models")
            _ctx.Invoke(_ctx.RebuildContent);
    }

    /// <summary>Models pages are unbounded (one row per registry entry) — the height chunker absorbs them.</summary>
    public bool PrefersAutoPagination => true;

    /// <summary>Called by the shell when the user switches tabs, so re-entering Models lands on the list.</summary>
    public void ResetView() => _view = ModelsView.List;

    private void GoTo(ModelsView view)
    {
        _view = view;
        _ctx.RebuildContent();
    }

    public IReadOnlyList<PageSpec> Build()
    {
        var manageable = _ctx.Registry.DownloadedModels;

        // Manage view: guarded on manageable.Count > 0 so deleting the last model (whose confirm
        // callback rebuilds) falls through to the list instead of an empty management page.
        if (_view == ModelsView.Manage && manageable.Count > 0)
        {
            var manageRows = new List<SettingsRowSpec>();
            foreach (var entry in manageable)
            {
                var e3 = entry;
                manageRows.Add(new ButtonSpec($"md_del_{entry.Id}",
                    $"{entry.DisplayName}  {FormatSize(entry.SizeBytes)}  \uD83D\uDDD1 Delete",
                    () => _ctx.ShowModal($"Delete {e3.DisplayName}? This cannot be undone.", () =>
                    {
                        _ctx.ModelManagement.DeleteModel(e3.Id);
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Delete, Width: 544, Height: 100));
            }
            manageRows.Add(new ButtonSpec("md_back", "Back to models",
                () => GoTo(ModelsView.List), ButtonKind.Default, Width: 300, Height: 100));
            return [new PageSpec(manageRows)];
        }

        var page1 = new List<SettingsRowSpec>();
        var downloaded = _ctx.Registry.SelectableModels;

        if (downloaded.Count == 0)
        {
            page1.Add(new LabelSpec(() => "No models downloaded. Download one below."));
        }
        else
        {
            var display = downloaded.Select(m => m.DisplayName).ToArray();
            var ids = downloaded.Select(m => m.Id).ToArray();
            page1.Add(new ComboBoxSpec("md_active", "Active Model",
                display.Prepend("Off").ToArray(), ids.Prepend("Off").ToArray(),
                () => _ctx.Registry.ActiveModelId ?? "Off",
                v => { _ctx.ModelManagement.SetActiveModel(v); _ctx.ApplyAndSave(); })
                { Description = "Which model powers predictions; Off keeps dictionary words." });
        }

        page1.Add(new LabelSpec(() => new string('\u2500', 50), Wrap: false));

        foreach (var entry in _ctx.Registry.Registry.Values.OrderBy(e => e.SizeBytes))
        {
            if (_ctx.Registry.IsModelDownloaded(entry.Id))
            {
                var e1 = entry;
                page1.Add(new LabelSpec(() => $"  {e1.DisplayName}  {FormatSize(e1.SizeBytes)}  \u2713 Downloaded"));
            }
            else
            {
                var e2 = entry;
                // In flight: a live progress line instead of the button — the download IS
                // running, and a button that stays "[Download]" with nothing happening is what
                // read as "models do not download". The label re-evaluates on every
                // progress-driven rebuild (~2 Hz, see OnDownloadProgress).
                if (e2.Id == _ctx.ModelManagement.CurrentDownloadId)
                {
                    page1.Add(new LabelSpec(() =>
                        $"⇣ {e2.DisplayName}  downloading… {_ctx.ModelManagement.CurrentDownloadProgress:P0}"));
                    continue;
                }
                // 544 = the widest the 560 DIP content column holds (ConstrainToContentWidth
                // clamps on narrower displays): one line for every bundled label except the
                // GPU one, which wraps to two centered lines rather than clipping.
                page1.Add(new ButtonSpec($"md_dl_{entry.Id}",
                    $"{entry.DisplayName}  {FormatSize(entry.SizeBytes)}  [Download]",
                    () =>
                    {
                        // Not silent: a busy workflow that swallows the dwell is
                        // indistinguishable from a broken button.
                        if (_ctx.ModelManagement.IsDownloading)
                        {
                            _ctx.ShowError("Another download is already in progress.");
                            return;
                        }
                        _ctx.ShowModal($"Download {e2.DisplayName} ({FormatSize(e2.SizeBytes)})?",
                            () => _ = DownloadBundledAsync(e2.Id, e2.Url, e2.Sha256, e2.TokenizerUrl, e2.TokenizerSha256));
                    },
                    ButtonKind.Default, Width: 544, Height: 100));
            }
        }

        page1.Add(new LabelSpec(() => new string('\u2500', 50), Wrap: false));
        page1.Add(new ButtonSpec("md_add", "\uff0b Add Custom Model (paste URL)",
            DownloadCustomFromClipboard, ButtonKind.Default, Width: 440, Height: 100));

        if (manageable.Count > 0)
        {
            page1.Add(new ButtonSpec("md_manage", "Manage models...",
                () => GoTo(ModelsView.Manage), ButtonKind.Default, Width: 300, Height: 100));
        }

        return [new PageSpec(page1)];
    }

    private async Task DownloadBundledAsync(string id, string url, string sha256, string tokUrl, string tokSha256)
    {
        try
        {
            var result = await _ctx.ModelManagement.DownloadBundledModelAsync(id, url, sha256, tokUrl, tokSha256);
            _ctx.Invoke(() =>
            {
                if (!result.IsSuccess) _ctx.ShowError(result.Error ?? "Download failed.");
                // ApplyAndSave persists and raises SettingsChanged -> ReloadIfChanged, which
                // rebuilds the prediction stack when the captured stack inputs changed. One of
                // those inputs is NOT a setting: KeyboardStackSnapshot carries whether the file
                // MLModelSize names is on disk. Downloading the model the setting ALREADY names
                // changes no setting but does flip that term from absent to present, so the
                // reload fires and the LM goes live without a restart (ticket 11). Without the
                // disk term the snapshot stayed equal here and the rebuild was suppressed —
                // green service test, feature dead until restart.
                else _ctx.ApplyAndSave();
                _ctx.RebuildContent();
            });
        }
        catch (Exception ex)
        {
            _ctx.Invoke(() =>
            {
                _ctx.ShowError($"Download failed: {ex.Message}");
                _ctx.RebuildContent();
            });
        }
    }

    private void DownloadCustomFromClipboard()
    {
        var url = (_ctx.ReadClipboard() ?? "").Trim();
        if (string.IsNullOrEmpty(url) || !url.StartsWith("https://"))
        {
            _ctx.ShowError("Copy a valid HTTPS model URL to clipboard first, then dwell this button.");
            return;
        }
        if (_ctx.ModelManagement.IsDownloading)
        {
            _ctx.ShowError("Another download is already in progress.");
            return;
        }
        var displayName = ModelManagementService.DeriveModelName(url);
        _ctx.ShowModal($"Download custom model \"{displayName}\" from clipboard URL?", () =>
        {
            _ = Task.Run(async () =>
            {
                // try/catch mirrors DownloadBundledAsync above, and for the same reason. This is a
                // fire-and-forget Task.Run: an escaping exception is not merely unhandled, it is
                // UNOBSERVED. App.xaml.cs registers a TaskScheduler.UnobservedTaskException handler
                // that logs and marks it observed, so the process survives — and the user, who
                // dwelled a button and is waiting, is told nothing at all. Silent failure on a
                // user-initiated action is the worst of the available outcomes.
                //
                // DownloadCustomModelAsync converts its own expected failures into a Failed result
                // rather than throwing, so this is defence in depth against the unexpected
                // (ObjectDisposed on the workflow gate, OOM on a large model) — not a routine path.
                try
                {
                    var result = await _ctx.ModelManagement.DownloadCustomModelAsync(url);
                    _ctx.Invoke(() =>
                    {
                        if (!result.IsSuccess) _ctx.ShowError(result.Error ?? "Download failed.");
                        else _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    });
                }
                catch (Exception ex)
                {
                    _ctx.Invoke(() =>
                    {
                        _ctx.ShowError($"Download failed: {ex.Message}");
                        _ctx.RebuildContent();
                    });
                }
            });
        });
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576:F0} MB";
        if (bytes >= 1024) return $"{bytes / 1024:F0} KB";
        return $"{bytes} B";
    }
}
