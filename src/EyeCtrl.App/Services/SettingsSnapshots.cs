namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;

public sealed record KeyboardStackSnapshot(
    string ActiveLayout,
    double KeySizeMultiplier,
    bool PredictionEnabled,
    string LanguagePack,
    string ScreenEdge,
    int EmojiSkinTone,
    bool ClickSoundEnabled,
    bool DecayEnabled,
    int DecayHalfLifeDays,
    bool MLSuggestionsEnabled,
    string MLModelSize,
    bool PreferGPU,
    int KeyboardKeyDwellMs,
    int KeyboardPredictionDwellMs,
    int DwellCooldownMs,
    bool ActiveModelOnDisk)
{
    /// <summary>
    /// Captures every input the prediction stack is rebuilt from: the settings above, plus whether
    /// the file <c>MLModelSize</c> names is on disk — the one input that is not a setting. It is
    /// what makes the reload able to see a completed download of the model the setting ALREADY
    /// names: no setting changes, but the files appear.
    /// </summary>
    /// <param name="models">
    /// The registry the active model's file presence is read from. REQUIRED — not an optional
    /// parameter — so a new capture site cannot silently take a default and disagree with the
    /// other sites; the term is computed in exactly this one place for the same reason.
    /// <para>
    /// Reads the files LIVE through <see cref="ModelRegistryService.IsModelDownloaded"/> and never
    /// <see cref="ModelRegistryEntry.IsDownloaded"/>: the cached flag is only refreshed by the
    /// <c>DownloadedModels</c>/<c>SelectableModels</c> getters, so at the apply a download
    /// triggers it still reads false and a term built on it would be inert exactly where this
    /// term is needed.
    /// </para>
    /// <para>
    /// The term is not download-specific: it is plain disk state, so an out-of-band appearance or
    /// removal of the active model's files (a manual copy, a deletion outside the app) is caught
    /// by whichever settings apply comes next. There is deliberately no watcher — the reload it
    /// triggers loads the model, so the only acceptable trigger is something the user did.
    /// </para>
    /// <para>
    /// Known and accepted: with ML suggestions off the term can still flip and trigger a full
    /// reload that cannot change the prediction result (<c>PredictionStackFactory.Build</c> only
    /// reads the model files when <c>MLSuggestionsEnabled</c> is true). Gating the term on that
    /// setting was declined — it would make the term lie about the filesystem and couple the disk
    /// fact to how the stack happens to use it. Recorded so it is not rediscovered as a bug.
    /// </para>
    /// </param>
    public static KeyboardStackSnapshot Capture(EyeCtrlSettings s, ModelRegistryService? models) => new(
        s.Keyboard.ActiveLayout,
        s.Keyboard.KeySizeMultiplier,
        s.Keyboard.PredictionEnabled,
        s.Keyboard.LanguagePack,
        s.Keyboard.ScreenEdge,
        s.Keyboard.EmojiSkinTone,
        s.Keyboard.ClickSoundEnabled,
        s.Keyboard.DecayEnabled,
        s.Keyboard.DecayHalfLifeDays,
        s.Keyboard.MLSuggestionsEnabled,
        s.Keyboard.MLModelSize,
        s.Keyboard.PreferGPU,
        s.Dwell.KeyboardKeyDwellMs,
        s.Dwell.KeyboardPredictionDwellMs,
        s.Dwell.DwellCooldownMs,
        IsActiveModelOnDisk(s, models));

    private static bool IsActiveModelOnDisk(EyeCtrlSettings s, ModelRegistryService? models)
    {
        // Empty and "Off" are the two spellings of "no model", and neither names a file:
        // checking them here keeps the term's meaning "the active model's files are present"
        // even when the settings say there is no active model.
        var id = s.Keyboard.MLModelSize;
        return models is not null
            && !string.IsNullOrEmpty(id)
            && !string.Equals(id, "Off", StringComparison.OrdinalIgnoreCase)
            && models.IsModelDownloaded(id);
    }
}

public sealed record ToolbarSnapshot(double Size, bool IsDocked, string DockedEdge, int ToolbarButtonDwellMs)
{
    public static ToolbarSnapshot Capture(EyeCtrlSettings s) =>
        new(s.Toolbar.Size, s.Toolbar.IsDocked, s.Toolbar.DockedEdge, s.Dwell.ToolbarButtonDwellMs);
}
