namespace EyeCtrl.App.Localization;

using EyeCtrl.Core.Localization;

/// <summary>
/// The overlays surface's host-side text sources for the App's composed surfaces. The Settings
/// surfaces own their own <c>settings</c>-keyed twins (D3); this file is the overlays side, and
/// every key is written literally at its call site so the coverage and single-surface gates see it.
/// </summary>
/// <remarks>
/// The ring's own source lives in Core (<see cref="EyeCtrl.Core.ActionMenu.RingLabelSource.Overlays"/>)
/// because both the live ring and the modes menu take it and a ViewModels-layer host cannot reference
/// the App. What is left here is the quick-actions grid's preset labels.
/// </remarks>
internal static class OverlayTextSources
{
    /// <summary>
    /// The grid tile's preset-id to label function. An id this surface has no key for returns null,
    /// so <c>QuickActionPresets.DisplayLabel</c> falls back to the tile's stored label — a stale or
    /// unknown id can never blank a tile.
    /// </summary>
    /// <param name="presetId">The preset id stored on the tile.</param>
    /// <returns>The overlays label, or null for an unknown id.</returns>
    public static string? PresetLabelFor(string presetId) => presetId switch
    {
        "kbd.copy" => T("overlays.preset.kbd.copy"),
        "kbd.paste" => T("overlays.preset.kbd.paste"),
        "kbd.cut" => T("overlays.preset.kbd.cut"),
        "kbd.undo" => T("overlays.preset.kbd.undo"),
        "kbd.redo" => T("overlays.preset.kbd.redo"),
        "kbd.select_all" => T("overlays.preset.kbd.select_all"),
        "kbd.save" => T("overlays.preset.kbd.save"),
        "kbd.find" => T("overlays.preset.kbd.find"),
        "kbd.new" => T("overlays.preset.kbd.new"),
        "kbd.print" => T("overlays.preset.kbd.print"),
        "kbd.switch_app" => T("overlays.preset.kbd.switch_app"),
        "kbd.show_desktop" => T("overlays.preset.kbd.show_desktop"),
        "kbd.lock" => T("overlays.preset.kbd.lock"),
        "kbd.file_explorer" => T("overlays.preset.kbd.file_explorer"),
        "media.play_pause" => T("overlays.preset.media.play_pause"),
        "media.stop" => T("overlays.preset.media.stop"),
        "media.next" => T("overlays.preset.media.next"),
        "media.previous" => T("overlays.preset.media.previous"),
        "media.volume_up" => T("overlays.preset.media.volume_up"),
        "media.volume_down" => T("overlays.preset.media.volume_down"),
        "media.mute" => T("overlays.preset.media.mute"),
        "window.minimize" => T("overlays.preset.window.minimize"),
        "window.maximize_restore" => T("overlays.preset.window.maximize_restore"),
        "window.close" => T("overlays.preset.window.close"),
        _ => null,
    };

    private static string T(string key) => StringTable.Current.T(key);
}
