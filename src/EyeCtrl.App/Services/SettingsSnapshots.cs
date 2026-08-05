namespace EyeCtrl.App.Services;

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
    int DwellCooldownMs)
{
    public static KeyboardStackSnapshot Capture(EyeCtrlSettings s) => new(
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
        s.Dwell.DwellCooldownMs);
}

public sealed record ToolbarSnapshot(double Size, bool IsDocked, string DockedEdge, int ToolbarButtonDwellMs, bool ShowWindowActions)
{
    public static ToolbarSnapshot Capture(EyeCtrlSettings s) =>
        new(s.Toolbar.Size, s.Toolbar.IsDocked, s.Toolbar.DockedEdge, s.Dwell.ToolbarButtonDwellMs, s.Toolbar.ShowWindowActions);
}
