namespace EyeCtrl.Core.Keyboard;

using System;
using System.Collections.Generic;

/// <summary>
/// Classifies keyboard key ids by what an accidental activation costs. An <b>app-escaping</b> key
/// emits an effect the application cannot undo locally — it submits a form, dismisses a dialog,
/// destroys a selection, or invokes an app-defined function key — whereas a misfired letter or
/// navigation key costs one correcting press that the user can see and make. The class exists so the
/// keyboard can hold a dwell floor under those keys (<c>KeyboardViewModel.KeyDwellMsFor</c>) and keep
/// the DwellTime knob's forgiving direction, which is SHORTER, from trading their safety away.
/// <para>
/// The class is deliberately not "every special-action key". The navigation keys — arrows, Home,
/// End, PageUp, PageDown — are pressed in runs, and a wrong one is undone by pressing the opposite
/// key once; exempting them from the forgiving response would deny faster dwell exactly where speed
/// matters most, for a user whose scarce resource is steadiness. <c>space</c> and
/// <c>backspace</c>/<c>deleteWord</c> compose or correct text; the <c>switch*</c>/<c>modifier:*</c>/
/// <c>toggle:*</c> keys stay inside this app and are reversible. What remains is the set below.
/// </para>
/// <para>
/// Classification is by the key's dwell-target id, because for every special-action key the id IS
/// the <see cref="KeyDefinition.SpecialAction"/> string: <c>KeyboardWindow.RebuildKeys</c> mints
/// <c>key.SpecialAction</c> as the id and only falls back to a positional id for regular keys. The
/// ids in flight are the RAW CASED layout strings (<c>key:F1</c>, <c>key:ArrowUp</c>,
/// <c>shortcut:Ctrl+A</c>) — the View lowercases only for its own dispatch — so this classifier is
/// case-insensitive and its tests use the real cased ids. A lowercase-only match would silently
/// classify nothing, with the suite green.
/// </para>
/// <para>
/// The convention is now load-bearing for safety, not just telemetry: <c>KeyboardGateProbeTests</c>
/// pins that the window registers special keys under their <c>SpecialAction</c>, because collapsing
/// the id to a positional form would un-class every escaping key and evaporate the floor silently.
/// </para>
/// </summary>
public static class KeyboardSpecialActions
{
    /// <summary>
    /// The named <c>key:</c> actions that leave or destroy state: Escape and Delete, plus F1–F12.
    /// Navigation names (arrows, Home/End, PageUp/PageDown) are deliberately absent — see the class
    /// remarks. Insert and PrintScreen are absent too: Insert is a toggle a second press reverses,
    /// and PrintScreen has no destructive effect.
    /// </summary>
    private static readonly HashSet<string> EscapingNamedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Escape",
        "Delete",
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
    };

    /// <summary>
    /// Absolute policy floor above the configured baseline, in milliseconds — <b>deliberately 0 in
    /// this change</b>, which makes the floor exactly the user's configured
    /// <c>KeyboardKeyDwellMs</c> (450 today). Exempting the escaping keys from the forgiving knob is
    /// the gate; raising their value to 600 is the separable <em>should</em>, and it is a live
    /// behavior change (a slower keyboard on the very next build) that this ticket must not take
    /// silently.
    /// <para>
    /// 600 remains the recorded candidate: every other consequential action uses 600–700 ms (menus,
    /// drag, scroll, edge buttons 600; activation button and toolbar 700), so 600 would make the
    /// keyboard internally consistent. The counter-argument is equally on record: the accidental rate
    /// at 450 is unmeasured and probably low (space and enter are 18.7 sigma apart at the measured
    /// within-fixation noise), and a longer hold is a real cost to a user whose steady gaze is their
    /// scarce resource. Setting this constant to 600 is the entire policy change — the composition is
    /// <c>max(composed, max(configuredBaseline, this))</c>, so a user's larger setting is never
    /// shortened, and this also becomes the instrument for the gate's other action: lowering the
    /// global key dwell.
    /// </para>
    /// </summary>
    public const int AppEscapingPolicyFloorMs = 0;

    /// <summary>
    /// Whether an accidental activation of <paramref name="keyId"/> can leave the application or
    /// destroy work (see the class remarks for the class definition and why it is by id).
    /// </summary>
    /// <param name="keyId">The key's dwell-target id — the cased <c>SpecialAction</c> for special keys, or null.</param>
    /// <returns>True when the key belongs to the app-escaping class and must never dwell below its floor.</returns>
    public static bool IsAppEscaping(string? keyId)
    {
        if (string.IsNullOrEmpty(keyId))
            return false;
        if (keyId.Equals("enter", StringComparison.OrdinalIgnoreCase)
            || keyId.Equals("tab", StringComparison.OrdinalIgnoreCase)
            || keyId.StartsWith("shortcut:", StringComparison.OrdinalIgnoreCase))
            return true;

        return keyId.StartsWith("key:", StringComparison.OrdinalIgnoreCase)
            && EscapingNamedActions.Contains(keyId[4..]);
    }
}
